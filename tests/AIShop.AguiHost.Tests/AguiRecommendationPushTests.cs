using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIShop.AguiHost.Model;
using AIShop.Core.Interfaces;
using AIShop.Service.Agui;
using Mem0Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Sdk;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-reco-realtime S6：推荐实时推送的**服务端端到端契约**（真机 WAF，design §4 / §4.4 D / §5）。
///
/// <para><b>分工</b>：S4（<c>RecommendationPushAgent</c>）证「装饰器行为对」、S5（<c>RecommendationPushMountingTests</c>）
/// 证「装配到位」，本类证「经真实 AG-UI <c>POST /</c> 的 SSE 流，契约逐条成立」——帧序、至多一条、不进历史、
/// 不产工具调用栏、闲聊不推、未调工具仍推、两源一致、异常降级、身份缺失不推。</para>
///
/// <para><b>断言纪律（本仓硬要求）</b>：凡断言「不存在」，必先给**正向锚点**——例如断言「库里没有推荐负载」前，
/// 先断言「同库中确实能查到本轮用户消息」（否则「查了空库」也会绿，属空转断言）；断言「无 CUSTOM」前，
/// 先断言「本轮文本回复与 <c>RUN_FINISHED</c> 照常到达」（否则「流中途炸了」也会绿）。</para>
///
/// <para><b>隔离</b>：临时业务/向量/会话/聊天四库经 <c>Agui__DbConnection</c> / <c>Agui__RagConnection</c> /
/// <c>Agui__SessionConnection</c> / <c>Agui__ChatConnection</c> 环境变量 seam 注入（同 <see cref="AguiE2ETests"/>：
/// WebApplicationBuilder 在 CreateBuilder 阶段读环境变量，早于 Program 顶层读取 seam）；模型 seam 换
/// <see cref="StubModelChatClientFactory"/>（离线免 Key）。挂 <c>[Collection(nameof(AguiRequestTests))]</c>：
/// 启动真实宿主（MigrateAsync / 播种 / RAG 预热）且环境变量为进程级，须与其它宿主级测试串行。</para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiRecommendationPushTests : IDisposable
{
    /// <summary>推荐 CUSTOM 事件名（wire 契约，<c>RecommendationPushContent.EventName</c>）。</summary>
    private const string RecommendationEvent = "recommendation";

    /// <summary>承载推荐依据的工具名。</summary>
    private const string RecommendationTool = "recommend_products";

    /// <summary>推荐负载的特征键（camelCase 序列化后的 JSON 属性名）——只有推荐负载才有，用于「不进历史」的检测。</summary>
    private const string PayloadMarker = "hasRecommendation";

    /// <summary>白名单关键词消息（命中 ProductKeywordMap → 有推荐依据）。</summary>
    private const string KeywordMessage = "我想买跑步鞋";

    /// <summary>另一个白名单关键词消息（命中「耳机」，与上一条依据不同）。</summary>
    private const string EarphoneMessage = "我想买耳机";

    /// <summary>无白名单关键词的闲聊消息。</summary>
    private const string ChitChatMessage = "你好呀，今天心情不错";

    /// <summary>
    /// B1（盘点 L8）：购物意图明确但**不在**白名单里的消息——23 组关键词里既无「T恤」也无展开命中，
    /// 目录里唯一 T 恤（种子 id=2「有机棉T恤」）的 Tags 也不含「T恤」→ 纯关键词判据必然漏推。
    /// </summary>
    private const string TShirtMessage = "T恤有吗";

    /// <summary>种子账户：会话快照归属键（<c>AGUIShopping:{用户名}</c>）与聊天历史 <c>conversation_id</c> 都由它派生。</summary>
    private const string TestUser = "fzf003";

    /// <summary>AG-UI wire 事件类型（<c>AGUI.Abstractions</c> 的实际字符串）。</summary>
    private const string CustomType = "CUSTOM";

    /// <summary>文本内容增量帧类型（R1 场景 2：<c>CUSTOM</c> MUST 位于本轮**全部**此类帧之后）。</summary>
    private const string TextMessageContentType = "TEXT_MESSAGE_CONTENT";

    /// <summary>轮次终点帧类型。</summary>
    private const string RunFinishedType = "RUN_FINISHED";

    /// <summary>轮次错误帧类型（异常降级用例断言其不存在）。</summary>
    private const string RunErrorType = "RUN_ERROR";

    /// <summary>工具调用开始帧类型（工具调用栏条目的来源）。</summary>
    private const string ToolCallStartType = "TOOL_CALL_START";

    /// <summary>工具调用结束帧类型（工具调用栏条目的来源）。</summary>
    private const string ToolCallResultType = "TOOL_CALL_RESULT";

    /// <summary>推荐计算失败时装饰器写出的 Warning 模板片段（<c>RecommendationPushAgent</c>）。</summary>
    private const string PushFailureWarningText = "推荐推送计算失败";

    private readonly List<string> _cleanupPaths = [];
    private readonly List<(string Key, string? Prev)> _envRestore = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var (key, prev) in _envRestore)
            Environment.SetEnvironmentVariable(key, prev);
        _envRestore.Clear();

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

    // ---------- ① 帧序 + 至多一条 + 与同轮工具结果同构（R1-1 / R1-2 / R1-3）----------

    /// <summary>
    /// 帧序（R1-1 / R1-2）：一轮里模型真实调用了 <c>recommend_products</c> → SSE 含
    /// <c>type":"CUSTOM"</c> + <c>name":"recommendation"</c> 的帧，且其**序号早于** <c>RUN_FINISHED</c>；
    /// 同轮 <c>name:"recommendation"</c> 计数**恰为 1**（R1-3）；
    /// <c>value</c> 与同轮 <c>TOOL_CALL_RESULT</c> 的内容**语义一致**（同一 builder 的产物；两侧序列化管线对非 ASCII 的转义形态可不同）。
    /// </summary>
    [Fact]
    public async Task ShouldPushCustomBeforeRunFinished_ShapedLikeSameRoundToolResult()
    {
        var mock = new MockToolChatClient(new MockToolChatClient.ToolScript(
            ToolName: RecommendationTool,
            Arguments: new Dictionary<string, object?> { ["query"] = KeywordMessage },
            FinalText: "S6-T1 已为您甄选跑鞋。"));

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        var (status, sse) = await PostRoundAsync(client, "s6-t1", KeywordMessage);

        Assert.Equal(HttpStatusCode.OK, status);
        // 正锚点①：本轮文本回复送达（流没中途炸）
        Assert.Contains("S6-T1", sse);
        var frames = ParseSseFrames(sse);
        // 正锚点②：模型确实调了工具并拿到结果（否则「与工具结果同构」无从比对）
        var toolPayload = SingleToolResultPayload(frames);

        var customIndex = IndexOfFrame(frames, CustomType, RecommendationEvent);
        var finishedIndex = IndexOfFrame(frames, RunFinishedType, name: null);
        Assert.True(customIndex >= 0, $"SSE 流应含 name=\"{RecommendationEvent}\" 的 CUSTOM 帧。实际帧序：{Describe(frames)}");
        Assert.True(finishedIndex >= 0, $"SSE 流应含 RUN_FINISHED 帧。实际帧序：{Describe(frames)}");
        Assert.True(
            customIndex < finishedIndex,
            $"CUSTOM 必须早于 RUN_FINISHED（实际 CUSTOM@{customIndex}、RUN_FINISHED@{finishedIndex}）。帧序：{Describe(frames)}");

        // R1 场景 2（Z3 措辞澄清）：CUSTOM 必须位于本轮【全部】文本内容帧之后。
        // 注意该场景 **不** 要求 CUSTOM 在协议帧 `TEXT_MESSAGE_END` 之后 —— 真机实测 CUSTOM 在该帧之前
        // （全部内容帧之后、`TEXT_MESSAGE_END` 之前），spec 已注明「两者相对顺序不作要求」。
        // 正锚点先行：本轮确实产出过内容帧 —— 否则「CUSTOM 之后无内容帧」在「压根没产出文本」时也会绿，属空转断言。
        var contentIndices = frames
            .Select((frame, index) => (index, isContent: IsFrame(frame, TextMessageContentType, name: null)))
            .Where(pair => pair.isContent)
            .Select(pair => pair.index)
            .ToList();
        Assert.True(
            contentIndices.Count > 0,
            $"正锚点：本轮应产出 {TextMessageContentType} 帧。帧序：{Describe(frames)}");
        Assert.True(
            contentIndices[^1] < customIndex,
            $"CUSTOM 必须位于本轮全部 {TextMessageContentType} 帧之后（实际最后一个内容帧 @{contentIndices[^1]}、CUSTOM@{customIndex}）。帧序：{Describe(frames)}");

        // R1-3：一轮至多一条
        Assert.Equal(1, CountFrames(frames, CustomType, RecommendationEvent));

        // 与同轮工具结果同构：键集合相同 + products 条目同形同值（同一 builder 口径）
        var pushed = frames[customIndex].GetProperty("value");
        AssertSameShape(toolPayload, pushed);
        // 同口径产出：两负载 JSON **语义**相等即可。工具结果文本与 CUSTOM value 由不同序列化管线产出，
        // 非 ASCII 的转义形态可能不同（工具结果带 `\uXXXX` 转义、CUSTOM value 为原样 UTF-8），
        // 故按 JSON 值比较，而非裸文本逐字节。
        Assert.True(
            JsonElement.DeepEquals(toolPayload, pushed),
            $"CUSTOM 载荷应与同轮工具结果语义一致。tool={toolPayload.GetRawText()} / pushed={pushed.GetRawText()}");
    }

    // ---------- ② 不进历史（R4-3）----------

    /// <summary>
    /// 不进历史（R4-3）：本轮推送了 CUSTOM 之后，直查**会话库** <c>agent_sessions.session_json</c> 与
    /// **聊天历史库** <c>chat_messages.message_json</c> → 均不含推荐负载。
    ///
    /// <para>用「模型只产文本、不调任何工具」的一轮：这样库里唯一可能的 <c>hasRecommendation</c> 来源就是推送载荷
    /// （若模型调了 <c>recommend_products</c>，其 FRC 会合法入库，断言就不再指向推送）。</para>
    ///
    /// <para><b>正向锚点</b>：两个库都能查到**本轮**的用户消息文本（证明查库路径有效、命中本轮数据，不是查了空库），
    /// 且会话库/历史库中确实存有本轮 assistant 回复（同上一条）。</para>
    /// </summary>
    [Fact]
    public async Task ShouldNotLeakPushPayloadIntoSessionOrChatHistory()
    {
        const string userMessage = "S6-T2 我想买跑步鞋";
        const string replyText = "S6T2REPLY 已为您甄选跑鞋若干。";

        using var factory = StartFactory(TextOnlyChatClient(replyText));
        using var client = factory.CreateClient();

        var (status, sse) = await PostRoundAsync(client, "s6-t2", userMessage);

        Assert.Equal(HttpStatusCode.OK, status);
        var frames = ParseSseFrames(sse);
        var customIndex = IndexOfFrame(frames, CustomType, RecommendationEvent);
        Assert.True(customIndex >= 0, $"SSE 流应含推荐 CUSTOM 帧（否则「没入库」是因为压根没推）。帧序：{Describe(frames)}");
        // 正锚点①：推的载荷里确实带特征键（证明下面「库里没有该键」不是无的放矢）
        Assert.True(frames[customIndex].GetProperty("value").GetProperty(PayloadMarker).GetBoolean());

        // 正锚点②③：会话库 + 聊天历史库里都查得到本轮数据（用户消息 + 回复文本）
        var sessionStore = factory.Services.GetService<SqliteAgentSessionStore>();
        Assert.NotNull(sessionStore);
        var sessionJson = await ReadSessionJsonAsync(sessionStore.ConnectionString, $"AGUIShopping:{TestUser}");
        Assert.Contains(userMessage, sessionJson, StringComparison.Ordinal);
        Assert.Contains(replyText, sessionJson, StringComparison.Ordinal);

        var chatProvider = factory.Services.GetRequiredService<SqlChatHistoryProvider>();
        var chatJson = await ReadChatMessagesAsync(chatProvider.Options.ConnectionString, userMessage);
        Assert.Contains(userMessage, chatJson, StringComparison.Ordinal);
        Assert.Contains(replyText, chatJson, StringComparison.Ordinal);

        // 断言（R4-3）：推荐负载不进会话快照、不进聊天历史
        Assert.DoesNotContain(PayloadMarker, sessionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PayloadMarker, chatJson, StringComparison.Ordinal);
    }

    // ---------- ③ 不产生工具调用栏条目（R6）----------

    /// <summary>
    /// 工具调用栏（R6）：<c>CUSTOM</c> 推荐事件 MUST NOT 产生 <c>TOOL_CALL_START</c> / <c>TOOL_CALL_RESULT</c> 帧。
    ///
    /// <para><b>正向锚点</b>：同轮模型真实调用 <c>recommend_products</c> 时，两类帧**正常出现**（各恰 1 条，
    /// 且 <c>toolCallName</c> 就是真实工具名）——正因如此，「恰 1 条」的计数本身就是「推荐没额外产生条目」的证据
    /// （若推荐也走工具帧，计数会变成 2）。</para>
    /// </summary>
    [Fact]
    public async Task ShouldNotCreateToolCallBarEntries_WhenRecommendationPushed()
    {
        var mock = new MockToolChatClient(new MockToolChatClient.ToolScript(
            ToolName: RecommendationTool,
            Arguments: new Dictionary<string, object?> { ["query"] = KeywordMessage },
            FinalText: "S6-T3 已为您甄选跑鞋。"));

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        var (status, sse) = await PostRoundAsync(client, "s6-t3", KeywordMessage);
        Assert.Equal(HttpStatusCode.OK, status);

        var frames = ParseSseFrames(sse);

        // 正锚点：真实工具调用照常产生两类帧（各恰好一条，属于模型那一次调用）
        int starts = CountFrames(frames, ToolCallStartType, name: null);
        int results = CountFrames(frames, ToolCallResultType, name: null);
        Assert.True(starts >= 1, $"真实工具调用应产生 TOOL_CALL_START 帧。帧序：{Describe(frames)}");
        Assert.True(results >= 1, $"真实工具调用应产生 TOOL_CALL_RESULT 帧。帧序：{Describe(frames)}");
        Assert.Equal(results, starts);
        Assert.Equal(new[] { RecommendationTool }, ToolCallNames(frames));

        // 推荐 CUSTOM 确实发出（否则「没有多余工具帧」是空转）
        var customIndex = IndexOfFrame(frames, CustomType, RecommendationEvent);
        Assert.True(customIndex >= 0, $"SSE 流应含推荐 CUSTOM 帧。帧序：{Describe(frames)}");

        // 断言（R6）：工具帧的总数与真实调用**一一对应**（上限 = 真实调用数），推荐事件没有多产生任何条目；
        // 且推荐帧本身不是工具帧（无 toolCallId / toolCallName 属性）。
        Assert.Equal(1, starts);
        Assert.Equal(1, results);
        Assert.False(frames[customIndex].TryGetProperty("toolCallId", out _));
        Assert.False(frames[customIndex].TryGetProperty("toolCallName", out _));
        Assert.DoesNotContain(ToolCallNames(frames), toolName => toolName == RecommendationEvent);
    }

    // ---------- ④ 闲聊轮不推 / 未调工具仍推（R2-1 / R3-1）----------

    /// <summary>
    /// 门控（R2-1 + R3-1）：同一宿主两个请求，模型**只产文本、不发任何工具调用**。
    /// <list type="bullet">
    /// <item>请求 A（含白名单关键词）→ 服务端**照常**推送 CUSTOM（推荐不依赖模型是否调用工具，R3-1）；</item>
    /// <item>请求 B（闲聊、无关键词）→ 不推送 CUSTOM，但文本回复与 <c>RUN_FINISHED</c> 照常（R2-1）。</item>
    /// </list>
    /// 请求 A 即请求 B 的**正向锚点**：同一宿主、同一模型替身，「B 没推」只可能是门控结果，不是环境坏掉。
    /// </summary>
    [Fact]
    public async Task ShouldPushOnKeywordTextRound_AndNotPushOnChitChatRound()
    {
        using var factory = StartFactory(TextOnlyChatClient("S6-T4 已收到您的消息。"));
        using var client = factory.CreateClient();

        // 请求 A：无任何工具调用，仅文本 + 关键词 → 仍推（R3-1）
        var (statusA, sseA) = await PostRoundAsync(client, "s6-t4-keyword", KeywordMessage);
        Assert.Equal(HttpStatusCode.OK, statusA);
        var framesA = ParseSseFrames(sseA);
        Assert.Equal(1, CountFrames(framesA, CustomType, RecommendationEvent));
        // CUSTOM 必须早于 RUN_FINISHED（轮末、不打断回复流）
        Assert.True(
            IndexOfFrame(framesA, CustomType, RecommendationEvent) < IndexOfFrame(framesA, RunFinishedType, name: null),
            $"CUSTOM 应早于 RUN_FINISHED。帧序：{Describe(framesA)}");

        // 请求 B：闲聊、无关键词 → 不推（R2-1），但流照常走完
        var (statusB, sseB) = await PostRoundAsync(client, "s6-t4-chitchat", ChitChatMessage);
        Assert.Equal(HttpStatusCode.OK, statusB);
        // 正锚点：文本回复 + 轮次终点照常（「没有 CUSTOM」不是因为流炸了或没跑完）
        Assert.Contains("S6-T4", sseB);
        var framesB = ParseSseFrames(sseB);
        Assert.True(IndexOfFrame(framesB, RunFinishedType, name: null) >= 0, $"闲聊轮应正常收尾。帧序：{Describe(framesB)}");
        Assert.DoesNotContain(RunErrorType, Describe(framesB));

        // 断言（R2-1）：闲聊轮无推荐 CUSTOM，也不产生任何工具调用栏条目
        Assert.Equal(0, CountFrames(framesB, CustomType, RecommendationEvent));
        Assert.Equal(0, CountFrames(framesB, ToolCallStartType, name: null));
        Assert.Equal(0, CountFrames(framesB, ToolCallResultType, name: null));
    }

    // ---------- ⑤ 两源一致（R3-2）----------

    /// <summary>
    /// 两源一致（R3-2）：模型真实调用 <c>recommend_products(query = "跑步鞋")</c>，而**本轮用户消息含另一关键词**
    /// （「耳机」）→ 推送依据须取**工具的 <c>query</c>**，故 <c>CUSTOM</c> 的 <c>value</c> 与同轮
    /// <c>TOOL_CALL_RESULT</c> 内容**语义一致**（同依据、同口径）。
    ///
    /// <para>判别性：若实现改用本轮用户消息作依据，value 会变成「耳机」口径 → 与工具结果不等，用例必红；
    /// 另断言 <c>reason</c> 引工具 query 派生的关键词（「健身」）且不提「耳机」，把「用的是哪条依据」钉死在行为上。</para>
    /// </summary>
    [Fact]
    public async Task ShouldMatchToolCallResult_WhenToolQueryDiffersFromUserMessage()
    {
        var mock = new MockToolChatClient(new MockToolChatClient.ToolScript(
            ToolName: RecommendationTool,
            Arguments: new Dictionary<string, object?> { ["query"] = KeywordMessage },
            FinalText: "S6-T5 按您的查询为您推荐。"));

        using var factory = StartFactory(mock);
        using var client = factory.CreateClient();

        var (status, sse) = await PostRoundAsync(client, "s6-t5", EarphoneMessage);
        Assert.Equal(HttpStatusCode.OK, status);

        var frames = ParseSseFrames(sse);
        var toolPayload = SingleToolResultPayload(frames);
        var customIndex = IndexOfFrame(frames, CustomType, RecommendationEvent);
        Assert.True(customIndex >= 0, $"SSE 流应含推荐 CUSTOM 帧。帧序：{Describe(frames)}");

        var pushed = frames[customIndex].GetProperty("value");

        // 正锚点：依据确实是工具 query（「我想买跑步鞋」）而非本轮用户消息（「我想买耳机」）。
        // query 命中的白名单关键词按序数序为「健身」「跑步」「鞋子」…，推荐商品的 reason 引最前的「健身」；
        // 若依据错用本轮用户消息，reason 会变成「耳机」类，两条断言同时必红。
        var reasons = pushed.GetProperty("products").EnumerateArray()
            .Select(product => product.GetProperty("reason").GetString() ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.Contains("健身", reason, StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.Contains("耳机", StringComparison.Ordinal));

        // 两源一致（语义，非逐字节）
        // 语义一致（非逐字节）：工具结果文本经宿主再序列化（非 ASCII 转义为 \uXXXX），
        // 而 CUSTOM 的 value 是 JsonElement 原样写出（保留 UTF-8 中文）——两者 JSON 值相同、转义形态不同。
        Assert.True(
            JsonElement.DeepEquals(toolPayload, pushed),
            $"CUSTOM 载荷应与同轮工具结果语义一致。tool={toolPayload.GetRawText()} / pushed={pushed.GetRawText()}");
    }

    // ---------- ⑥ 异常降级（R4-1）----------

    /// <summary>
    /// 异常降级（R4-1）：注入「推荐偏好缓存键即抛」的 <see cref="IMemoryCache"/> 替身 → 推荐计算必然抛 →
    /// 本轮回复文本完整、<c>RUN_FINISHED</c> 照常、无 <c>RUN_ERROR</c>、无推荐 CUSTOM，
    /// 且 Serilog 静态日志里**恰好一条 Warning**（模板含「推荐推送计算失败」）。
    ///
    /// <para><b>正向锚点</b>：那条 Warning 本身就是「降级分支确实被走到」的证据（否则「没有 CUSTOM」可能只是门控）；
    /// 再加文本回复与 <c>RUN_FINISHED</c> 照常，排除「流炸了所以没帧」。</para>
    /// </summary>
    [Fact]
    public async Task ShouldDeliverReplyAndWarnExactlyOnce_WhenRecommendationPushThrows()
    {
        using var factory = StartFactory(
            TextOnlyChatClient("S6-T6 已帮您处理完毕。"),
            services =>
            {
                services.RemoveAll<IMemoryCache>();
                services.AddSingleton<IMemoryCache>(new PreferenceCacheThrowingMemoryCache());
            });
        using var client = factory.CreateClient();

        var sink = new CollectingSink();
        var originalLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        HttpStatusCode status;
        string sse;
        try
        {
            (status, sse) = await PostRoundAsync(client, "s6-t6", KeywordMessage);
        }
        finally
        {
            Log.Logger = originalLogger;
        }

        Assert.Equal(HttpStatusCode.OK, status);

        var frames = ParseSseFrames(sse);
        // 正锚点：回复文本完整送达 + 轮次正常收尾（没有因推荐异常而断了流）
        Assert.Contains("S6-T6", sse);
        Assert.True(IndexOfFrame(frames, RunFinishedType, name: null) >= 0, $"本轮应正常收尾。帧序：{Describe(frames)}");

        // 断言（R4-1）：无 RUN_ERROR、无推荐 CUSTOM
        Assert.DoesNotContain(RunErrorType, Describe(frames));
        Assert.Equal(0, CountFrames(frames, CustomType, RecommendationEvent));

        // 降级不静默：恰好一条 Warning，来自装饰器的推送失败分支
        var warning = Assert.Single(sink.Events, logEvent => logEvent.Level == LogEventLevel.Warning);
        Assert.Contains(PushFailureWarningText, warning.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    // ---------- ⑦ 身份缺失不推（R4-2）----------

    /// <summary>
    /// 身份缺失（R4-2）：<c>ICurrentUserAccessor.CurrentUser</c> 为 <c>null</c> → 不推送 CUSTOM，
    /// 也不报错（HTTP 200 + 文本回复 + <c>RUN_FINISHED</c> 照常，无 <c>RUN_ERROR</c>）。
    ///
    /// <para><b>正向锚点</b>：同一宿主、同一依据，把身份绑上后再发一轮 → CUSTOM 照常出现
    /// （证明「第一轮没推」源于身份缺失，而非无依据 / 装配坏掉）。</para>
    /// </summary>
    [Fact]
    public async Task ShouldNotPushWhenCurrentUserMissing_WhileBoundIdentityStillPushes()
    {
        var accessor = Substitute.For<ICurrentUserAccessor>();
        accessor.CurrentUser.Returns((string?)null);

        using var factory = StartFactory(
            TextOnlyChatClient("S6-T7 已收到。"),
            services =>
            {
                services.RemoveAll<ICurrentUserAccessor>();
                services.AddSingleton(accessor);
            });
        using var client = factory.CreateClient();

        // 身份缺失：无 CUSTOM、无错误响应
        var (statusMissing, sseMissing) = await PostRoundAsync(client, "s6-t7-missing", KeywordMessage);
        Assert.Equal(HttpStatusCode.OK, statusMissing);
        Assert.Contains("S6-T7", sseMissing);
        var framesMissing = ParseSseFrames(sseMissing);
        Assert.True(IndexOfFrame(framesMissing, RunFinishedType, name: null) >= 0, $"本轮应正常收尾。帧序：{Describe(framesMissing)}");
        Assert.DoesNotContain(RunErrorType, Describe(framesMissing));
        Assert.Equal(0, CountFrames(framesMissing, CustomType, RecommendationEvent));

        // 正向锚点：同一宿主、同一依据，绑上身份后照常推送
        accessor.CurrentUser.Returns("marla");
        var (statusBound, sseBound) = await PostRoundAsync(client, "s6-t7-bound", KeywordMessage);
        Assert.Equal(HttpStatusCode.OK, statusBound);
        var framesBound = ParseSseFrames(sseBound);
        Assert.Equal(1, CountFrames(framesBound, CustomType, RecommendationEvent));
    }

    // ---------- ⑧ B1：语义检索注入 + L8「T恤有吗」（真机 bge）----------

    /// <summary>
    /// B1-eT + L8 端到端（真机 WAF，走**真实** bge 语义检索）：宿主 <c>AddRagService</c> 注册的
    /// <see cref="IProductSemanticSearch"/> 经可选参自动注入 provider，使购物意图明确但不在白名单里的
    /// 「T恤有吗」能推出目录内唯一 T 恤（种子 id=2）。
    ///
    /// <para><b>判别性</b>：若该依赖未注入（可选参为 null），门控只剩关键词判据 → 该轮 0 帧、用例必红。
    /// 故本用例同时锁住「注入面」（生产装配里 provider 拿到的不是 null）与「L8 修复」两件事；
    /// 替身驱动的确定性版本在 <c>RecommendationPushPayloadTests</c>（不依赖模型文件）。</para>
    ///
    /// <para><b>正锚点</b>：同宿主、同模型替身的另一轮（白名单关键词）照常推 1 帧 —— 证明 T 恤轮的有帧
    /// 不是环境自带的；并断言文本回复 + <c>RUN_FINISHED</c> 照常，排除「流串了所以帧序异常」。</para>
    /// </summary>
    [Fact]
    public async Task ShouldPushTShirtRound_ViaRealSemanticSearchInjectedFromHost()
    {
        using var factory = StartFactory(TextOnlyChatClient("S6-T8 已为您找到 T 恤。"));
        using var client = factory.CreateClient();

        // 正锚点：白名单关键词轮照常推 1 帧（门控与装配都在工作）
        var (statusKeyword, sseKeyword) = await PostRoundAsync(client, "s6-t8-keyword", KeywordMessage);
        Assert.Equal(HttpStatusCode.OK, statusKeyword);
        Assert.Equal(1, CountFrames(ParseSseFrames(sseKeyword), CustomType, RecommendationEvent));

        // 断言（L8）：T 恤轮经真实语义检索放行，载荷含 id=2
        var (status, sse) = await PostRoundAsync(client, "s6-t8-tshirt", TShirtMessage);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("S6-T8", sse);
        var frames = ParseSseFrames(sse);
        Assert.True(
            IndexOfFrame(frames, RunFinishedType, name: null) >= 0,
            $"本轮应正常收尾。帧序：{Describe(frames)}");

        var customIndex = IndexOfFrame(frames, CustomType, RecommendationEvent);
        Assert.True(
            customIndex >= 0,
            $"「T恤有吗」应经语义检索推出推荐 CUSTOM 帧（0 帧 = 漏推 L8 或语义检索未注入）。帧序：{Describe(frames)}");
        Assert.Equal(1, CountFrames(frames, CustomType, RecommendationEvent));

        var ids = frames[customIndex].GetProperty("value").GetProperty("products").EnumerateArray()
            .Select(product => product.GetProperty("id").GetInt32())
            .ToArray();
        Assert.Contains(2, ids);
    }

    // ---------- 装配 ----------

    /// <summary>
    /// 启动 WAF 宿主：四库经环境变量 seam 注入临时目录；模型 seam 换离线 stub；<c>IMemoryService</c> 移除
    /// （Mem0 提取链会用全局纯净 <c>IChatClient</c> 走 LLM，用脚本化替身会污染 mock，且非本工单验收面）。
    /// </summary>
    /// <param name="mock">脚本化模型客户端（所有 modelId 的底层）。</param>
    /// <param name="configure">用例额外的 DI 覆写（如替换缓存 / 身份访问器）。</param>
    private WebApplicationFactory<Program> StartFactory(IChatClient mock, Action<IServiceCollection>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_s6_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupPaths.Add(dir);

        SetEnvironment("Agui__DbConnection", $"Data Source={Path.Combine(dir, "business.db")}");
        SetEnvironment("Agui__RagConnection", $"Data Source={Path.Combine(dir, "rag.db")}");
        SetEnvironment("Agui__SessionConnection", $"Data Source={Path.Combine(dir, "sessions.db")}");
        SetEnvironment("Agui__ChatConnection", $"Data Source={Path.Combine(dir, "chat.db")}");

        try
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IModelChatClientFactory>();
                    services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(mock));
                    // 记忆服务移除见方法注释；IMemoryStore 保留（recommend_products 工具依赖它，移除会让 keyed 工厂解析失败）
                    services.RemoveAll<IMemoryService>();
                    configure?.Invoke(services);
                }));

            // CreateClient() 触发 host 构建（Program 顶层读 seam 的时点），构建完成后即可恢复环境变量
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

    /// <summary>只产文本、不发任何工具调用的脚本化 <see cref="IChatClient"/>（两入口同一回复，同探针用例口径）。</summary>
    private static IChatClient TextOnlyChatClient(string replyText)
    {
        var mock = Substitute.For<IChatClient>();
        mock.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText)));
        mock.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamTextAsync(replyText));
        return mock;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamTextAsync(string text)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    /// <summary>发一轮 AG-UI 请求（POST "/" RunAgentInput），返回状态码与 SSE 响应体。</summary>
    private static async Task<(HttpStatusCode Status, string Sse)> PostRoundAsync(
        HttpClient client,
        string threadId,
        string userMessage,
        string username = "fzf003")
    {
        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody(threadId, username, userMessage), Encoding.UTF8, "application/json"));

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>构造 AG-UI <c>RunAgentInput</c> 形状的请求体（消息 id 唯一 GUID，同 AguiE2ETests）。</summary>
    private static string RunAgentBody(string threadId, string username, string userMessage)
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
            ["forwardedProps"] = new JsonObject
            {
                ["username"] = username,
            },
        };

        return root.ToJsonString();
    }

    /// <summary>把 SSE 响应体拆成 JSON 帧列表（只取 <c>data:</c> 行；同探针用例口径）。</summary>
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

    /// <summary>首个匹配 <paramref name="type"/>（<paramref name="name"/> 非 null 时还需 name 相等）的帧序号；无匹配返回 -1。</summary>
    private static int IndexOfFrame(List<JsonElement> frames, string type, string? name)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (IsFrame(frames[i], type, name))
                return i;
        }

        return -1;
    }

    /// <summary>匹配 <paramref name="type"/>（<paramref name="name"/> 非 null 时还需 name 相等）的帧数。</summary>
    private static int CountFrames(List<JsonElement> frames, string type, string? name)
        => frames.Count(frame => IsFrame(frame, type, name));

    private static bool IsFrame(JsonElement frame, string type, string? name)
        => frame.TryGetProperty("type", out var typeProperty)
            && string.Equals(typeProperty.GetString(), type, StringComparison.Ordinal)
            && (name is null
                || (frame.TryGetProperty("name", out var nameProperty)
                    && string.Equals(nameProperty.GetString(), name, StringComparison.Ordinal)));

    /// <summary>工具调用栏条目的工具名（<c>TOOL_CALL_START.toolCallName</c>）。</summary>
    private static string[] ToolCallNames(List<JsonElement> frames)
        => [.. frames
            .Where(frame => IsFrame(frame, ToolCallStartType, name: null))
            .Select(frame => frame.GetProperty("toolCallName").GetString() ?? string.Empty)];

    /// <summary>
    /// 取同轮**唯一**一条 <c>TOOL_CALL_RESULT</c> 的 <c>content</c> 并解析为 JSON 对象
    /// （正锚点：没有真实工具结果时直接失败，避免下游「同构」断言空转）。
    /// </summary>
    private static JsonElement SingleToolResultPayload(List<JsonElement> frames)
    {
        var resultFrame = Assert.Single(frames, frame => IsFrame(frame, ToolCallResultType, name: null));
        var content = resultFrame.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(content), "TOOL_CALL_RESULT 应携带非空 content");

        return ParsePayload(content);
    }

    /// <summary>解析工具结果文本为 JSON 对象（宿主可能多编码一层——剥到对象为止，客户端 <c>decodeToolResultContent</c> 同口径）。</summary>
    private static JsonElement ParsePayload(string content)
    {
        var payload = JsonDocument.Parse(content).RootElement.Clone();
        if (payload.ValueKind == JsonValueKind.String)
            payload = JsonDocument.Parse(payload.GetString()!).RootElement.Clone();

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        return payload;
    }

    /// <summary>断言两负载**同构**：顶层键集合相同，且 <c>products</c> 条目的键集合相同、逐条取值相同。</summary>
    private static void AssertSameShape(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(
            expected.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal),
            actual.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var expectedProducts = expected.GetProperty("products").EnumerateArray().ToArray();
        var actualProducts = actual.GetProperty("products").EnumerateArray().ToArray();
        Assert.NotEmpty(expectedProducts);
        Assert.Equal(expectedProducts.Length, actualProducts.Length);

        for (var i = 0; i < expectedProducts.Length; i++)
        {
            Assert.Equal(
                expectedProducts[i].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal),
                actualProducts[i].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.True(
                JsonElement.DeepEquals(expectedProducts[i], actualProducts[i]),
                $"推荐条目 #{i} 应与工具结果语义一致。tool={expectedProducts[i].GetRawText()} / pushed={actualProducts[i].GetRawText()}");
        }
    }

    /// <summary>把帧序压成可读串（断言失败时的诊断信息）。</summary>
    private static string Describe(List<JsonElement> frames)
        => string.Join(
            " -> ",
            frames.Select(frame =>
            {
                var type = frame.TryGetProperty("type", out var t) ? t.GetString() : "?";
                var name = frame.TryGetProperty("name", out var n) ? n.GetString() : null;
                return name is null ? type : $"{type}:{name}";
            }));

    /// <summary>
    /// 轮询读会话快照（<c>SaveSessionAsync</c> 在 SSE 流结束后执行，轮询避免跨请求时序竞态）；
    /// 超时视为失败（锚点不足即用例失败，不返回空串——否则「库里没有推荐负载」会变成空转断言）。
    /// </summary>
    private static async Task<string> ReadSessionJsonAsync(string connectionString, string storeId, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT session_json FROM agent_sessions WHERE store_id = $storeId";
                command.Parameters.AddWithValue("$storeId", storeId);
                if (await command.ExecuteScalarAsync() is string json && !string.IsNullOrWhiteSpace(json))
                    return json;
            }
            catch (SqliteException)
            {
                // 宿主仍在写库（快照/WAL），短暂重试
            }

            await Task.Delay(200);
        }

        throw new XunitException($"等待会话快照落库超时：store_id = {storeId}");
    }

    /// <summary>
    /// 轮询读聊天历史全部行文本（<c>message_json</c> 拼接；表为懒建，未建表 / 文件不存在等同 0 行）。
    /// 轮询以 <paramref name="requiredFragment"/> 出现为完成信号——超时即失败（保证正向锚点非空，
    /// 后续「不含推荐负载」的断言才有意义）。
    /// </summary>
    private static async Task<string> ReadChatMessagesAsync(
        string connectionString,
        string requiredFragment,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var joined = string.Empty;

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

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT message_json FROM chat_messages";
                var builder = new StringBuilder();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    builder.Append(reader.GetString(0)).Append('\n');

                joined = builder.ToString();
                if (joined.Contains(requiredFragment, StringComparison.Ordinal))
                    return joined;
            }
            catch (SqliteException)
            {
                // 宿主仍在写库，短暂重试
            }

            await Task.Delay(200);
        }

        throw new XunitException($"等待聊天历史落库超时（未查到含「{requiredFragment}」的行）");
    }

    /// <summary>
    /// <see cref="IMemoryCache"/> 替身：**只**对推荐偏好缓存键（<c>reco_prefkw_*</c>）抛，
    /// 其余键原样委托真实缓存 —— 异常因此精确落在「推荐计算」路径上（可归因），
    /// 不会波及商品目录缓存等旁路（<c>ProductRepository.GetAll</c>）。
    /// </summary>
    private sealed class PreferenceCacheThrowingMemoryCache : IMemoryCache
    {
        private const string FailureKeyFragment = "reco_prefkw";

        private readonly MemoryCache _inner = new(new MemoryCacheOptions());

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

        public void Remove(object key) => _inner.Remove(key);

        public bool TryGetValue(object key, out object? value)
        {
            if (key is string text && text.Contains(FailureKeyFragment, StringComparison.Ordinal))
                throw new InvalidOperationException("S6 用例：推荐偏好缓存替身故意抛出");

            return _inner.TryGetValue(key, out value);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>收集 Serilog 日志事件的内存 sink（配合临时替换全局 <c>Log.Logger</c> 捕获告警）。</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
