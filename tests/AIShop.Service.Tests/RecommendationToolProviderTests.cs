using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AIShop.Core.Interfaces;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Services;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AIShop.Service.Tests;

/// <summary>
/// recommend_products 工具（T5 段：对话关键词 → 推荐链路、JSON 契约与「零大模型调用」护栏；
/// T6 段：偏好记忆直读 + IMemoryCache 缓存 / 确定性 / 降级）。
/// 夹具沿用 RecommendationServiceTests 模式：替身 IProductRepository（返回 <see cref="ProductSeedData.Products"/>）
/// + 真实 ProductCatalog + 真实 RecommendationService 注册进测试用 ServiceCollection。
/// </summary>
public sealed class RecommendationToolProviderTests
{
    private const string TestUser = "marla";

    /// <summary>RecommendationService 在「无关键词无偏好」时的精选兜底文案（口径单一来源）。</summary>
    private const string CuratedFallbackMessage = "为您精选商品";

    /// <summary>身份缺失时的说明性文案。</summary>
    private const string IdentityMissingMessage = "无法确定用户身份";

    /// <summary>
    /// warm（偏好缓存已命中）状态下单次调用的耗时预算。阈值极宽松——正常路径是纯内存计算（亚毫秒级），
    /// 本断言只为拦住「执行路径上偷偷加了一次大模型往返」这一量级的回退（design §8.5）。
    /// </summary>
    private static readonly TimeSpan WarmCallBudget = TimeSpan.FromSeconds(1);

    // ---------- 零大模型调用（spec R9 场景 2）----------

    [Fact]
    public void ShouldNotDependingOnChatClientOrMemoryService_InConstructor()
    {
        // 结构性断言：构造依赖集合不含被禁服务（IChatClient / 可能触发 LLM 精排的 IMemoryService）
        var parameterTypes = typeof(RecommendationToolProvider)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.NotEmpty(parameterTypes);
        Assert.DoesNotContain(typeof(IChatClient), parameterTypes);
        Assert.DoesNotContain(typeof(IMemoryService), parameterTypes);
    }

    [Fact]
    public async Task ShouldReturnNormally_WhenProhibitedServicesThrowOnAnyCall()
    {
        // 以「一被调用即抛异常」的替身注册 IMemoryService / IChatClient：若工具路径上存在大模型调用，这里必炸
        var throwingMemoryService = DispatchProxy.Create<IMemoryService, ThrowingProxy>();
        var throwingChatClient = DispatchProxy.Create<IChatClient, ThrowingProxy>();
        using var harness = new Harness(memoryService: throwingMemoryService, chatClient: throwingChatClient);
        harness.Accessor.SetCurrentUser(TestUser);

        // 前提校验（防空转）：替身确实「一被调用即抛异常」——否则本用例只是空转，
        // 且工具内部若经 scope 解析这两个服务会在此暴露（scope 由同一容器工厂创建）。
        Assert.Throws<InvalidOperationException>(() => throwingChatClient.GetService(typeof(IChatClient)));

        var withQuery = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var withoutQuery = await harness.Provider.RecommendProductsAsync();

        Assert.NotEqual(0, ProductsOf(withQuery).GetArrayLength());
        Assert.Equal(CuratedFallbackMessage, MessageOf(withoutQuery));
    }

    // ---------- 输出契约（spec R10 场景 1）----------

    [Fact]
    public async Task ShouldReturnParsableRecommendation_WhenQueryHitsCatalog()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var json = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");

        var root = Parse(json);
        Assert.True(root.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal("根据您的兴趣，为您推荐：", root.GetProperty("message").GetString());

        var products = ProductsOf(json);
        Assert.InRange(products.GetArrayLength(), 1, 6);

        string[] expectedFields = ["category", "emoji", "id", "name", "price", "reason"];
        foreach (var product in products.EnumerateArray())
        {
            var fieldNames = product.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedFields, fieldNames);

            // 理由归属当前对话关键词（prefKeywords 本工单恒空）
            Assert.StartsWith("因为你提到「", product.GetProperty("reason").GetString());
        }

