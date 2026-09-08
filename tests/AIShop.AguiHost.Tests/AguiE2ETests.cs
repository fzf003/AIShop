using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Infrastructure.MemoryService;
using Mem0Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Meai = Microsoft.Extensions.AI;

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
/// 另 RemoveAll Mem0 记忆服务三件套（IMemoryService/IMemoryStore/SqliteMemoryStore）——记忆链路会用全局纯净
/// IChatClient（= stub 工厂脚本化 mock）做 LLM 提取，避免 mock 被非 Agent 调用路径污染（记忆非验收 2/3 范围）。
/// </summary>
[Collection(nameof(AguiE2ETests))]
public sealed class AguiE2ETests : IDisposable
{
    private const string SearchTool = "search_product";
    private const string AddToCartTool = "add_to_cart";

    private readonly List<string> _cleanupPaths = [];
    private readonly List<(string Key, string? Prev)> _envRestore = [];

    /// <summary>当前测试临时业务库连接串（StartFactory 写入，供落库断言直查；每测试独立实例）。</summary>
    private string _businessConnection = "";

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

        // 等待第一轮会话真正落库（SaveSessionAfterStreamingAsync 在 SSE 流结束后执行，轮询避免跨请求时序竞态）
        await WaitForSessionRowAsync(factory, threadId);

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
    /// 装配 WAF：以 <see cref="MockToolChatClient"/> 作所有 modelId 的底层（<see cref="IModelChatClientFactory"/> stub，
    /// C5 seam——agent 聊天底层经 RouterChatClient → 工厂），并经 Program.cs T16 seam 环境变量注入临时业务/向量/会话库。
    /// 同时 RemoveAll Mem0 记忆服务（IMemoryService/IMemoryStore/SqliteMemoryStore）：记忆链会用全局纯净 IChatClient
    /// 做 LLM 提取，避免脚本化工具 mock 被非 Agent 路径调用污染；记忆非验收 2/3 范围，移除不改变断言语义。
    /// </summary>
    private WebApplicationFactory<Program> StartFactory(MockToolChatClient mock)
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

        try
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IModelChatClientFactory>();
                    services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(mock));

                    // 移除 Mem0 记忆服务（见上方注释）；记忆模型（Models/bge-small-zh-v1.5）与语义检索共享同名目录但
                    // 记忆非本测试范围——移除使 mock 只服务 Agent 工具链路，避免 LlmMemoryExtractor 等非预期调用。
                    services.RemoveAll<IMemoryService>();
                    services.RemoveAll<IMemoryStore>();
                    services.RemoveAll<SqliteMemoryStore>();
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

    /// <summary>轮询等待会话行落库（store_id = "AGUIShopping:{threadId}"，key 带 agent.Name 前缀）。</summary>
    private static async Task WaitForSessionRowAsync(WebApplicationFactory<Program> factory, string threadId, int timeoutMs = 10_000)
    {
        var store = factory.Services.GetService<SqliteAgentSessionStore>();
        Assert.NotNull(store);
        var storeId = $"AGUIShopping:{threadId}";
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
