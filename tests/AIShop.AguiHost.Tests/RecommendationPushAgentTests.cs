using System.Runtime.CompilerServices;
using System.Text.Json;
using AIShop.AgentTelemetry;
using AIShop.AguiHost.Recommendation;
using AIShop.Core.Interfaces;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Services;
using AIShop.Service.Agui;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-reco-realtime S4：推荐推送装饰器 <c>RecommendationPushAgent</c> 的行为验收（design §4.4 B / §4.3）。
///
/// <para>覆盖五件事：① 命中推送（内层更新逐条原样 + 流末恰 1 条推荐更新）；② 闲聊轮不推（门控落地）；
/// ③ 依据解析（工具 <c>query</c> 优先于本轮用户消息、多次调用取最后一次有效值）；④ 异常降级
/// （内层更新完整送达 + 恰一条 Warning + 无推送）；⑤ 身份缺失不推。另断言基类转发未被破坏
/// （<c>Name</c> / <c>GetService(ChatOptions)</c> / 会话读写），以及**非流式入口不注入 CUSTOM**
/// 的契约（design §9 第 5 条）。</para>
///
/// <para>内层用本文件内的 <see cref="ScriptedAgent"/> 替身（完全控制流内容，不经 FICC，故可精确注入
/// <see cref="FunctionCallContent"/>）；「转发不变」用例则用**真实装配产物**
/// （<see cref="AGUIShoppingAgent.Create"/> + 9 个工具），使该断言确实覆盖生产形态。</para>
///
/// <para>不启动宿主、不落库，故本类不进 <c>AguiRequestTests</c> 串行集合。</para>
/// </summary>
public sealed class RecommendationPushAgentTests
{
    private const string TestUser = "marla";

    /// <summary>本轮用户消息（命中白名单关键词「跑步」，是推荐依据的兜底来源）。</summary>
    private const string UserMessageWithKeyword = "我想买跑步鞋";

    /// <summary>模型调 <c>recommend_products</c> 时传入的另一个关键词查询（命中「耳机」）。</summary>
    private const string ToolQueryEarphones = "我想买耳机";

    // ---------- ① 命中推送 ----------

