using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using AGUI.Server;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-reco-realtime S1 —— AG-UI <c>CUSTOM</c> 帧可行性证明（探针用例，成功后长期保留为回归锚）。
///
/// <para><b>被证实的命题</b>：宿主经 <c>services.Configure&lt;AGUIStreamOptions&gt;(o =&gt; o.MapContent(...))</c>（IOptions 路径）
/// 注册的 mapper，能把一个自定义 <c>AIContent</c>（挂在最外层 <see cref="DelegatingAIAgent"/> 流末追加的
/// <see cref="AgentResponseUpdate"/> 上）转换成 <c>type":"CUSTOM"</c> 的 SSE 帧，且该帧出现在 <c>RUN_FINISHED</c> 之前
/// —— design §4 路线 1 成立。</para>
///
/// <para><b>为什么需要它</b>：安装版 <c>AGUI.Server 0.0.5</c> 的实现体未被反编译，「<c>MapContent</c> 返回的
/// <c>CustomEvent</c> 真能从 <c>/</c> 的 SSE 流发出来」此前只是推断（design §9 第 4 条 / §8 R1）。本套用例把两条
/// 配置路径（IOptions / 备选 RawRepresentation 载体）与一条硬约束一起变成事实。</para>
///
/// <para><b>零 src/ 改动</b>：探针全部经 WAF <c>WithWebHostBuilder(b =&gt; b.ConfigureServices(...))</c> 织入——
/// 模型 seam 换 <see cref="StubModelChatClientFactory"/>（离线免 Key）、keyed <c>AIAgent</c> 重新注册为
/// 「包了探针的生产 agent」、「流选项 mapper」按用例开关注册。</para>
///
/// <para><b>实测硬约束（本套用例最重要的发现）</b>：<c>AGUI.Server</c> 的转换器在遍历每个更新时会<b>先无条件</b>执行
/// <c>JsonSerializer.SerializeToElement(chatResponse, typeof(ChatResponseUpdate))</c>（原始更新快照，用于事件上的
/// <c>RawEvent</c>），再走内容映射。该序列化经 MEAI <c>AIContent</c> 的 <b>JSON 多态</b>配置解析派生类型——
/// 自定义 <c>AIContent</c> 子类<b>未注册为 <c>JsonDerivedType</c> 时该内容永远到不了 mapper</b>。故
/// <see cref="ProbeSetup.MapperWithoutJsonType"/> 专测这一形态，其余 mapper 用例都先注册多态派生类型。
/// （0.0.5 下该 <c>NotSupportedException</c> 会冲出转换器、整条流断连；0.0.6 起被捕获并转成
/// <c>RUN_ERROR{code:"StreamingError"}</c> 终止事件——见本文件 H2 用例。）</para>
/// </summary>
/// <remarks>
/// 挂 <see cref="AguiRequestTestsCollection"/> 串行集合：WAF 真实宿主会 MigrateAsync/播种独立 agui.db，
/// 多宿主并行迁移同一 SQLite 文件会互锁（同 AguiRequestTests / AguiE2ETests 约定）。
/// </remarks>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiCustomEventProbeTests
{
    /// <summary>脚本化模型回复里的 ASCII 标记（正锚点：证明内层流确实产出了文本，探针不是空跑）。</summary>
    private const string ProbeReply = "S1-PROBE 探针回复文本。";

    /// <summary>探针事件名（与生产的 <c>recommendation</c> 区分，便于断言「这帧确实来自我们的 mapper」）。</summary>
    private const string ProbeEventName = "probe";

    /// <summary>探针载荷里的标记字段（归因：CUSTOM 帧的 value 必须等于我们塞进去的 payload）。</summary>
    private const string ProbePayloadText = "S1-CUSTOM-PAYLOAD";

    /// <summary>
    /// 主路径（design §4.4 D 第 2 条，IOptions 路径，S1 工单第 4 条）：<c>POST /</c> 真机形态 <c>RunAgentInput</c>
    /// → SSE 流中出现 <c>type":"CUSTOM"</c> + <c>name":"probe"</c> 的帧，且该帧<b>早于</b> <c>RUN_FINISHED</c>；
    /// 帧 payload 与我们塞入的 <see cref="ProbeContent"/> 逐字段一致（证明 CUSTOM 确由本 mapper 产出）。
    /// </summary>
    [Fact]
    public async Task PostRoot_IOptionsMapper_EmitsCustomFrameBeforeRunFinished()
    {
        var runState = new ProbeRunState();
        using var factory = CreateProbeFactory(ProbeSetup.MapperWithJsonType, runState);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("s1-probe-io"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();

        // 正锚点①：内层流照常吐出文本（探针只「多追加一条」，没有破坏既有帧）
        Assert.Contains(ProbeReply, sse);
        // 正锚点②：探针确实在流末合成了一条更新（SynthesizedUpdateCount 在装饰器内自增）
        Assert.Equal(1, runState.SynthesizedUpdateCount);

        var frames = ParseSseFrames(sse);
        var customIndex = IndexOfFrame(frames, "CUSTOM", ProbeEventName);
        var finishedIndex = IndexOfFrame(frames, "RUN_FINISHED", name: null);

        Assert.True(customIndex >= 0, $"SSE 流应含 name=\"{ProbeEventName}\" 的 CUSTOM 帧。实际帧序：{Describe(frames)}");
        Assert.True(finishedIndex >= 0, $"SSE 流应含 RUN_FINISHED 帧。实际帧序：{Describe(frames)}");
        // S1 核心断言：CUSTOM 是流内元素，必然早于转换器在流末补发的 RUN_FINISHED（design §4.1 帧序承诺）
        Assert.True(
            customIndex < finishedIndex,
            $"CUSTOM 帧必须早于 RUN_FINISHED（实际 CUSTOM@{customIndex}、RUN_FINISHED@{finishedIndex}）。帧序：{Describe(frames)}");

        // 归因断言：CUSTOM 帧的 value 就是我们塞进 ProbeContent 的 JsonElement（逐字段相等）
        var value = frames[customIndex].GetProperty("value");
        Assert.True(value.GetProperty("probe").GetBoolean());
        Assert.Equal(ProbePayloadText, value.GetProperty("text").GetString());
    }

    /// <summary>
    /// 反证（S1 工单第 6 条，长期保留）：<b>不</b>注册 <c>Configure&lt;AGUIStreamOptions&gt;</c> 时，
    /// 同一探针 agent 仍会合成 <see cref="ProbeContent"/>（正锚点①）且本轮流照常以 <c>RUN_FINISHED</c> 收尾（正锚点②），
    /// 但 SSE 里<b>不应</b>出现 CUSTOM 帧——无 mapper 命中则该内容被转换器静默丢弃。
    /// 这证明主用例的 CUSTOM 帧确实由我们的 mapper 产出，而非别处冒出来。
    /// </summary>
    [Fact]
    public async Task PostRoot_WithoutStreamOptionsRegistration_NoCustomFrame()
    {
        var runState = new ProbeRunState();
        using var factory = CreateProbeFactory(ProbeSetup.NoMapperWithJsonType, runState);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("s1-probe-no-mapper"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();

        // 正锚点①：探针确实执行并合成了携带自定义 AIContent 的更新（否则「无 CUSTOM」是空转断言）
        Assert.Equal(1, runState.SynthesizedUpdateCount);
        // 正锚点②：本轮正常收尾（不是中途异常断流导致的「没有 CUSTOM」）
        Assert.Contains(ProbeReply, sse);
        var frames = ParseSseFrames(sse);
        Assert.True(IndexOfFrame(frames, "RUN_FINISHED", name: null) >= 0, $"本轮应正常收尾。帧序：{Describe(frames)}");

        // 反证断言：无 mapper 注册 → 探针的自定义内容被静默丢弃，流里没有 name="probe" 的 CUSTOM 帧。
        // 【口径修正（S5）】不得再断言「SSE 里一个 CUSTOM 帧都没有」——本探针的 keyed AIAgent 是外层再包一层探针的
        // 【生产 keyed factory 产物】，而 S5 起该产物最外层是 RecommendationPushAgent（design §4.4 D 第 1 条）；
        // 本用例的 user 消息含白名单关键词（跑步鞋）→ 生产链路会正常产出 name="recommendation" 的 CUSTOM 帧（R1，
        // 该共存本身即「装配生效」的旁证）。该帧与本反证无关，故按事件名精确排除，而非全局禁 CUSTOM。
        Assert.Equal(-1, IndexOfFrame(frames, "CUSTOM", ProbeEventName));
        Assert.DoesNotContain($"\"name\":\"{ProbeEventName}\"", sse);
    }

    /// <summary>
    /// 硬约束刻画（H2，2026-09-20 随 <c>AGUI.Server 0.0.5 → 0.0.6</c> 更新）：自定义 <c>AIContent</c> 子类
    /// <b>未</b>注册进 MEAI <c>AIContent</c> 的 JSON 多态派生类型时，转换器遍历更新时的<b>无条件原始快照序列化</b>
    /// （<c>SerializeToElement(chatResponse, typeof(ChatResponseUpdate))</c>）仍会抛 <c>NotSupportedException</c>，
    /// 该内容因此<b>永远走不到 <c>MapContent</c></b>；但 0.0.6 起转换器捕获该异常并转成终止事件
    /// <c>RUN_ERROR</c>（<c>code = "StreamingError"</c>）—— 流不再断连（0.0.5 会直接断，客户端表现为
    /// <c>HttpRequestException</c>）。本用例把新形态钉住：将来上游再改（或我们误删注册）都会立刻变红。
    /// </summary>
    [Fact]
    public async Task PostRoot_CustomContentWithoutJsonDerivedType_EndsWithRunError()
    {
        var runState = new ProbeRunState();
        using var factory = CreateProbeFactory(ProbeSetup.MapperWithoutJsonType, runState);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("s1-probe-no-json-type"), Encoding.UTF8, "application/json"));

        // 0.0.6：序列化失败不再断连——响应正常建立且可完整读取
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();

        // 探针确实跑到了「合成更新」这一步（失败点在它之后，而非流程根本没走到）
        Assert.Equal(1, runState.SynthesizedUpdateCount);

        var frames = ParseSseFrames(sse);

        // 终止事件是 RUN_ERROR 而非 RUN_FINISHED（未注册类型的快照序列化失败被转换器捕获）
        var errorIndex = IndexOfFrame(frames, "RUN_ERROR", name: null);
        Assert.True(
            errorIndex >= 0,
            $"未注册多态类型时本轮应以 RUN_ERROR 收尾。实际帧序：{Describe(frames)}");
        Assert.True(
            IndexOfFrame(frames, "RUN_FINISHED", name: null) < 0,
            $"RUN_ERROR 已是终止事件，其后不应再出现 RUN_FINISHED。实际帧序：{Describe(frames)}");

        // 归因：错误来自转换器内置的流式失败出口，不是我们的 mapper 产出的
        Assert.Equal("StreamingError", frames[errorIndex].GetProperty("code").GetString());

        // 该内容从未走到 MapContent → 不会有 name="probe" 的 CUSTOM 帧
        Assert.Equal(-1, IndexOfFrame(frames, "CUSTOM", ProbeEventName));
    }

    /// <summary>
    /// 备选注入路线（不依赖 <c>AGUIStreamOptions</c>，也不需要 JSON 多态注册）：探针合成
    /// <see cref="AgentResponseUpdate.RawRepresentation"/> 指向一个 <see cref="ChatResponseUpdate"/>，
    /// 后者的 <c>RawRepresentation</c> 即目标 <see cref="BaseEvent"/>。转换器遇到「原始表示已是 <see cref="BaseEvent"/>」
    /// 的更新会<b>原样透出该事件并 continue</b>，完全绕开 <c>AIContent</c> 多态序列化与 <c>MapContent</c>。
    /// </summary>
    [Fact]
    public async Task PostRoot_RawRepresentationCarriedEvent_EmitsCustomFrameBeforeRunFinished()
    {
        var runState = new ProbeRunState();
        using var factory = CreateProbeFactory(ProbeSetup.RawEventCarrier, runState);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("s1-probe-carrier"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Contains(ProbeReply, sse);
        Assert.Equal(1, runState.SynthesizedUpdateCount);

        var frames = ParseSseFrames(sse);
        var customIndex = IndexOfFrame(frames, "CUSTOM", ProbeEventName);
        var finishedIndex = IndexOfFrame(frames, "RUN_FINISHED", name: null);

        Assert.True(customIndex >= 0, $"SSE 流应含 name=\"{ProbeEventName}\" 的 CUSTOM 帧。实际帧序：{Describe(frames)}");
        Assert.True(finishedIndex >= 0, $"SSE 流应含 RUN_FINISHED 帧。实际帧序：{Describe(frames)}");
        Assert.True(
            customIndex < finishedIndex,
            $"CUSTOM 帧必须早于 RUN_FINISHED（实际 CUSTOM@{customIndex}、RUN_FINISHED@{finishedIndex}）。帧序：{Describe(frames)}");

        var value = frames[customIndex].GetProperty("value");
        Assert.True(value.GetProperty("probe").GetBoolean());
        Assert.Equal(ProbePayloadText, value.GetProperty("text").GetString());
    }

    /// <summary>探针装配形态（决定注册什么、探针合成哪种更新）。</summary>
    private enum ProbeSetup
    {
        /// <summary>注册 IOptions mapper + 注册自定义内容的 JSON 多态派生类型（主路径）。</summary>
        MapperWithJsonType,

        /// <summary>注册 IOptions mapper，但<b>不</b>注册 JSON 多态派生类型（刻画上游硬约束）。</summary>
        MapperWithoutJsonType,

        /// <summary>不注册 mapper，注册 JSON 多态（反证：内容被静默丢弃）。</summary>
        NoMapperWithJsonType,

        /// <summary>什么都不注册，探针经 <c>RawRepresentation</c> 载体直发 <c>BaseEvent</c>（备选路线）。</summary>
        RawEventCarrier,
    }

    /// <summary>
    /// 装配 WAF 探针宿主：<see cref="IModelChatClientFactory"/> 换离线 stub（免 Key）→ keyed <c>AIAgent</c>
    /// 重新注册为「生产 agent（原 factory 产物）外包 <see cref="ProbeAgent"/>」→ 按 <paramref name="setup"/>
    /// 注册流选项 mapper / JSON 多态派生类型；会话 store 覆写到临时库避免跨用例串扰。
    /// </summary>
    private static WebApplicationFactory<Program> CreateProbeFactory(ProbeSetup setup, ProbeRunState runState)
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), $"agui_probe_{Guid.NewGuid():N}.db");
        var sessionConnection = $"Data Source={sessionPath}";
        var useCarrier = setup == ProbeSetup.RawEventCarrier;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // 模型 seam：stub 工厂让 RouterChatClient 的每一轮底层都是脚本化 mock（离线、免 Key，同 AguiRequestTests）
                services.RemoveAll<IModelChatClientFactory>();
                services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(CreateMockChatClient()));

                // 会话库隔离：覆写 Program AddAguiSessionStore 的默认 store（具体实例 + keyed 映射，同 AguiRequestTests）
                services.RemoveAll<AgentSessionStore>();
                services.RemoveAll<SqliteAgentSessionStore>();
                services.AddSingleton(new SqliteAgentSessionStore(sessionConnection));
                services.AddKeyedSingleton<AgentSessionStore>(AGUIShoppingAgent.AgentName,
                    static (sp, _) => sp.GetRequiredService<SqliteAgentSessionStore>());

                // 探针装饰器：捕获 Program 注册的 keyed AIAgent 原始 factory（= 生产装配路径，含 RouterChatClient /
                // ReplySanitizingChatClient / OTel 包装），RemoveAll 后以「同一 factory 产物 + 最外层探针」重新注册。
                // 位置必须在最外层：OpenTelemetryAgent 会经 MEAI 遥测客户端把内层 agent 当 IChatClient 走一遍
                // 序列化/还原（design §3.3），自定义内容套在其内层有被丢弃的风险。
                var originalDescriptor = services.Last(descriptor =>
                    descriptor.ServiceType == typeof(AIAgent)
                    && Equals(descriptor.ServiceKey, AGUIShoppingAgent.AgentName));
                var originalFactory = originalDescriptor.KeyedImplementationFactory;
                Assert.NotNull(originalFactory);

                services.RemoveAll<AIAgent>();
                services.AddKeyedSingleton<AIAgent>(AGUIShoppingAgent.AgentName, (sp, key) =>
                    new ProbeAgent((AIAgent)originalFactory(sp, key), runState, useCarrier));

                if (setup is ProbeSetup.MapperWithJsonType or ProbeSetup.NoMapperWithJsonType)
                    RegisterProbeContentPolymorphism(services);

                if (setup is ProbeSetup.MapperWithJsonType or ProbeSetup.MapperWithoutJsonType)
                {
                    // IOptions 路径（design §4.4 D 第 2 条）：MapAGUIServer 的请求 lambda 读
                    // RequestServices.GetService<IOptions<AGUIStreamOptions>>()?.Value，故此注册即生效。
                    services.Configure<AGUIStreamOptions>(options => options.MapContent(content =>
                        content is ProbeContent probe
                            ?
                            [
                                new CustomEvent
                                {
                                    Name = ProbeContent.EventName,
                                    Value = probe.Payload,
                                }
                            ]
                            : null));
                }
            });
        });
    }

    /// <summary>
    /// 把探针内容类型注册进 MEAI <c>AIContent</c> 的 JSON 多态派生类型表——AG-UI 转换器对每个更新做的
    /// 原始快照序列化需要它（见类注释的「实测硬约束」）。做法 = 给宿主 HTTP JSON 选项的 resolver 链挂一个
    /// <c>JsonTypeInfo</c> 修饰器，在 <c>AIContent</c> 的类型信息上重建 <c>PolymorphismOptions</c>
    /// （复制既有派生类型后追加探针类型）。
    /// </summary>
    private static void RegisterProbeContentPolymorphism(IServiceCollection services)
    {
        services.Configure<HttpJsonOptions>(options =>
        {
            var resolver = options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.SerializerOptions.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
            {
                if (typeInfo.Type != typeof(AIContent) || typeInfo.PolymorphismOptions is not { } polymorphism)
                    return;

                if (polymorphism.DerivedTypes.Any(derived => derived.DerivedType == typeof(ProbeContent)))
                    return;

                var merged = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = polymorphism.TypeDiscriminatorPropertyName,
                    IgnoreUnrecognizedTypeDiscriminators = polymorphism.IgnoreUnrecognizedTypeDiscriminators,
                    UnknownDerivedTypeHandling = polymorphism.UnknownDerivedTypeHandling,
                };
                foreach (var derived in polymorphism.DerivedTypes)
                    merged.DerivedTypes.Add(derived);
                merged.DerivedTypes.Add(new JsonDerivedType(typeof(ProbeContent), "probeContent"));
                typeInfo.PolymorphismOptions = merged;
            });
        });
    }

    /// <summary>脚本化文本回复的 chatClient（<c>ChatClientAgent</c> 走流式入口，yield 一次文本增量即结束流）。</summary>
    private static IChatClient CreateMockChatClient()
    {
        var mockChat = Substitute.For<IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, ProbeReply)));
        mockChat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync(ProbeReply));
        return mockChat;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamingTextAsync(string text)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    /// <summary>构造 AG-UI <c>RunAgentInput</c> 形状的请求体（消息 id 唯一 GUID，同 AguiRequestTests）。</summary>
    private static string RunAgentBody(string threadId)
    {
        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = $"m-{Guid.NewGuid():N}",
                    ["role"] = "user",
                    ["content"] = "你好，帮我推荐一双跑步鞋",
                }),
        };

        return root.ToJsonString();
    }

    /// <summary>
    /// 把 SSE 响应体拆成 JSON 帧列表（只取 <c>data:</c> 行并解析；<c>event:</c>/空行忽略）。
    /// 用解析后的结构断言远比 <c>Contains</c> 精确：可定位帧的<b>序号</b>，从而断言 CUSTOM 早于 RUN_FINISHED。
    /// </summary>
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

    /// <summary>返回首个匹配 <paramref name="type"/>（<paramref name="name"/> 非 null 时还需 name 相等）的帧序号；无匹配返回 -1。</summary>
    private static int IndexOfFrame(List<JsonElement> frames, string type, string? name)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (!frame.TryGetProperty("type", out var typeProperty)
                || !string.Equals(typeProperty.GetString(), type, StringComparison.Ordinal))
            {
                continue;
            }

            if (name is null)
                return i;

            if (frame.TryGetProperty("name", out var nameProperty)
                && string.Equals(nameProperty.GetString(), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

    /// <summary>探针执行状态（跨 WAF 工厂闭包传递，供用例断言探针确实在流末合成了更新）。</summary>
    private sealed class ProbeRunState
    {
        /// <summary>探针在流末追加的 <see cref="AgentResponseUpdate"/> 条数。</summary>
        public int SynthesizedUpdateCount { get; set; }
    }

    /// <summary>
    /// 探针内容类型：一个「未命中内置映射」的自定义 <see cref="AIContent"/>，只携带 <see cref="JsonElement"/> 载荷。
    /// 对应生产侧的 <c>RecommendationPushContent</c>（design §4.4 C）。
    /// </summary>
    private sealed class ProbeContent(JsonElement payload) : AIContent
    {
        /// <summary>CUSTOM 事件名（wire 上的 <c>name</c>）。</summary>
        public const string EventName = ProbeEventName;

        /// <summary>事件载荷（原样进 <c>CustomEvent.Value</c>）。</summary>
        public JsonElement Payload { get; } = payload;
    }

    /// <summary>
    /// 探针装饰器：转发内层流式更新（逐条、零改动），内层流结束后追加一条「推送更新」。
    /// 对应生产侧的 <c>RecommendationPushAgent</c>（design §4.4 B）。
    /// </summary>
    /// <param name="innerAgent">内层 agent（= 生产装配产物）。</param>
    /// <param name="runState">合成计数（用例断言「探针真的跑了」）。</param>
    /// <param name="useRawEventCarrier">
    /// true = 走备选路线，合成更新的 <c>RawRepresentation</c> 指向一个其 <c>RawRepresentation</c> 为
    /// <see cref="CustomEvent"/> 的 <see cref="ChatResponseUpdate"/>（转换器原样透出该事件）；
    /// false = 走 <c>MapContent</c> 路线，合成更新携带自定义 <see cref="ProbeContent"/>。
    /// </param>
    private sealed class ProbeAgent(AIAgent innerAgent, ProbeRunState runState, bool useRawEventCarrier)
        : DelegatingAIAgent(innerAgent)
    {
        /// <summary>探针载荷（与生产侧 <c>RecommendationPushContent</c> 的 JsonElement 同构）。</summary>
        private static readonly JsonElement Payload =
            JsonSerializer.SerializeToElement(new { probe = true, text = ProbePayloadText });

        /// <inheritdoc />
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                yield return update;
            }

            runState.SynthesizedUpdateCount++;
            yield return useRawEventCarrier
                ? CarrierUpdate()
                : new AgentResponseUpdate(ChatRole.Assistant, [new ProbeContent(Payload)]);
        }

        /// <summary>
        /// 备选路线载体：<c>AgentResponseUpdate.RawRepresentation</c> 指向的 <see cref="ChatResponseUpdate"/>
        /// 会被 <c>AsChatResponseUpdate()</c> 原样返回，其自身的 <c>RawRepresentation</c> 才是最终事件——
        /// 转换器见「原始表示是 <see cref="BaseEvent"/>」即原样透出（不经内容映射、不做多态序列化）。
        /// </summary>
        private static AgentResponseUpdate CarrierUpdate()
        {
            var customEvent = new CustomEvent { Name = ProbeEventName, Value = Payload };
            return new AgentResponseUpdate
            {
                RawRepresentation = new ChatResponseUpdate { RawRepresentation = customEvent },
            };
        }
    }
}