        // 商品字段真实来自商品目录（而非占位值）：跑步鞋 = Id 3 / 鞋类 / ¥129.99 / 👟
        var runningShoe = products.EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == 3);
        Assert.Equal("专业跑鞋", runningShoe.GetProperty("name").GetString());
        Assert.Equal("鞋类", runningShoe.GetProperty("category").GetString());
        Assert.Equal(129.99m, runningShoe.GetProperty("price").GetDecimal());
        Assert.Equal("👟", runningShoe.GetProperty("emoji").GetString());
    }

    // ---------- 无命中兜底（spec R10 场景 2）----------

    [Fact]
    public async Task ShouldFallBackToCuratedList_WhenQueryMatchesNoKeyword()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var json = await harness.Provider.RecommendProductsAsync("帮我随便看看");

        var root = Parse(json);
        Assert.False(root.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal(CuratedFallbackMessage, root.GetProperty("message").GetString());
        Assert.Equal(0, ProductsOf(json).GetArrayLength());
        Assert.Equal(0, root.GetProperty("categories").GetArrayLength());
    }

    // ---------- 身份缺失降级（spec R10 场景 3）----------

    [Fact]
    public async Task ShouldReturnExplanatoryMessage_WhenCurrentUserMissing()
    {
        using var harness = new Harness();   // Accessor 未绑定用户 → CurrentUser 为 null

        var json = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");

        var root = Parse(json);
        Assert.Equal(IdentityMissingMessage, root.GetProperty("message").GetString());
        Assert.Equal(0, ProductsOf(json).GetArrayLength());
        Assert.False(root.GetProperty("hasRecommendation").GetBoolean());
    }

    // ---------- 工具注册（可选参数保真）----------

    [Fact]
    public void ShouldRegisterSingleTool_WithOptionalQueryParameter()
    {
        using var harness = new Harness();

        var tools = harness.Provider.CreateTools();

        var tool = Assert.Single(tools);
        Assert.Equal("recommend_products", tool.Name);

        var function = Assert.IsAssignableFrom<AIFunction>(tool);
        var schema = JsonSerializer.Serialize(function.JsonSchema);
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("properties", out var properties), schema);
        Assert.True(properties.TryGetProperty("query", out _), schema);
        // 方法组注册（非 lambda）才能保住默认值 → query 不是 required（lambda 会让模型不传时调用失败）
        Assert.False(IsRequired(root, "query"), schema);

        // L19：`CancellationToken` 是 AIFunctionFactory 的**约定参数**（由框架注入），不得进 schema ——
        // 否则模型会被要求填一个它根本无法提供的参数。
        Assert.False(properties.TryGetProperty("ct", out _), schema);
        Assert.Single(properties.EnumerateObject());
    }

    /// <summary>
    /// T14：工具描述里的**面向用户的输出规约**必须有断言兜住。
    ///
    /// 盘点 T14 原文：「面板文案约束（『不得表述为登录校验/认证』『不要输出 JSON』）无断言」——
    /// 全 `tests/` grep 无「不要输出 JSON」「不要输出商品编号」（那两句话**只存在于**
    /// `RecommendationToolProvider` 的工具描述里），改动时若被顺手删掉**没有任何测试会红**。
    /// 这两句是用户明确要求的输出规约（不要 Markdown、不要暴露内部编号），属于对外契约。
    /// </summary>
    [Fact]
    public void ToolDescription_CarriesUserFacingOutputConstraints()
    {
        using var harness = new Harness();

        var tool = Assert.Single(harness.Provider.CreateTools());

        Assert.Contains("不要输出 JSON", tool.Description);
        Assert.Contains("不要输出商品编号", tool.Description);
    }

    // ---------- 偏好进入推荐（spec R11 场景 1）----------

    [Fact]
    public async Task ShouldRecommendFromPreference_WhenQueryHitsNoKeyword()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        var json = await harness.Provider.RecommendProductsAsync("帮我随便看看");

        var root = Parse(json);
        Assert.True(root.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal("根据您的兴趣，为您推荐：", root.GetProperty("message").GetString());

        // 「用户喜欢跑步」抽出偏好关键词（健身 / 跑步 / 运动）→ 匹配出的商品进入推荐，
        // 且理由归属「偏好」（当前对话无任何命中）
        var products = ProductsOf(json).EnumerateArray().ToList();
        Assert.NotEmpty(products);
        Assert.All(products, product =>
            Assert.StartsWith("根据你的偏好「", product.GetProperty("reason").GetString()));

        var runningShoe = products.Single(product => product.GetProperty("id").GetInt32() == 3);
        Assert.Equal("根据你的偏好「健身」", runningShoe.GetProperty("reason").GetString());

        // 记忆读取按用户名过滤（与写入侧 MemoryContextProvider 的 UserId=username 口径一致）
        harness.Store.Received(1).GetAllAsync(
            Arg.Is<MemoryFilter?>(filter => filter != null && filter.UserId == TestUser),
            Arg.Any<CancellationToken>());
    }

    // ---------- 偏好读取命中缓存（spec R11 场景 2）----------

    [Fact]
    public async Task ShouldReadMemoryOnce_WhenPreferenceCacheIsWarm()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        var first = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var second = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");

        // 第二次走 IMemoryCache，不再全表枚举该用户记忆；两次结果完全一致
        harness.Store.Received(1).GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>());
        Assert.Equal(first, second);
    }

    // ---------- 记忆读取失败降级（spec R11 场景 3）----------

    [Fact]
    public async Task ShouldDegradeToCuratedFallbackAndWarn_WhenMemoryReadThrows()
    {
        using var harness = new Harness(store: FailingStore());
        harness.Accessor.SetCurrentUser(TestUser);

        var sink = new CollectingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        string json;
        try
        {
            json = await harness.Provider.RecommendProductsAsync("帮我随便看看");
        }
        finally
        {
            Log.Logger = original;
        }

        // 降级：不抛异常，退化为「无对话关键词 + 无偏好 → 精选兜底」
        var root = Parse(json);
        Assert.False(root.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal(CuratedFallbackMessage, root.GetProperty("message").GetString());
        Assert.Equal(0, ProductsOf(json).GetArrayLength());

        // 降级不静默：记录一条含用户名的 Warning
        var warning = Assert.Single(sink.Events, e =>
            e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(TestUser, StringComparison.Ordinal));
        Assert.Contains("记忆偏好读取失败", warning.MessageTemplate.Text);
    }

    // ---------- 确定性：不依赖存储枚举顺序（spec R11 第 2 段）----------

    [Fact]
    public async Task ShouldProduceIdenticalResult_WhenMemoryEnumerationOrderDiffers()
    {
        Memory[] memories = [Preference("用户喜欢跑步"), Preference("用户想买耳机")];

        using var first = new Harness(memories: memories);
        using var reversed = new Harness(memories: [.. memories.Reverse()]);
        first.Accessor.SetCurrentUser(TestUser);
        reversed.Accessor.SetCurrentUser(TestUser);

        var firstJson = await first.Provider.RecommendProductsAsync("帮我随便看看");
        var reversedJson = await reversed.Provider.RecommendProductsAsync("帮我随便看看");

        // 同一记忆集合、不同枚举顺序 → 偏好关键词与推荐顺序完全一致
        Assert.Equal(firstJson, reversedJson);
        Assert.NotEmpty(ProductsOf(firstJson).EnumerateArray());
        Assert.StartsWith("根据你的偏好「", ProductsOf(firstJson).EnumerateArray().First().GetProperty("reason").GetString());
    }

    // ---------- L10：截断按相关性，不再按 UTF-16 码点 ----------

    /// <summary>
    /// L10：单轮命中超过 5 个关键词时，参与推荐的是**用户先说到的**那些，而不是码点最小的那些。
    ///
    /// 例句中「跑步」出现在最前（索引 3），由它一并命中的 健身 / 运动 同点；咖啡、巧克力 次之；
    /// 耳机 / 手表 在句尾。旧口径（<c>OrderBy(keyword, Ordinal)</c>）会选出
    /// <c>健身 / 咖啡 / 巧克力 / 手表 / 耳机</c> —— **句中靠后的反而入选**，正是 L10 描述的问题。
    ///
    /// 反证：把 <c>MatchKeywords</c> 的排序改回纯序数，本用例必须变红。
    /// </summary>
    [Fact]
    public void ShouldRankQueryKeywordsByMentionOrder_WhenMoreThanFiveMatch()
    {
        var keywords = RecommendationToolProvider.MatchKeywords(
            "我想买跑步鞋，还要咖啡和巧克力，再推荐个耳机和手表");

        Assert.Equal(["健身", "跑步", "运动", "咖啡", "巧克力"], keywords);
    }

    /// <summary>
    /// L10：偏好关键词按**跨记忆出现频次**取前 5 —— 反复提到的偏好比只提过一次的更巩固。
    ///
    /// 同频次按序数兜底，故结果与字典枚举顺序无关（频次本身是集合级统计量）——
    /// spec R11 第 2 段的确定性要求因此仍然成立。
    ///
    /// 反证：把 <c>TopPreferenceKeywords</c> 的排序改回纯序数，本用例必须变红
    /// （旧口径会选出 <c>健身 / 咖啡 / 瑜伽 / 耳机 / 跑步</c>，把出现 3 次的「跑步」排到最后）。
    /// </summary>
    [Fact]
    public void ShouldRankPreferenceKeywordsByFrequency_WhenMoreThanFiveMatch()
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["健身"] = 1,
            ["跑步"] = 3,
            ["运动"] = 1,
            ["瑜伽"] = 1,
            ["耳机"] = 2,
            ["音乐"] = 1,
            ["咖啡"] = 1,
        };

        var keywords = RecommendationToolProvider.TopPreferenceKeywords(frequencies);

        Assert.Equal(["跑步", "耳机", "健身", "咖啡", "瑜伽"], keywords);
    }

    /// <summary>
    /// L19：工具入口必须把**调用方的 token 透传下去**，而不是硬用 <c>CancellationToken.None</c>
    /// —— 否则客户端断开后，工具路径那条偏好全表枚举仍会跑完。
    ///
    /// 断言「透传」而非「抛异常」：本 Harness 的记忆存储是替身，无论 ct 是否取消都会照常返回，
    /// 构造不出「抛」的差异；而「store 收到的是不是调用方那个 token」是同一缺陷的直接证据。
    ///
    /// 反证：把 <c>RecommendProductsAsync</c> 里那两个实参改回 <c>CancellationToken.None</c>，
    /// 本用例必须变红（收到的会是 <c>None</c>）。
    /// </summary>
    [Fact]
    public async Task RecommendProducts_PassesCallerCancellationTokenToMemoryStore()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        using var cts = new CancellationTokenSource();

        await harness.Provider.RecommendProductsAsync("我想买跑步鞋", cts.Token);

        // 偏好缓存未命中 → 直读记忆存储；这一步必须拿到调用方的 token。
        // `GetAllAsync` 返回 IAsyncEnumerable（Received 的断言在调用时即生效，不消费序列）。
        _ = harness.Store.Received(1).GetAllAsync(Arg.Any<MemoryFilter?>(), cts.Token);
    }

    // ---------- 延迟预算（design §8.5 验收②）----------
    [Fact]
    public async Task ShouldStayWithinLatencyBudget_WhenPreferenceCacheIsWarm()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        await harness.Provider.RecommendProductsAsync("我想买跑步鞋");   // 预热偏好缓存

        var stopwatch = Stopwatch.StartNew();
        await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < WarmCallBudget,
            $"warm 状态下单次调用耗时 {stopwatch.ElapsedMilliseconds}ms，超出预算 {WarmCallBudget.TotalMilliseconds}ms");
        harness.Store.Received(1).GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 夹具 ----------

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ProductsOf(string json) => Parse(json).GetProperty("products");

    private static string? MessageOf(string json) => Parse(json).GetProperty("message").GetString();

    private static bool IsRequired(JsonElement schema, string propertyName)
        => schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array
            && required.EnumerateArray().Any(element => element.GetString() == propertyName);

    /// <summary>一条测试用记忆（UserId 与 <see cref="TestUser"/> 一致，对齐 MemoryContextProvider 的写入口径）。</summary>
    private static Memory Preference(string text)
        => new() { Id = Guid.NewGuid().ToString(), Text = text, UserId = TestUser };

    /// <summary>
    /// 替身记忆存储：<c>GetAllAsync</c> **枚举即失败**（与真实 SQLite 失败同形：异常发生在枚举期
    /// <c>MoveNextAsync</c>，而非方法调用期）。
    /// </summary>
    private static IMemoryStore FailingStore()
    {
        var store = Substitute.For<IMemoryStore>();
        store.GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new FailingAsyncEnumerable());
        return store;
    }

    /// <summary>枚举即抛异常的异步序列。</summary>
    private sealed class FailingAsyncEnumerable : IAsyncEnumerable<Memory>
    {
        private const string FailureMessage = "记忆库不可用（测试注入）";

        public IAsyncEnumerator<Memory> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new FailingEnumerator();

        private sealed class FailingEnumerator : IAsyncEnumerator<Memory>
        {
            /// <summary>首轮 <see cref="MoveNextAsync"/> 即抛异常，故本属性不可达。</summary>
            public Memory Current => throw new InvalidOperationException(FailureMessage);

            public ValueTask<bool> MoveNextAsync()
                => throw new InvalidOperationException(FailureMessage);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>收集 Serilog 日志事件的内存 sink（仅测试用；配合临时替换全局 Log.Logger 捕获告警）。</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>
    /// 测试装配：工具 provider 经 DI 容器解析（非直构），使「依赖了被禁服务」在装配期即暴露
    /// （解析到一调用即抛异常的替身）。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;

        public Harness(
            IReadOnlyList<Memory>? memories = null,
            IMemoryStore? store = null,
            IMemoryService? memoryService = null,
            IChatClient? chatClient = null)
        {
            var repository = Substitute.For<IProductRepository>();
            repository.GetAll().Returns(ProductSeedData.Products);

            Store = store ?? StoreReturning(memories ?? []);

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<IProductCatalogService>(_ => new ProductCatalog(repository));
            services.AddScoped<RecommendationService>();
            services.AddSingleton<ICurrentUserAccessor>(Accessor);
            services.AddSingleton(Store);
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
            if (memoryService is not null) services.AddSingleton(memoryService);
            if (chatClient is not null) services.AddSingleton(chatClient);
            services.AddSingleton<RecommendationToolProvider>();

            _services = services.BuildServiceProvider();
            Provider = _services.GetRequiredService<RecommendationToolProvider>();
        }

        public ICurrentUserAccessor Accessor { get; } = new CurrentUserAccessor();

        /// <summary>注入 provider 的记忆存储替身（供断言读取次数 / 过滤条件）。</summary>
        public IMemoryStore Store { get; }

        public RecommendationToolProvider Provider { get; }

        public void Dispose() => _services.Dispose();

        /// <summary>
        /// 替身记忆存储：<c>GetAllAsync</c> 返回**异步序列**（与 <c>SqliteMemoryStore</c> 的签名一致），
        /// 生产代码必须 <c>await foreach</c> 消费；调用次数可经 <c>Received</c> 断言（缓存命中验证）。
        /// </summary>
        private static IMemoryStore StoreReturning(IReadOnlyList<Memory> memories)
        {
            var store = Substitute.For<IMemoryStore>();
            store.GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>())
                .Returns(_ => AsAsyncEnumerable(memories));
            return store;
        }

        private static async IAsyncEnumerable<Memory> AsAsyncEnumerable(IEnumerable<Memory> memories)
        {
            foreach (var memory in memories)
            {
                await Task.Yield();
                yield return memory;
            }
        }
    }

    /// <summary>任何成员被调用即抛异常的接口替身（证明被禁服务不在工具执行路径上）。
    /// DispatchProxy 要求基类可继承，故此处不能 sealed。</summary>
    private class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"不应调用 {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
    }
}