    /// <summary>
    /// 命中推送：内层流产出 2 条更新 → 装饰器先逐条原样转发（顺序 + 对象引用均不变），
    /// 流末追加**恰好 1 条**携带 <see cref="RecommendationPushContent"/> 的更新，
    /// 其载荷与「同依据直接调 provider」的负载逐字节相同（同一 builder 的证据）。
    /// </summary>
    [Fact]
    public async Task ShouldForwardInnerUpdatesThenPushExactlyOne_WhenRoundHasKeyword()
    {
        using var harness = new RecoHarness();
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, "正在为您查询…"),
            new AgentResponseUpdate(ChatRole.Assistant, "为您找到几件跑鞋。"),
        };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        var collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]));

        // 内层逐条原样 + 恰好一条合成更新
        Assert.Equal(innerUpdates.Length + 1, collected.Count);
        for (var i = 0; i < innerUpdates.Length; i++)
        {
            // 顺序与引用不变（「零延迟改动、不改对象」的硬证据）
            Assert.Same(innerUpdates[i], collected[i]);
        }

        var pushed = collected[^1];
        var content = Assert.IsType<RecommendationPushContent>(Assert.Single(pushed.Contents));
        Assert.Equal(JsonValueKind.Object, content.Payload.ValueKind);
        Assert.True(content.Payload.GetProperty("hasRecommendation").GetBoolean());
        Assert.NotEmpty(content.Payload.GetProperty("products").EnumerateArray());

        // 同依据经同一 provider 直接计算的结果与推送载荷逐字节相同（装饰器确实把依据交给了同一个 builder）
        var direct = await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword);
        Assert.NotNull(direct);
        Assert.Equal(direct.Value.GetRawText(), content.Payload.GetRawText());
    }

    /// <summary>
    /// 防漂移：装饰器用来识别「模型调过推荐工具」的工具名，必须与 <see cref="RecommendationToolProvider"/>
    /// 实际注册的工具名一致（两处都是字面量，靠本断言锁死）。
    /// </summary>
    [Fact]
    public void ShouldMatchRegisteredToolName_WhenScanningToolCalls()
    {
        using var harness = new RecoHarness();

        Assert.Equal(RecommendationPushAgent.ToolName, Assert.Single(harness.Provider.CreateTools()).Name);
    }

    // ---------- ② 闲聊轮不推（门控落地）----------

    /// <summary>
    /// 闲聊轮：本轮用户消息不含白名单关键词 → provider 门控不通过（返回 <c>null</c>）→ 装饰器**不追加**任何更新，
    /// 内层两条更新逐条原样透出（面板因此天然保持上一次）。
    /// 正锚点：同一 harness 换个有依据的查询确实能推送 —— 证明「没推」是门控结果，不是环境坏掉。
    /// </summary>
    [Fact]
    public async Task ShouldNotPushInnerUnchanged_WhenRoundHasNoKeyword()
    {
        using var harness = new RecoHarness();
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, "你好呀～"),
            new AgentResponseUpdate(ChatRole.Assistant, "有什么可以帮您？"),
        };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        var collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "你好呀")]));

        Assert.Equal(innerUpdates.Length, collected.Count);
        for (var i = 0; i < innerUpdates.Length; i++)
        {
            Assert.Same(innerUpdates[i], collected[i]);
        }

        Assert.DoesNotContain(collected, update => update.Contents.Any(content => content is RecommendationPushContent));

        // 正锚点：同一 harness 能推送（门控之所以拦住，只是因为本轮无关键词）
        Assert.NotNull(await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword));
    }

    // ---------- ③ 依据解析：工具 query 优先 ----------

    /// <summary>
    /// 工具 <c>query</c> 优先（对应 R3-2）：内层流含 <c>recommend_products(query = "耳机")</c>，而本轮用户消息是
    /// 「跑步鞋」→ 推送载荷必须与**以 query 为依据**直接算出的结果相同、且与以用户消息为依据的结果不同。
    /// 正锚点：两种依据的直接结果确实互不相同，否则「以 X 为依据」的断言会空转。
    /// </summary>
    [Fact]
    public async Task ShouldUseToolQueryAsBasis_WhenModelCalledRecommendProducts()
    {
        using var harness = new RecoHarness();
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, [FunctionCall(ToolQueryEarphones)]),
            new AgentResponseUpdate(ChatRole.Assistant, "根据您的对话，为您推荐几件商品。"),
        };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        var collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]));

        var pushed = Assert.IsType<RecommendationPushContent>(Assert.Single(collected[^1].Contents));
        var byToolQuery = await harness.Provider.TryBuildPushPayloadAsync(ToolQueryEarphones);
        var byUserMessage = await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword);
        Assert.NotNull(byToolQuery);
        Assert.NotNull(byUserMessage);

        // 正锚点：两条依据产出不同负载（否则下面的等式无从区分用的是哪条）
        Assert.NotEqual(byToolQuery.Value.GetRawText(), byUserMessage.Value.GetRawText());

        Assert.Equal(byToolQuery.Value.GetRawText(), pushed.Payload.GetRawText());
        Assert.NotEqual(byUserMessage.Value.GetRawText(), pushed.Payload.GetRawText());
    }

    /// <summary>
    /// 多次调用取**最后一次有效值**：两个 <c>recommend_products</c> 调用（query 依次为「耳机」「跑步鞋」），
    /// 本轮用户消息无关键词 → 依据必须是后一个。
    /// </summary>
    [Fact]
    public async Task ShouldUseLastValidToolQuery_WhenToolCalledMoreThanOnce()
    {
        using var harness = new RecoHarness();
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, [FunctionCall(ToolQueryEarphones)]),
            new AgentResponseUpdate(ChatRole.Assistant, [FunctionCall(UserMessageWithKeyword)]),
            new AgentResponseUpdate(ChatRole.Assistant, "已为您推荐。"),
        };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        var collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "随便看看")]));

        var pushed = Assert.IsType<RecommendationPushContent>(Assert.Single(collected[^1].Contents));
        var expected = await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword);
        Assert.NotNull(expected);
        Assert.Equal(expected.Value.GetRawText(), pushed.Payload.GetRawText());
    }

    // ---------- ④ 异常降级 ----------

    /// <summary>
    /// 异常降级（对应 R4-1）：让 provider 内**未被捕获**的路径抛（<c>IMemoryCache.TryGetValue</c> 即抛）→
    /// 内层更新完整送达、异常不外泄、恰好一条 Warning、无推送。
    /// 正锚点：同一 harness 的 provider 直接调用确实抛 —— 证明这条降级路径被真正走到了。
    /// </summary>
    [Fact]
    public async Task ShouldDeliverInnerUpdatesAndWarnOnce_WhenRecommendationThrows()
    {
        using var harness = new RecoHarness(cache: new ThrowingMemoryCache());
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, "好的，"),
            new AgentResponseUpdate(ChatRole.Assistant, "已经帮您处理好了。"),
        };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        // 正锚点：该替身确实让推荐计算抛（未被 provider 内部的记忆读取 catch 覆盖）
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword));

        var sink = new CollectingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        IReadOnlyList<AgentResponseUpdate> collected;
        try
        {
            // 不抛：异常在装饰器内被吞（整条流完整跑完）
            collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]));
        }
        finally
        {
            Log.Logger = original;
        }

        // 内层更新完整送达、无合成更新
        Assert.Equal(innerUpdates.Length, collected.Count);
        for (var i = 0; i < innerUpdates.Length; i++)
        {
            Assert.Same(innerUpdates[i], collected[i]);
        }

        Assert.DoesNotContain(collected, update => update.Contents.Any(content => content is RecommendationPushContent));

        // 降级不静默：恰好一条 Warning，且来自本装饰器的推送失败分支
        var warning = Assert.Single(sink.Events, logEvent => logEvent.Level == LogEventLevel.Warning);
        Assert.Contains("推荐推送计算失败", warning.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    // ---------- ⑤ 身份缺失 ----------

    /// <summary>
    /// 身份缺失（对应 R4-2）：<c>ICurrentUserAccessor.CurrentUser</c> 为 <c>null</c> → provider 返回 <c>null</c> →
    /// 无推送、无异常、内层更新逐条原样。
    /// 正锚点：同一 provider / 同一依据，把用户绑上后确实能推送。
    /// </summary>
    [Fact]
    public async Task ShouldNotPushAndNotThrow_WhenCurrentUserMissing()
    {
        using var harness = new RecoHarness(user: null);
        var innerUpdates = new[] { new AgentResponseUpdate(ChatRole.Assistant, "为您推荐如下。") };
        var agent = new RecommendationPushAgent(new ScriptedAgent("Inner", innerUpdates), harness.Provider);

        var collected = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]));

        Assert.Same(Assert.Single(innerUpdates), Assert.Single(collected));

        // 正锚点：绑上身份后同一依据确实推得出来（证明「没推」源于身份缺失而非无依据）
        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword));
        harness.Accessor.CurrentUser.Returns(TestUser);
        Assert.NotNull(await harness.Provider.TryBuildPushPayloadAsync(UserMessageWithKeyword));
    }

    // ---------- ⑥ 转发不变 ----------

    /// <summary>
    /// 转发不变：装饰器不得 override <c>Name</c> / <c>GetService</c> / 会话读写 —— 用**真实装配产物**
    /// （<see cref="AGUIShoppingAgent.Create"/> 挂 9 个工具）作内层，逐项断言转发到内层。
    /// </summary>
    [Fact]
    public async Task ShouldForwardNameServicesAndSessions_ToInnerAgent()
    {
        using var harness = new RecoHarness();
        var inner = AGUIShoppingAgent.Create(
            Substitute.For<IChatClient>(),
            new CartToolProvider(Substitute.For<IServiceScopeFactory>(), Substitute.For<ICurrentUserAccessor>()),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            recommendationTools: harness.Provider);
        var decorator = new RecommendationPushAgent(inner, harness.Provider);

        // Name / Id 原样转发
        Assert.Equal(AGUIShoppingAgent.AgentName, decorator.Name);
        Assert.Equal(inner.Name, decorator.Name);

        // 服务解析原样转发：工具集仍是内层那 9 个
        var innerOptions = Assert.IsType<ChatOptions>(inner.GetService(typeof(ChatOptions)));
        Assert.Same(innerOptions, decorator.GetService(typeof(ChatOptions)));
        Assert.Equal(9, innerOptions.Tools!.Count);
        Assert.Contains(innerOptions.Tools, tool => tool.Name == RecommendationPushAgent.ToolName);

        // 会话读写经基类转发：创建出来的是内层 ChatClientAgent 的会话，且可序列化 / 反序列化
        var session = await decorator.CreateSessionAsync();
        Assert.NotNull(session);
        var serialized = await decorator.SerializeSessionAsync(session);
        Assert.Equal(JsonValueKind.Object, serialized.ValueKind);
        Assert.NotNull(await decorator.DeserializeSessionAsync(serialized));
    }

    // ---------- ⑦ 非流式入口不注入（契约锁定） ----------

    /// <summary>
    /// 契约锁定（design §9 第 5 条 / <c>RecommendationPushAgent</c> 类注释）：非流式入口
    /// （公开 <c>RunAsync</c> → <c>RunCoreAsync</c>）**不注入** CUSTOM 推荐 —— 装饰器只 override 流式路径，
    /// 非流式由 <c>DelegatingAIAgent</c> 基类原样转发给内层。三条断言各锁一件事：
    /// <list type="number">
    /// <item><b>不抛异常</b>：内层替身默认的「非流式不应被调用」守卫不会触发（走到了正常返回分支）；</item>
    /// <item><b>原样一致</b>：返回结果与内层给出的**同一实例**（<c>Assert.Same</c>），证明装饰器既不重建也不替换响应；</item>
    /// <item><b>无 CUSTOM</b>：结果里没有任何 <see cref="RecommendationPushContent"/> —— 即便装饰器改成**原地追加**到转发来的响应上（此时上一条 <c>Assert.Same</c> 仍会通过），本断言也会抓住。</item>
    /// </list>
    /// 对照（正锚点）：同一装配下流式路径确实推了 CUSTOM —— 证明「非流式没有」是**路径语义**，不是环境坏掉。
    /// </summary>
    [Fact]
    public async Task ShouldForwardInnerResponseWithoutCustom_WhenNonStreamingEntryUsed()
    {
        using var harness = new RecoHarness();

        // 内层：流式产出一条 recommend_products 调用 + 一段文本（命中白名单 → 流式路径必推）；
        //       非流式原样返回一条 assistant 文本响应（不含任何合成内容）。
        var innerUpdates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, [FunctionCall(ToolQueryEarphones)]),
            new AgentResponseUpdate(ChatRole.Assistant, "根据您的对话，为您推荐几件商品。"),
        };
        var innerResponse = new AgentResponse(new ChatMessage(ChatRole.Assistant, "根据您的对话，为您推荐几件商品。"));
        var agent = new RecommendationPushAgent(
            new ScriptedAgent("Inner", innerUpdates, innerResponse),
            harness.Provider);

        // 对照：同一 agent 的流式路径确实推了 CUSTOM（名字与事件名一致）。
        var streamed = await CollectAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]));
        Assert.Contains(streamed, update => update.Contents.Any(content => content is RecommendationPushContent));

        // 非流式：不抛异常（不是守卫分支）→ 返回内层同一实例 → 无任何 CUSTOM 合成内容。
        var result = await agent.RunAsync([new ChatMessage(ChatRole.User, UserMessageWithKeyword)]);

        Assert.Same(innerResponse, result);
        Assert.DoesNotContain(
            result.Messages,
            message => message.Contents.Any(content => content is RecommendationPushContent));
    }

    // ---------- 夹具 ----------

    /// <summary>构造一条 <c>recommend_products</c> 的 <see cref="FunctionCallContent"/>（参数 key = query）。</summary>
    private static FunctionCallContent FunctionCall(string query)
        => new(Guid.NewGuid().ToString("N"), RecommendationPushAgent.ToolName, new Dictionary<string, object?> { ["query"] = query });

    /// <summary>完整枚举一次流式响应（异常会自然外泄 —— 这正是「不炸流」断言要抓的失败形态）。</summary>
    private static async Task<IReadOnlyList<AgentResponseUpdate>> CollectAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var collected = new List<AgentResponseUpdate>();
        await foreach (var update in updates)
        {
            collected.Add(update);
        }

        return collected;
    }

    /// <summary>
    /// 脚本化内层 agent：完全控制流式更新序列（<b>不经</b> FICC / ChatClientAgent），
    /// 使用例可以精确注入 <see cref="FunctionCallContent"/> 与文本增量。会话相关成员在本工单用不到，
    /// 一律抛出（若装饰器漏转发、走到这里，用例会立刻变红）。
    /// </summary>
    /// <param name="name">内层 agent 名（透传断言用）。</param>
    /// <param name="updates">流式路径逐条产出的更新序列。</param>
    /// <param name="nonStreamingResponse">
    /// 非流式入口（<c>RunCoreAsync</c>）的返回值。<b>默认 <c>null</c> 时保持抛异常</b>：
    /// 这是「流式用例不会误走非流式入口」的守卫，只有专测非流式的用例才显式开启（design §9 第 5 条）。
    /// </param>
    private sealed class ScriptedAgent(
        string name,
        IReadOnlyList<AgentResponseUpdate> updates,
        AgentResponse? nonStreamingResponse = null) : AIAgent
    {
        public override string? Name => name;

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            // 默认（未开启开关）= 守卫：证明流式用例不会误走非流式入口。
            // 仅非流式契约用例显式给出响应，此时才作为正常的内层返回值。
            => nonStreamingResponse is null
                ? throw new NotSupportedException("S4 只覆盖流式路径，非流式入口由基类转发（本替身不应被调用）。")
                : Task.FromResult(nonStreamingResponse);

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话读写应由 DelegatingAIAgent 基类转发到内层，本替身不应被调用。");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话读写应由 DelegatingAIAgent 基类转发到内层，本替身不应被调用。");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话读写应由 DelegatingAIAgent 基类转发到内层，本替身不应被调用。");
    }

    /// <summary>
    /// <see cref="IMemoryCache"/> 替身：<c>TryGetValue</c> 即抛 —— 模拟「推荐计算过程中未被 provider
    /// 内部捕获的异常路径」（provider 只对记忆读取失败降级，缓存访问不在其 try 内）。
    /// </summary>
    private sealed class ThrowingMemoryCache : IMemoryCache
    {
        private const string FailureMessage = "S4 用例：IMemoryCache 替身故意抛出";

        public ICacheEntry CreateEntry(object key) => throw new InvalidOperationException(FailureMessage);

        public void Remove(object key) => throw new InvalidOperationException(FailureMessage);

        public bool TryGetValue(object key, out object? value) => throw new InvalidOperationException(FailureMessage);

        public void Dispose()
        {
            // 无资源需要释放
        }
    }

    /// <summary>收集 Serilog 日志事件的内存 sink（配合临时替换全局 <c>Log.Logger</c> 捕获告警）。</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>
    /// 推荐 provider 装配（沿用 S2 的 <c>RecommendationPushPayloadTests</c> 口径：替身仓储 + 真实
    /// <see cref="ProductCatalog"/> / <see cref="RecommendationService"/>，provider 经 DI 解析）。
    /// </summary>
    private sealed class RecoHarness : IDisposable
    {
        private readonly ServiceProvider _services;

        public RecoHarness(string? user = TestUser, IReadOnlyList<Memory>? memories = null, IMemoryCache? cache = null)
        {
            var repository = Substitute.For<IProductRepository>();
            repository.GetAll().Returns(ProductSeedData.Products);

            Accessor = Substitute.For<ICurrentUserAccessor>();
            Accessor.CurrentUser.Returns(user);

            Store = Substitute.For<IMemoryStore>();
            Store.GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>())
                .Returns(_ => AsAsyncEnumerable(memories ?? []));

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<IProductCatalogService>(_ => new ProductCatalog(repository));
            services.AddScoped<RecommendationService>();
            services.AddSingleton<ICurrentUserAccessor>(Accessor);
            services.AddSingleton(Store);
            services.AddSingleton(cache ?? new MemoryCache(new MemoryCacheOptions()));
            services.AddSingleton<RecommendationToolProvider>();

            _services = services.BuildServiceProvider();
            Provider = _services.GetRequiredService<RecommendationToolProvider>();
        }

        public ICurrentUserAccessor Accessor { get; }

        public IMemoryStore Store { get; }

        public RecommendationToolProvider Provider { get; }

        public void Dispose() => _services.Dispose();

        private static async IAsyncEnumerable<Memory> AsAsyncEnumerable(IEnumerable<Memory> memories)
        {
            foreach (var memory in memories)
            {
                await Task.Yield();
                yield return memory;
            }
        }
    }
}
