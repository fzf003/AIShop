using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIShop.Service.Agui;
using Mem0Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C1（agui-reco-realtime）：**内层流抛异常时本轮仍必须到达确定性收尾**。
///
/// <para><b>背景（实测证据）</b>：宿主在 net10.0 走 <c>TypedResults.ServerSentEvents</c>，其 <c>RUN_ERROR</c>
/// 兜底写在 <c>#if !NET10_0_OR_GREATER</c> 分支里（安装包 net10.0 DLL 内 <c>RunErrorEvent</c> /
/// <c>StreamingError</c> 字符串命中数实测为 0，net8.0/net9.0 各命中 1）。因此内层模型流一旦抛异常，
/// 若异常直接冲出 agent 的迭代器，整条 SSE 流会在半途**静默断开**：既无 <c>CUSTOM</c> 推荐帧，
/// 也无任何终止帧 → 客户端 <c>@ag-ui/client</c> 的 <c>isRunning</c> 永不复位（C2 的另一半）。</para>
///
/// <para><b>本类断言</b>（经真实 AG-UI wire：WAF <c>POST "/"</c> RunAgentInput → SSE 事件流）：</para>
/// <list type="number">
/// <item>脚本化模型在产出若干帧后抛异常 → 故障前已送达的内容帧仍在，且**终止帧确实到了客户端**
/// （本轮走错误出口 <c>RUN_ERROR</c>，不是伪造的成功收尾）；</item>
/// <item>推荐推送与内层异常**解耦** → 本轮 <c>CUSTOM</c> 推荐帧照常到达（推荐不因模型故障而静默丢失）。</item>
/// </list>
///
/// <para><b>为什么用增量读流</b>：C1 的早期方案是「先 yield 终止帧、再原样重抛异常」——重抛会立刻打断响应，
/// <see cref="HttpContent.ReadAsStringAsync"/> 在此时抛 <c>HttpRequestException</c> 且**把已经送出的帧一并丢掉**，
/// 用例只能看到「连接断了」，恰好把要验的行为遮掉。改用「不重抛」方案后（见 <c>RecommendationPushAgent</c>），
/// 本轮的读取已能正常结束；本类仍保留 <see cref="HttpCompletionOption.ResponseHeadersRead"/> + **手动增量读**：
/// 逐块读出并累积，万一出现传输层中断也保住已收到的部分，由断言判定帧序（传输中断本身**不**判失败）。</para>
///
/// <para><b>正锚点</b>：<see cref="NormalStream_StillPushesCustomAndFinishes"/> 用同款装配 + 不抛的模型流，
/// 证明该 harness（身份 / 关键词 / 推荐链路）本来就能推 CUSTOM 且能正常收尾 —— 排除「异常轮没帧」是环境坏掉
/// 或关键词没命中造成的空转。</para>
///
/// <para>挂 <c>[Collection(nameof(AguiRequestTests))]</c>：启动真实宿主（MigrateAsync / 播种 / RAG 预热），
/// 且本类经进程级环境变量注入四库 seam，须与其它宿主级测试串行（同 <c>AguiRecommendationPushTests</c> 约定）。</para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiStreamCloseoutTests : IDisposable
{
    /// <summary>终止帧类型：正常收尾。</summary>
    private const string RunFinishedType = "RUN_FINISHED";

    /// <summary>终止帧类型：错误收尾（AG-UI wire 上另一条合法出口）。</summary>
    private const string RunErrorType = "RUN_ERROR";

    /// <summary>推荐 CUSTOM 帧类型（wire 契约）。</summary>
    private const string CustomType = "CUSTOM";

    /// <summary>推荐 CUSTOM 帧的事件名（wire 契约，<c>RecommendationPushContent.EventName</c>）。</summary>
    private const string RecommendationEvent = "recommendation";

    /// <summary>终止帧的 <c>code</c>（wire 契约，<c>AguiStreamFailureContent.ErrorCode</c>）。</summary>
    private const string FailureCode = "agent_stream_failure";

    /// <summary>本轮用户消息（含白名单关键词「跑步」= 推荐依据，命中即推）。</summary>
    private const string KeywordMessage = "我想买跑步鞋";

    /// <summary>可购物种子用户（<c>AguiUsernameForwarder</c> 存在性校验通过）。</summary>
    private const string TestUser = "fzf003";

    /// <summary>中途抛出的异常文案（用于在诊断信息里确认「确实是这条故障」）。</summary>
    private const string MidStreamFailure = "C1 用例：模型流产出若干帧后中断";

    /// <summary>故障前已产出的部分回复文本（证明「产出若干帧后」而非「一帧未发就炸」）。</summary>
    private const string PartialReply = "正在为您查询 C1-PARTIAL-MARKER";

    private readonly List<string> _cleanupPaths = [];
    private readonly List<(string Key, string? Prev)> _envRestore = [];

    public void Dispose()
    {
        // SQLite 连接池会持有临时库文件句柄 → 先清池，临时目录才删得掉（同 AguiRecommendationPushTests）。
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

    /// <summary>
    /// 需求 1 + 2（C1 核心）：模型流在产出 1 帧后抛异常 → 本轮仍到达确定性收尾，且推荐 CUSTOM 照常推送。
    /// </summary>
    [Fact]
    public async Task MidStreamFailure_StillReachesDeterministicCloseout_AndPushesCustom()
    {
        using var factory = StartFactory(ThrowingMidStreamChatClient());
        using var client = factory.CreateClient();

        var (status, sse, transportFailure) = await PostRoundTolerantAsync(client, "c1-throw-thread", KeywordMessage);

        // ① 响应头已正常建立（不是 500 短路 / 404 身份校验短路）
        Assert.Equal(HttpStatusCode.OK, status);

        // ② 情形：故障前已产出的帧确实送达（证明是「中途炸」而不是「没开始」）。
        // 正锚点先行：没有这条，下面「终止帧存在」在「一帧都没收到」时也无从归因。
        // 诊断增强（临时）：判定条件与 Assert.Contains 完全相同（sse 含不含该子串），
        // 仅在失败时额外打印服务端事实，用于区分「写了没送达」与「压根没写」。
        Assert.True(
            sse.Contains(PartialReply, StringComparison.Ordinal),
            $"故障前已产出的内容帧应已送达。status={status}；"
            + $"传输异常={(transportFailure is null ? "无" : $"{transportFailure.GetType().Name}: {transportFailure.Message}")}；"
            + $"已收字节数={sse.Length}\n原始 SSE：{Truncate(sse)}");

        var frames = ParseSseFrames(sse);

        // ③ 确定性收尾：终止帧确实到了客户端。本轮是真实失败 → 必须走错误出口 RUN_ERROR
        //    （伪装成 RUN_FINISHED 等于谎报成功，属另一种静默）。
        //    诊断里带上传输异常：若帧序为空，需要立刻能分辨「连接中断」还是「映射没生效」。
        var terminalIndex = TerminalFrameIndex(frames);
        Assert.True(
            terminalIndex >= 0,
            $"内层流抛异常后本轮仍须到达确定性收尾。实际帧序：{Describe(frames)}；传输异常：{transportFailure}\n原始 SSE：{Truncate(sse)}");

        var terminal = frames[terminalIndex];
        Assert.True(
            IsFrame(terminal, RunErrorType, name: null),
            $"异常轮的终止帧必须是 {RunErrorType}（不得伪装成 {RunFinishedType}）。实际帧序：{Describe(frames)}\n原始 SSE：{Truncate(sse)}");
        Assert.Equal(FailureCode, ReadString(terminal, "code"));
        Assert.False(
            string.IsNullOrWhiteSpace(ReadString(terminal, "message")),
            $"终止帧应携带面向客户端的错误说明。实际帧：{terminal.GetRawText()}");

        // ④ 推送与异常解耦：本轮 CUSTOM 推荐帧照常到达（且恰一条）
        Assert.True(
            CountFrames(frames, CustomType, RecommendationEvent) == 1,
            $"内层异常不得让推荐静默丢失——本轮应恰有 1 条 CUSTOM/{RecommendationEvent} 帧。实际帧序：{Describe(frames)}\n原始 SSE：{Truncate(sse)}");
    }

    /// <summary>
    /// 正锚点：同款 harness + **不抛**的模型流 → CUSTOM 照常推送且正常收尾。
    /// 该用例始终应绿（它锁的是「harness 真能推」这一前提，而不是被修复的行为）。
    /// </summary>
    [Fact]
    public async Task NormalStream_StillPushesCustomAndFinishes()
    {
        using var factory = StartFactory(TextChatClient());
        using var client = factory.CreateClient();

        var (status, sse, transportFailure) = await PostRoundTolerantAsync(client, "c1-normal-thread", KeywordMessage);

        // 正常轮：流自然结束，读到底不该出现传输中断
        Assert.Null(transportFailure);
        Assert.Equal(HttpStatusCode.OK, status);

        var frames = ParseSseFrames(sse);
        Assert.True(TerminalFrameIndex(frames) >= 0, $"正常轮应正常收尾。帧序：{Describe(frames)}");
        Assert.Equal(1, CountFrames(frames, CustomType, RecommendationEvent));
    }

    // ---------- 装配 ----------

    /// <summary>
    /// 启动 WAF 宿主：四库经环境变量 seam 注入临时目录（T16 约定：Program 顶层读配置早于
    /// <c>ConfigureAppConfiguration</c>，故只能用环境变量）；模型 seam 换离线 stub；
    /// <c>IMemoryService</c> 移除（Mem0 提取链会用全局纯净 <c>IChatClient</c> 走 LLM，会污染脚本化 mock）。
    /// </summary>
    private WebApplicationFactory<Program> StartFactory(IChatClient mock, Action<IServiceCollection>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_c1_{Guid.NewGuid():N}");
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
                    // IMemoryStore / SqliteMemoryStore 保留：recommend_products 工具依赖它，移除会让 keyed 工厂解析失败
                    services.RemoveAll<IMemoryService>();
                    configure?.Invoke(services);
                }));

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

    /// <summary>只产一条文本帧的脚本化 <see cref="IChatClient"/>（正锚点用；两入口同回复）。</summary>
    private static IChatClient TextChatClient()
    {
        var mock = Substitute.For<IChatClient>();
        mock.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, PartialReply)));
        mock.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(EmitThenCompleteAsync());
        return mock;
    }

    /// <summary>
    /// 脚本化「产出若干帧后抛异常」的模型流：先吐一条部分回复文本（客户端因此已收到内容帧），
    /// 再抛 <see cref="InvalidOperationException"/>（模拟真实模型/网关故障）。异常沿
    /// <c>FICC → ChatClientAgent → OpenTelemetryAgent → RecommendationPushAgent</c> 上传。
    /// </summary>
    private static IChatClient ThrowingMidStreamChatClient()
    {
        var mock = Substitute.For<IChatClient>();
        mock.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException(MidStreamFailure));
        mock.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(EmitThenThrowAsync());
        return mock;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitThenCompleteAsync()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, PartialReply);
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitThenThrowAsync()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, PartialReply);
        await Task.Yield();
        throw new InvalidOperationException(MidStreamFailure);
    }

    /// <summary>
    /// 发一轮 AG-UI 请求并**增量**读取 SSE 响应体：<see cref="HttpCompletionOption.ResponseHeadersRead"/> 拿到
    /// 响应头即返回，再逐块读流并累积。若出现传输层中断（连接被打断），本方法把**已收到的部分**照常返回，
    /// 交由断言判定（见类注释的「为什么用增量读流」）。
    /// </summary>
    private static async Task<(HttpStatusCode? Status, string Sse, Exception? TransportFailure)> PostRoundTolerantAsync(
        HttpClient client,
        string threadId,
        string userMessage,
        string username = TestUser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(RunAgentBody(threadId, username, userMessage), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage? response = null;
        var buffer = new StringBuilder();

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            await using var stream = await response.Content.ReadAsStreamAsync();
            var chunk = new byte[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(chunk);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 响应已开始写出后连接被打断：已累积的帧保留，不丢观测面
                    return (response.StatusCode, buffer.ToString(), ex);
                }

                if (read == 0)
                    return (response.StatusCode, buffer.ToString(), null);

                buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 建连/读流前的失败（如身份校验短路等）：无帧可判，交由断言给出可读诊断
            return (response?.StatusCode, buffer.ToString(), ex);
        }
        finally
        {
            response?.Dispose();
        }
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

    /// <summary>把 SSE 响应体拆成 JSON 帧列表（只取 <c>data:</c> 行；同 AguiRecommendationPushTests 口径）。</summary>
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

    /// <summary>
    /// 终止帧的序号：**最后一个**类型为 <c>RUN_FINISHED</c> 或 <c>RUN_ERROR</c> 的帧；无终止帧返回 -1。
    /// 「确定性收尾」= 至少存在一个终止帧（两条出口都是客户端 <c>isRunning</c> 可复位的终态）。
    /// </summary>
    private static int TerminalFrameIndex(List<JsonElement> frames)
    {
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            if (IsFrame(frames[i], RunFinishedType, name: null) || IsFrame(frames[i], RunErrorType, name: null))
                return i;
        }

        return -1;
    }

    private static int CountFrames(List<JsonElement> frames, string type, string? name)
        => frames.Count(frame => IsFrame(frame, type, name));

    private static bool IsFrame(JsonElement frame, string type, string? name)
        => frame.TryGetProperty("type", out var typeProperty)
            && string.Equals(typeProperty.GetString(), type, StringComparison.Ordinal)
            && (name is null
                || (frame.TryGetProperty("name", out var nameProperty)
                    && string.Equals(nameProperty.GetString(), name, StringComparison.Ordinal)));

    /// <summary>读字符串属性（不存在 / 非字符串 → <c>null</c>，供断言给出可读诊断）。</summary>
    private static string? ReadString(JsonElement frame, string propertyName)
        => frame.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>把帧序压成可读串（断言失败时的诊断信息）。</summary>
    private static string Describe(List<JsonElement> frames)
        => string.Join(
            " → ",
            frames.Select(frame =>
                frame.TryGetProperty("type", out var type) ? type.GetString() : "<no-type>"));

    private static string Truncate(string text)
        => text.Length <= 2000 ? text : string.Concat(text.AsSpan(0, 2000), "…（截断）");
}
