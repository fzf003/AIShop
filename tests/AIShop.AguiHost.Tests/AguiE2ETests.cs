using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Core.StaticData;
using AIShop.Service;
using AIShop.Service.Agui;
using Mem0Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Meai = Microsoft.Extensions.AI;
using Xunit.Sdk;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本集合为 mock-LLM E2E 回归（T16）：复跑验收 2/3（对应 tasks「### T16」），经真实 AG-UI wire
/// （WAF POST "/" RunAgentInput → SSE 事件流）+ 真实工具执行（CartToolProvider/EF/RAG 底座）+ 真实 DB 副作用断言，
/// 不调真实模型。置 DisableParallelization 串行集合（多宿主串行迁移/写 SQLite，同 AguiRequestTests 约定；
/// 本集合环境变量 seam 亦与并发测试互斥）。
/// </summary>
[CollectionDefinition(nameof(AguiE2ETests), DisableParallelization = true)]
public sealed class AguiE2ETestsCollection;

/// <summary>
/// T16 mock-LLM E2E：用 <see cref="MockToolChatClient"/>（脚本化工具 mock，见 tasks 实施期确认项 ⑩/⑪——
/// mock 双入口共享状态机按实测收敛；FICC 工具迭代走内层，SSE 出口经 streaming）驱动验收 2/3：
///   场景 A（验收 2）：mock 阶段1 产 <c>search_product</c>{keyword:跑步鞋}、阶段2 文本含 E2E-MARKER-A →
///     POST username=fzf003「推荐跑步鞋」→ 断言 SSE 流式含 marker + mock 阶段2 输入含 search_product 的
///     FunctionResultContent（真实 RAG 语义检索命中回填，见 bge 前置）；
///   场景 B（验收 3）：同库同 Thread 续「把第一个加购物车」→ mock 产 <c>add_to_cart</c>{3,1}，真实工具以 fzf003
///     落库 → 断言 SSE 含 E2E-MARKER-B + mock 输入含 add_to_cart 成功文本「已添加 专业跑鞋」+ SQLite 直查
///     临时业务库 fzf003 Carts/CartItems 出现 ProductId=3、Quantity=1 行。
/// 隔离：Program.cs T16 seam 读可选配置键 <c>Agui:DbConnection</c> / <c>Agui:RagConnection</c> / <c>Agui:SessionConnection</c>
/// （实测 ConfigureAppConfiguration 不达 Program 顶层读取，故经同名环境变量 <c>Agui__*Connection</c> 注入临时库路径——
/// WebApplicationBuilder 在 CreateBuilder 阶段读环境变量，早于 Program 顶层读取 seam；缺省键行为零变化由既有测试回回归）。
/// 另 RemoveAll Mem0 记忆服务（IMemoryService）——记忆链路会用全局纯净 IChatClient（= stub 工厂脚本化 mock）
/// 做 LLM 提取，避免 mock 被非 Agent 调用路径污染（记忆非验收 2/3 范围）。IMemoryStore/SqliteMemoryStore **不**移除：
/// 它们无 LLM 调用，且 T7 起 recommend_products 工具依赖 IMemoryStore（移除会让 keyed agent 工厂解析失败）。
/// </summary>
/// <remarks>
/// 盘点 T3 补入两条「失败形态」用例（<see cref="NullToolArguments_MimoForm_ToolStillRuns_AndHistoryKeepsPairedToolResult"/>
/// 与 <see cref="ToolExecutionFailure_MissingRequiredArguments_RoundStillFinishes_AndFailureIsSurfaced"/>）：
/// 脚本化 mock 经 <c>MockToolChatClient.ToolScript.NullArguments</c> 可产出真机 Mimo 形态
/// （<c>FunctionCallContent.Arguments == null</c>），另有「缺必填实参 → 工具执行抛异常」形态。
/// 两条用例走真实装配链（含 <c>ReplySanitizingChatClient</c> 的 C3 规范化与 FICC 工具循环），
/// 断言本轮收尾、工具被调用而非丢弃、落库历史工具调用与结果配对（无孤儿调用）。
/// </remarks>
[Collection(nameof(AguiE2ETests))]
public sealed class AguiE2ETests : IDisposable
{
    private const string SearchTool = "search_product";
    private const string AddToCartTool = "add_to_cart";
    /** 购物车汇总工具（G1 端到端用例用；与 `CartToolProvider` 注册的工具名一致）。 */
    private const string CartSummaryTool = "get_cart_summary";

    /** AG-UI wire 帧类型：轮次终点（关闭盘点 T3「链路不炸」判据）。 */
    private const string RunFinishedType = "RUN_FINISHED";

    /** AG-UI wire 帧类型：轮次错误（盘点 T3 断言其不出现）。 */
    private const string RunErrorType = "RUN_ERROR";

    /** AG-UI wire 帧类型：工具调用实参增量（承载 tool_call 的 arguments，C3 规范化的观测面）。 */
    private const string ToolCallArgsType = "TOOL_CALL_ARGS";

    private readonly List<string> _cleanupPaths = [];
    private readonly List<(string Key, string? Prev)> _envRestore = [];

    /// <summary>当前测试临时业务库连接串（StartFactory 写入，供落库断言直查；每测试独立实例）。</summary>
    private string _businessConnection = "";

    /// <summary>当前测试临时聊天历史库连接串（仅 <c>isolateChatDb: true</c> 时写入，供历史配对断言直查）。</summary>
    private string _chatConnection = "";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // 恢复环境变量（host 构建完成后即恢复；残留会污染同进程后续测试的 Program 顶层 seam 读取）
        foreach (var (key, prev) in _envRestore)
            Environment.SetEnvironmentVariable(key, prev);
        _envRestore.Clear();

        // 删除临时库目录（业务/向量/会话库各独立文件）
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略，交由系统清理
            }
        }
        _cleanupPaths.Clear();
    }

    [Fact]
    public async Task Search_WithScriptedLlm_SseStreamsText_AndSearchProductRealHit()
    {
        // 场景 A（验收 2 / spec「SSE 流式回复并触发 search_product（推荐链路）」）：mock 阶段1 = search_product 工具调用、
        // 阶段2 = 含 E2E-MARKER-A 的最终文本；真实工具经真实 CartToolProvider + EF/RAG 底座执行（命中种子商品 3 专业跑鞋）。
        var mock = new MockToolChatClient(
            new MockToolChatClient.ToolScript(
                ToolName: SearchTool,
                Arguments: new Dictionary<string, object?> { ["keyword"] = "跑步鞋" },
                FinalText: "已为您找到跑步鞋相关商品 E2E-MARKER-A"));

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        // POST "/" AG-UI RunAgentInput（username=fzf003，消息「推荐跑步鞋」）→ SSE 事件流
        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("e2e-a-thread", username: "fzf003", userMessage: "推荐跑步鞋"), Encoding.UTF8, "application/json"));

        // 断言 HTTP 200 + SSE 流式文本收到（marker 到达 = 最终文本经 SSE 流式出口输出）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("E2E-MARKER-A", sse);

        // mock 阶段2（工具结果回填后的那次模型调用）输入应含 search_product 的 FunctionResultContent——真实工具执行回填
        var toolResultInput = Assert.Single(mock.ToolResultInputs);
        Assert.Contains(SearchTool, FunctionCallNames(toolResultInput));

        // 真命中断言：FRC 文本来自真实语义检索（bge 前置已就位），命中种子商品 #3 专业跑鞋（名称/编号由真实 RAG 检索返回，非 mock 编造）。
        // 注：bge 模型缺失的环境（干净 CI 未下载模型）语义检索返回「未找到包含…」，届时按实测降级为「真实工具已执行 + 合法结果 + 不崩溃」
        //（tasks 验收 A 注 / 实施期确认项 ⑨），bge 就绪环境保持本条真命中断言。
        var resultText = JoinedToolResults(toolResultInput);
        Assert.Contains("找到", resultText);
        Assert.Contains("#3", resultText);
        Assert.Contains("专业跑鞋", resultText);
    }

    [Fact]
    public async Task AddToCart_AfterSearch_SameThread_CartRowVisibleInAguiDb()
    {
        // 场景 B（验收 3 / spec「加购与查车链路在独立库可见」）：同库同 Thread 两轮——
        // 第一轮 mock 产 search_product（真实命中）→ 最终文本；第二轮续「把第一个加购物车」mock 产
        // add_to_cart{productId:3, quantity:1}（真实工具以 fzf003 落独立业务库）→ 最终文本含 E2E-MARKER-B。
        var mock = new MockToolChatClient(
        [
            new MockToolChatClient.ToolScript(
                ToolName: SearchTool,
                Arguments: new Dictionary<string, object?> { ["keyword"] = "跑步鞋" },
                FinalText: "已为您找到跑步鞋商品 E2E-ROUND1"),
            new MockToolChatClient.ToolScript(
                ToolName: AddToCartTool,
                Arguments: new Dictionary<string, object?> { ["productId"] = 3, ["quantity"] = 1 },
                FinalText: "已将专业跑鞋加入您的购物车 E2E-MARKER-B"),
        ]);

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        const string threadId = "e2e-b-thread";

        // 第一轮：同 Thread 起会话（真实 search_product 检索命中）
        using (var round1 = await client.PostAsync(
                   "/",
                   new StringContent(RunAgentBody(threadId, username: "fzf003", userMessage: "推荐跑步鞋"), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, round1.StatusCode);
            Assert.Contains("E2E-ROUND1", await round1.Content.ReadAsStringAsync());
        }

        // 等待第一轮会话真正落库（SaveSessionAfterStreamingAsync 在 SSE 流结束后执行，轮询避免跨请求时序竞态）。
        // 会话按用户名归属（ResolveStoreId），本轮 username=fzf003 → 落库键 AGUIShopping:fzf003。
        await WaitForSessionRowAsync(factory, username: "fzf003");

        // 显式锁定新语义：threadId 键的行【不存在】（按用户名归属，不再按 threadId）。该用例的会话库为独立临时库
        //（StartFactory 每次建临时目录），无跨用例共享，故此处无时序风险；将来谁改回 threadId 绑定，此断言变红。
        var sessionStore = factory.Services.GetService<SqliteAgentSessionStore>();
        Assert.NotNull(sessionStore);
        Assert.Equal(0, await CountSessionRowsAsync(sessionStore.ConnectionString, $"AGUIShopping:{threadId}"));

        // 第二轮：同 Thread 续聊「把第一个加购物车」→ mock 产 add_to_cart → 真实工具以 fzf003 落库
        using (var round2 = await client.PostAsync(
                   "/",
                   new StringContent(RunAgentBody(threadId, username: "fzf003", userMessage: "把第一个加购物车"), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, round2.StatusCode);
            Assert.Contains("E2E-MARKER-B", await round2.Content.ReadAsStringAsync());
        }

        // mock 第二轮（工具结果回填后那次调用）输入应含 add_to_cart 的 FunctionResultContent 成功文本「已添加 专业跑鞋」
        Assert.Equal(2, mock.ToolResultInputs.Count);
        var secondToolResult = mock.ToolResultInputs[1];
        Assert.Contains(AddToCartTool, FunctionCallNames(secondToolResult));
        var addResultText = JoinedToolResults(secondToolResult);
        Assert.Contains("已添加 专业跑鞋", addResultText);

        // SQLite 直查该测试临时业务库：fzf003（Users.Username=fzf003）关联的 Carts/CartItems 出现 ProductId=3、Quantity=1 行
        //（验收 3「SQLite agui.db 可见」；factory 先释放避免连接池持锁，再清池后开独立连接直查）
        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await AssertCartRowAsync(_businessConnection, username: "fzf003", productId: 3, quantity: 1);
    }

    [Fact]
    public async Task RestAddToCart_ThenAguiGetCartSummary_SeesSameItemFromSameRepository()
    {
        // spec〈客户端支撑端点复用既有仓储〉场景 2「与 AI 工具写同一份数据」——本变更的核心承诺：
        // 购物车只有一份数据（同一 ICartRepository + 同一张 Carts/CartItems 表）。流程 = 先经 REST 加购，
        // 再以【同一用户名】经 AG-UI 让模型调 get_cart_summary（真实 CartToolProvider 读同一仓储），
        // 断言工具结果里能看到刚才 REST 加的那件商品。REST 面与工具面共享仓储、不共享工具层包装。
        var product = ProductSeedData.Products[0];
        const string username = "marla";

        var mock = new MockToolChatClient(
            new MockToolChatClient.ToolScript(
                ToolName: CartSummaryTool,
                Arguments: new Dictionary<string, object?>(),
                FinalText: "这是您的购物车 E2E-MARKER-C"));

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        // ① REST 加购（正向锚点：真的写成功——200 且返回体已含新条目，否则后续断言无意义）
        using (var added = await client.PostAsJsonAsync(
                   $"/cart/items?username={username}",
                   new { productId = product.Id, quantity = 2 }))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var addBody = await added.Content.ReadAsStringAsync();
            Assert.Contains($"\"productId\":{product.Id}", addBody);
            Assert.Contains(product.Name, addBody);
        }

        // ② 同一用户名经 AG-UI 触发 get_cart_summary（脚本化模型产 FCC → FICC 执行真实工具 → 回填结果）
        using var response = await client.PostAsync(
            "/",
            new StringContent(
                RunAgentBody("e2e-rest-thread", username: username, userMessage: "我的购物车里有什么？"),
                Encoding.UTF8,
                "application/json"));

        // 正向锚点：整条链真的跑通（SSE 收到脚本最终文本）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("E2E-MARKER-C", await response.Content.ReadAsStringAsync());

        // ③ 工具结果 = 真实 CartToolProvider.GetCartSummaryAsync 文本 → 必须含 REST 加购的那件商品
        //    （名称 + 条目行里的 ProductId 段 + 数量；证明 AI 工具读到的是同一份数据，而非空车）
        var toolResultInput = Assert.Single(mock.ToolResultInputs);
        Assert.Contains(CartSummaryTool, FunctionCallNames(toolResultInput));
        var summary = JoinedToolResults(toolResultInput);
        Assert.Contains(product.Name, summary);
        Assert.Contains($"-{product.Id}-", summary);
        Assert.Contains("x2", summary);
        Assert.Contains("共 2 件商品", summary);
    }

    [Fact]
    public async Task NullToolArguments_MimoForm_ToolStillRuns_AndHistoryKeepsPairedToolResult()
    {
        // 盘点 T3（正对 Mimo 事故形态）：模型产出的 tool_call 在 wire 上 arguments 为字符串 "null"，
        // 解析到 MEAI 后即 FunctionCallContent.Arguments == null。本用例经【真实装配】
        // （Program keyed factory → RecommendationPushAgent → ChatClientAgent → FICC → ReplySanitizingChatClient
        // 的 C3 规范化 → RouterChatClient → 脚本化 mock）跑完整一轮，断言三件事：
        //   ① 链路不炸（本轮正常收尾）；② 工具被调用而非被丢弃；③ 历史不毒化（工具调用有配对 tool 结果）。
        // 工具选零必填参的 get_cart_summary：这样「工具确实被执行」有一个只有真执行才可能出现的证据
        //（车里那件商品的名字），而不是「没抛异常」这种弱判据。
        var product = ProductSeedData.Products[0];
        const string username = "fzf003";
        const string finalText = "您的购物车情况如下 E2E-MARKER-M";

        var mock = new MockToolChatClient(
            new MockToolChatClient.ToolScript(
                ToolName: CartSummaryTool,
                Arguments: new Dictionary<string, object?>(),
                FinalText: finalText)
            {
                // Mimo 形态：本段工具调用的 Arguments 置为 null（真机 wire 上 arguments = "null"）
                NullArguments = true,
            });

        using var factory = StartFactory(mock, isolateChatDb: true);
        using var client = factory.CreateClient();

        // 正向锚点（**先于** AG-UI 轮）：经 REST 把一件商品放进该用户购物车——
        // 这条只有「真实工具确实执行」才可能出现在工具结果里（工具被丢弃则该证据根本无从产生）。
        using (var added = await client.PostAsJsonAsync(
                   $"/cart/items?username={username}",
                   new { productId = product.Id, quantity = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Contains($"\"productId\":{product.Id}", await added.Content.ReadAsStringAsync());
        }

        using var response = await client.PostAsync(
            "/",
            new StringContent(
                RunAgentBody("e2e-mimo-form", username, "我的购物车里有什么？"),
                Encoding.UTF8,
                "application/json"));

        // ① 链路不炸：本轮正常收尾——最终文本送达 + 有 RUN_FINISHED 终止帧 + 无 RUN_ERROR
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains(finalText, sse);
        var frames = ParseSseFrames(sse);
        Assert.Contains(RunFinishedType, FrameTypes(frames));
        Assert.DoesNotContain(RunErrorType, FrameTypes(frames));

        // ② 工具被调用（不是被丢弃）：真实工具执行结果回填进 FICC 的下一轮输入
        var toolResultInput = Assert.Single(mock.ToolResultInputs);
        Assert.Contains(CartSummaryTool, FunctionCallNames(toolResultInput));
        var summary = JoinedToolResults(toolResultInput);
        Assert.Contains(product.Name, summary);
        Assert.Contains("共 1 件商品", summary);

        // ③ 历史不毒化：assistant 的 toolCall 有配对的 FunctionResultContent（无孤儿调用）——
        // 在 in-memory 输入快照与【落库历史】两侧各钉一次。
        var callIds = AssertToolCallsPaired(toolResultInput);
        var chatRows = await ReadChatRowsAsync(_chatConnection, requiredFragment: finalText);
        AssertToolCallsPersistedPaired(chatRows, callIds);

        // ④ 边界表示（C3 规范化在真实 wire 上确实生效）：本轮 TOOL_CALL_ARGS 的 delta 是**合法 JSON 对象** {}，
        //    而非 wire 上那个字符串 "null"；rawEvent 里的 assistant 工具调用 arguments 也已是 {}。
        //    这是本用例里唯一对「规范化是否真的在装配链上」有区分力的断言（工具执行本身对 null 与 {} 等价，
        //    见 handoff-T3 反证 A）；正向锚点在先，避免「不含 null」在没产出该帧时空转。
        var argsFrame = Assert.Single(frames, frame => IsFrame(frame, ToolCallArgsType));
        Assert.Equal("{}", argsFrame.GetProperty("delta").GetString());
        Assert.Contains("\"arguments\":{}", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("\"arguments\":null", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecutionFailure_MissingRequiredArguments_RoundStillFinishes_AndFailureIsSurfaced()
    {
        // 盘点 T3（工具执行失败链路）：模型发出的 add_to_cart 调用【缺少必填参数】
        // （Arguments 为空对象 —— 与 C3 把非法实参规范化为 {} 后的形态等价，也正是真机 Mimo
        // 「工具拿不到有效实参」的等价形态）→ AIFunction 绑定必填参数失败并抛 ArgumentException。
        // 断言：本轮照常收尾（RUN_FINISHED、无 RUN_ERROR）；失败**不被静默吞掉**（作为携带 Exception 的
        // 配对工具结果回喂模型、可归因到具体缺参）；落库历史里工具调用与工具结果仍配对（无孤儿调用）。
        const string username = "fzf003";
        const string finalText = "抱歉，加购未能完成 E2E-MARKER-F";

        var mock = new MockToolChatClient(
            new MockToolChatClient.ToolScript(
                ToolName: AddToCartTool,
                // 空对象 = 未提供必填的 productId/quantity → AIFunction 参数绑定必然失败
                Arguments: new Dictionary<string, object?>(),
                FinalText: finalText));

        using var factory = StartFactory(mock, isolateChatDb: true);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(
                RunAgentBody("e2e-tool-failure", username, "把第一个加入购物车"),
                Encoding.UTF8,
                "application/json"));

        // ① 链路不炸：本轮照常收尾（工具执行失败不打断本轮，也不掩盖为 RUN_ERROR）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains(finalText, sse);
        var frames = ParseSseFrames(sse);
        Assert.Contains(RunFinishedType, FrameTypes(frames));
        Assert.DoesNotContain(RunErrorType, FrameTypes(frames));

        // ② 失败未静默吞掉：该工具调用拿到的是【携带异常的】FunctionResultContent（错误被记录并回喂模型）
        var toolResultInput = Assert.Single(mock.ToolResultInputs);
        Assert.Contains(AddToCartTool, FunctionCallNames(toolResultInput));
        var failure = Assert.Single(toolResultInput
            .SelectMany(message => message.Contents)
            .OfType<Meai.FunctionResultContent>());
        var exception = Assert.IsType<ArgumentException>(failure.Exception);
        // 可归因：异常直指缺失的必填参数（不是被折叠成不透明的失败）
        Assert.Contains("productId", exception.Message, StringComparison.Ordinal);

        // ③ 历史一致：工具调用与 tool 结果仍配对（无孤儿调用），失败以「配对结果」的形式留在历史里
        var callIds = AssertToolCallsPaired(toolResultInput);
        var chatRows = await ReadChatRowsAsync(_chatConnection, requiredFragment: finalText);
        AssertToolCallsPersistedPaired(chatRows, callIds);
    }

    /// <summary>从输入消息快照收集 FunctionCallContent 的工具名（FRC 前的 assistant FCC 承载工具名）。</summary>
    private static IEnumerable<string> FunctionCallNames(IEnumerable<Meai.ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<Meai.FunctionCallContent>().Select(c => c.Name);

    /// <summary>拼接输入消息快照中所有 FunctionResultContent 的工具结果文本（真实工具返回，供断言命中/成功文案）。</summary>
    private static string JoinedToolResults(IEnumerable<Meai.ChatMessage> messages)
    {
        var parts = messages
            .SelectMany(m => m.Contents)
            .OfType<Meai.FunctionResultContent>()
            .Select(f => f.Result?.ToString() ?? string.Empty)
            .Where(s => s.Length > 0);
        return string.Join("\n", parts);
    }

    /// <summary>
    /// 断言输入快照里「工具调用 ↔ 工具结果」<b>一一配对</b>（无孤儿调用）：每个 assistant 的
    /// <see cref="Meai.FunctionCallContent.CallId"/> 都有同一 CallId 的 <see cref="Meai.FunctionResultContent"/>。
    /// 返回该轮的调用 CallId 集（供落库历史侧复用同一锚点，不硬编码 mock 内部计数）。
    /// </summary>
    private static string[] AssertToolCallsPaired(IEnumerable<Meai.ChatMessage> messages)
    {
        var contents = messages.SelectMany(message => message.Contents).ToList();
        var callIds = contents.OfType<Meai.FunctionCallContent>().Select(call => call.CallId).ToArray();
        var resultIds = contents.OfType<Meai.FunctionResultContent>().Select(result => result.CallId).ToArray();

        Assert.NotEmpty(callIds);
        Assert.Equal(
            callIds.OrderBy(id => id, StringComparer.Ordinal),
            resultIds.OrderBy(id => id, StringComparer.Ordinal));

        return callIds;
    }

    /// <summary>
    /// 断言【落库历史】里每个工具调用都有配对的 <c>role="tool"</c> 结果行（承载同一 CallId）。
    /// 这正是「一次坏轮污染后续所有轮」的唯一闸门：assistant 带 toolCalls 却无配对的 tool 消息时，
    /// 客户端每轮全量重发该历史会持续失败。
    /// </summary>
    private static void AssertToolCallsPersistedPaired(List<(string Role, string Json)> rows, string[] callIds)
    {
        Assert.NotEmpty(callIds);

        var toolRows = rows.Where(row => row.Role == "tool").ToArray();
        Assert.NotEmpty(toolRows);

        foreach (var callId in callIds)
            Assert.Contains(toolRows, row => row.Json.Contains(callId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 轮询直查临时聊天历史库每行 <c>(role, message_json)</c>，直到出现含 <paramref name="requiredFragment"/> 的行为止
    /// （表为懒建：未建表 / 文件不存在等同「暂无行」，继续等；超时即失败，避免下游「查了空库」式空转断言）。
    /// </summary>
    private static async Task<List<(string Role, string Json)>> ReadChatRowsAsync(
        string connectionString,
        string requiredFragment,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                await using (var exists = connection.CreateCommand())
                {
                    exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'chat_messages'";
                    if ((long)(await exists.ExecuteScalarAsync() ?? 0L) == 0)
                    {
                        await Task.Delay(200);
                        continue;
                    }
                }

                var rows = new List<(string, string)>();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT role, message_json FROM chat_messages ORDER BY sequence";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows.Add((reader.GetString(0), reader.GetString(1)));

                if (rows.Exists(row => row.Item2.Contains(requiredFragment, StringComparison.Ordinal)))
                    return rows;
            }
            catch (SqliteException)
            {
                // 宿主仍在写库（快照/WAL），短暂重试
            }

            await Task.Delay(200);
        }

        throw new XunitException($"等待聊天历史落库超时（未查到含「{requiredFragment}」的行）");
    }

    /// <summary>把 SSE 响应体拆成 JSON 帧列表（只取 <c>data:</c> 行；口径同 <c>AguiRecommendationPushTests</c>）。</summary>
    private static List<JsonElement> ParseSseFrames(string body)
    {
        var frames = new List<JsonElement>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = line["data:".Length..].Trim();
            if (json.Length == 0)
                continue;

            frames.Add(JsonDocument.Parse(json).RootElement.Clone());
        }

        return frames;
    }

    /// <summary>帧类型判定（SSE <c>data:</c> 载荷的 <c>type</c> 属性）。</summary>
    private static bool IsFrame(JsonElement frame, string type)
        => frame.TryGetProperty("type", out var property)
            && string.Equals(property.GetString(), type, StringComparison.Ordinal);

    /// <summary>帧类型序列（断言失败时的诊断信息）。</summary>
    private static string[] FrameTypes(IEnumerable<JsonElement> frames)
        => [.. frames.Select(frame => frame.TryGetProperty("type", out var type) ? type.GetString() ?? "?" : "?")];

    /// <summary>
    /// 装配 WAF：以 <see cref="MockToolChatClient"/> 作所有 modelId 的底层（<see cref="IModelChatClientFactory"/> stub，
    /// C5 seam——agent 聊天底层经 RouterChatClient → 工厂），并经 Program.cs T16 seam 环境变量注入临时业务/向量/会话库。
    /// 同时 RemoveAll Mem0 <c>IMemoryService</c>：记忆链会用全局纯净 IChatClient 做 LLM 提取，避免脚本化工具 mock
    /// 被非 Agent 路径调用污染；记忆非验收 2/3 范围，移除不改变断言语义（记忆存储为何保留见 StartFactory 内注释）。
    /// </summary>
    /// <param name="mock">脚本化模型客户端（所有 modelId 的底层）。</param>
    /// <param name="isolateChatDb">为 <c>true</c> 时把聊天历史库（<c>Agui:ChatConnection</c>）也 seam 到本测试的临时目录
    /// （供「落库历史」断言直查）；缺省 <c>false</c> 保持既有 3 条用例的装配与副作用逐字节不变。</param>
    private WebApplicationFactory<Program> StartFactory(MockToolChatClient mock, bool isolateChatDb = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupPaths.Add(dir);

        var businessConnection = $"Data Source={Path.Combine(dir, "business.db")}";
        var ragConnection = $"Data Source={Path.Combine(dir, "rag.db")}";
        var sessionConnection = $"Data Source={Path.Combine(dir, "sessions.db")}";
        _businessConnection = businessConnection;

        // Program.cs T16 seam 经环境变量注入：WebApplicationBuilder 在 CreateBuilder 读环境变量，早于 Program 顶层
        // 读取 builder.Configuration["Agui:*"]（实测 ConfigureAppConfiguration 不达顶层读取）。key 用 __ 映射 :。
        SetEnvironment("Agui__DbConnection", businessConnection);
        SetEnvironment("Agui__RagConnection", ragConnection);
        SetEnvironment("Agui__SessionConnection", sessionConnection);

        if (isolateChatDb)
        {
            _chatConnection = $"Data Source={Path.Combine(dir, "chat.db")}";
            SetEnvironment("Agui__ChatConnection", _chatConnection);
        }

        try
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IModelChatClientFactory>();
                    services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(mock));

                    // 只移除 IMemoryService（见上方注释）：它一旦被解析，AGUIShopping 就会挂 MemoryContextProvider，
                    // 记忆链会用全局纯净 IChatClient（= 脚本化 mock）做 LLM 提取 → 污染 mock。
                    // IMemoryStore / SqliteMemoryStore 保持注册：它们不做任何 LLM 调用，且 T7 起 AGUIShopping 的
                    // recommend_products 工具（RecommendationToolProvider）依赖 IMemoryStore——一并移除会让 keyed
                    // agent 工厂无法解析（Development 下 DI ValidateOnBuild 直接报错，宿主起不来）。
                    services.RemoveAll<IMemoryService>();
                });
            });

            // CreateClient() 触发 host 构建（Program 顶层读取 seam 配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
            return factory;
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    private void SetEnvironment(string key, string value)
    {
        _envRestore.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, prev) in _envRestore)
            Environment.SetEnvironmentVariable(key, prev);
        _envRestore.Clear();
    }

    /// <summary>轮询等待会话行落库（store_id = "AGUIShopping:{username}"，key 带 agent.Name 前缀 + <b>用户名</b>）。
    /// 会话归属已从 AG-UI threadId 改为当前用户名（见 <c>SqliteAgentSessionStore.ResolveStoreId</c>）——本用例走真实 DI
    /// store（含 ICurrentUserAccessor），故落库键按用户名；其它直构 store 的用例走 threadId 回退分支。</summary>
    private static async Task WaitForSessionRowAsync(WebApplicationFactory<Program> factory, string username, int timeoutMs = 10_000)
    {
        var store = factory.Services.GetService<SqliteAgentSessionStore>();
        Assert.NotNull(store);
        var storeId = $"AGUIShopping:{username}";
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await CountSessionRowsAsync(store.ConnectionString, storeId) >= 1)
                    return;
            }
            catch (SqliteException)
            {
                // 宿主仍在写库（快照/WAL），短暂重试
            }
            await Task.Delay(200);
        }
        Assert.Fail($"等待会话落库超时：store_id = {storeId}");
    }

    private static async Task<long> CountSessionRowsAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>直连临时业务库断言：指定用户关联购物车出现指定商品行（ProductId/Quantity）。</summary>
    private static async Task AssertCartRowAsync(string businessConnection, string username, int productId, int quantity)
    {
        await using var connection = new SqliteConnection(businessConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ci.ProductId, ci.Quantity
            FROM CartItems ci
            INNER JOIN Carts c ON c.Id = ci.CartId
            INNER JOIN Users u ON u.Id = c.UserId
            WHERE u.Username = $username AND ci.ProductId = $productId
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$productId", productId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"临时业务库应含 {username} 的加购行（ProductId={productId}）");
        Assert.Equal(productId, reader.GetInt32(0));
        Assert.Equal(quantity, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync(), "同一用户同一商品只应有一条加购行（AddToCartAsync 幂等）");
    }

    /// <summary>构造 AG-UI RunAgentInput 形状的请求体 JSON（username 非 null 带 forwardedProps；消息 id 每次唯一 GUID，
    /// 避免同 Thread 续聊时与已还原会话历史消息 id 重复被 ChatClientAgent 按 id 判重合并）。</summary>
    private static string RunAgentBody(string threadId, string? username, string userMessage)
    {
        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = $"m-{Guid.NewGuid():N}",
                    ["role"] = "user",
                    ["content"] = userMessage,
                }),
        };

        if (username is not null)
        {
            root["forwardedProps"] = new JsonObject
            {
                ["username"] = username,
            };
        }

        return root.ToJsonString();
    }
}
