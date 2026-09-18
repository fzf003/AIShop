using System.Reflection;
using System.Text.Json;
using AIShop.Core.Entities;
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

namespace AIShop.Service.Tests;

/// <summary>
/// S2：<see cref="RecommendationToolProvider"/> 的「可推送负载」入口（<c>TryBuildPushPayloadAsync</c>）
/// 与推送门控纯函数 <c>ShouldPush</c>，以及**工具契约零回归**。
///
/// 三段：
/// ① 契约零回归 —— 工具入口返回值与改动前的**逐字节**快照一致（含 <c>hasRecommendation=false</c> 兜底分支），
///    且构造依赖仍为 4 个、不含被禁服务；
/// ② 推送门控 —— 正例（命中关键词且推荐非空）形状与工具结果同构；两条**独立**否分支（无关键词命中 / 推荐列表为空）；
/// ③ 身份缺失 —— 推送返回 <c>null</c>，且**不是**工具路径那条「无法确定用户身份」的说明性负载。
/// </summary>
public sealed class RecommendationPushPayloadTests
{
    private const string TestUser = "marla";

    /// <summary>无关键词命中时 RecommendationService 的精选兜底文案（口径单一来源）。</summary>
    private const string CuratedFallbackMessage = "为您精选商品";

    // ---------- 改动前基线快照 ----------
    // 以下 5 条字面量是**重构前**（本工单改动前）对当前 RecommendationToolProvider 的实测输出，
    // 逐字节冻结。用途：重构（抽私有 builder）后工具入口必须一字不差 —— 这是「工具契约零回归」的硬证据。
    // （JsonSerializerDefaults.Web 的默认编码器会转义非 ASCII，故此处为纯 ASCII 的 \uXXXX 形式。）

    private const string BaselineQueryHit =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u7535\u5B50\u4EA7\u54C1"",""\u5065\u5EB7""],""products"":[{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":8,""name"":""\u4E0D\u9508\u94A2\u4FDD\u6E29\u6C34\u74F6"",""category"":""\u914D\u4EF6"",""price"":24.99,""emoji"":""\uD83D\uDCA7"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":14,""name"":""\u690D\u7269\u86CB\u767D\u7C89"",""category"":""\u5065\u5EB7"",""price"":39.99,""emoji"":""\uD83D\uDCAA"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""}]}";

    private const string BaselineCuratedFallback =
        @"{""message"":""\u4E3A\u60A8\u7CBE\u9009\u5546\u54C1"",""hasRecommendation"":false,""categories"":[],""products"":[]}";

    private const string BaselinePreferenceOnly =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u7535\u5B50\u4EA7\u54C1"",""\u5065\u5EB7""],""products"":[{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":8,""name"":""\u4E0D\u9508\u94A2\u4FDD\u6E29\u6C34\u74F6"",""category"":""\u914D\u4EF6"",""price"":24.99,""emoji"":""\uD83D\uDCA7"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":14,""name"":""\u690D\u7269\u86CB\u767D\u7C89"",""category"":""\u5065\u5EB7"",""price"":39.99,""emoji"":""\uD83D\uDCAA"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""}]}";

    private const string BaselineIdentityMissing =
        @"{""message"":""\u65E0\u6CD5\u786E\u5B9A\u7528\u6237\u8EAB\u4EFD"",""hasRecommendation"":false,""categories"":[],""products"":[]}";

    private const string BaselineQueryAndPreference =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u7535\u5B50\u4EA7\u54C1"",""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u5065\u5EB7""],""products"":[{""id"":4,""name"":""\u65E0\u7EBF\u964D\u566A\u8033\u673A"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":249.99,""emoji"":""\uD83C\uDFA7"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":7,""name"":""\u590D\u53E4\u9ED1\u80F6\u5531\u7247\u673A"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\uD83C\uDFB5"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":15,""name"":""\u65E0\u7EBF\u5145\u7535\u677F"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":29.99,""emoji"":""\uD83D\uDD0B"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""}]}";

    // ---------- ① 工具契约零回归 ----------

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenQueryHitsCatalog()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselineQueryHit, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenCuratedFallbackApplies()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var json = await harness.Provider.RecommendProductsAsync("帮我随便看看");

        Assert.Equal(BaselineCuratedFallback, json);
        // 正向锚点：兜底分支确为 hasRecommendation=false（防「与快照相等」因两份都退化成别的东西而空转）
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("hasRecommendation").GetBoolean());
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenPreferenceDrivesRecommendation()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselinePreferenceOnly, await harness.Provider.RecommendProductsAsync("帮我随便看看"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenQueryAndPreferenceMixed()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselineQueryAndPreference, await harness.Provider.RecommendProductsAsync("我想买耳机"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenCurrentUserMissing()
    {
        using var harness = new Harness();   // Accessor 未绑定用户

        Assert.Equal(BaselineIdentityMissing, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));
    }

    [Fact]
    public void ShouldKeepFourConstructorDependencies_WithoutProhibitedServices()
    {
        var parameterTypes = typeof(RecommendationToolProvider)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        // 推送入口**不得**新增依赖：仍是 4 个，且类型逐个钉死
        Assert.Equal(4, parameterTypes.Count);
        string[] expectedTypes = ["IServiceScopeFactory", "ICurrentUserAccessor", "IMemoryStore", "IMemoryCache"];
        Assert.Equal(expectedTypes, parameterTypes.Select(type => type.Name));

        // 零大模型调用的硬约束（IMemoryService 装配了 LlmReranker 精排）
        Assert.DoesNotContain(typeof(IChatClient), parameterTypes);
        Assert.DoesNotContain(typeof(IMemoryService), parameterTypes);
    }

    // ---------- ② 推送门控 ----------

    [Fact]
    public async Task ShouldBuildPushPayload_WhenKeywordHitsAndRecommendationNonEmpty()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var toolJson = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var push = await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋");

        var payload = Assert.IsType<JsonElement>(push);   // 非 null（JsonElement 是值类型，须显式判空后取值）
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        var message = payload.GetProperty("message");
        Assert.Equal(JsonValueKind.String, message.ValueKind);
        Assert.True(payload.GetProperty("hasRecommendation").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("categories").EnumerateArray().ToArray());

        var products = payload.GetProperty("products").EnumerateArray().ToList();
        Assert.InRange(products.Count, 1, 6);   // Top-6 截断口径不变

        string[] expectedFields = ["category", "emoji", "id", "name", "price", "reason"];
        foreach (var product in products)
        {
            var fieldNames = product.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedFields, fieldNames);
        }

        // 同 query / 同偏好 → 推送负载与工具结果**逐字节同构**（同一 builder 的唯一证据）
        Assert.Equal(toolJson, payload.GetRawText());
    }

    [Fact]
    public void ShouldPush_WhenKeywordHitsAndProductsNonEmpty()
    {
        Assert.True(RecommendationToolProvider.ShouldPush(["跑步"], PayloadWithProducts()));
    }

    [Fact]
    public async Task ShouldNotPush_WhenChatRoundHasNoKeywordHit()
    {
        // 关键：本轮**有**偏好（故推荐列表非空），使「关键词未命中」成为唯一的阻断条件。
        // 否则精选兜底的 products 恒为空，本条与下一条就分不出是哪半句在起作用（断言空转）。
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        // 正向锚点：同一依据经工具路径确实算出了非空推荐 —— 证明确有东西可推，只是门控拦住了它
        var toolJson = await harness.Provider.RecommendProductsAsync("你好呀");
        Assert.NotEmpty(JsonDocument.Parse(toolJson).RootElement.GetProperty("products").EnumerateArray());

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("你好呀"));
        // 纯函数分支（与上条同源，独立可测）：关键词为空 → 不推
        Assert.False(RecommendationToolProvider.ShouldPush([], PayloadWithProducts()));
    }

    [Fact]
    public async Task ShouldNotPush_WhenKeywordHitsButRecommendationIsEmpty()
    {
        // 空商品目录：关键词**命中**（白名单匹配与目录无关）但算不出任何商品 → 推荐列表为空
        using var harness = new Harness(products: []);
        harness.Accessor.SetCurrentUser(TestUser);

        // 正向锚点：工具路径确实走到了「命中关键词但无商品」的兜底负载（而非「关键词没命中」）
        var toolJson = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var toolRoot = JsonDocument.Parse(toolJson).RootElement;
        Assert.False(toolRoot.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal(CuratedFallbackMessage, toolRoot.GetProperty("message").GetString());
        Assert.Empty(toolRoot.GetProperty("products").EnumerateArray());

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋"));
        // 纯函数分支：关键词非空但推荐为空 → 不推（防「兜底文案覆盖上一次面板」）
        Assert.False(RecommendationToolProvider.ShouldPush(["跑步"], PayloadWithProducts([])));
    }

    // ---------- ③ 身份缺失 ----------

    [Fact]
    public async Task ShouldReturnNullAndNotIdentityMissingPayload_WhenCurrentUserMissing()
    {
        using var harness = new Harness();   // Accessor 未绑定用户

        // 正向锚点：工具路径在此场景下**确实**产出「无法确定用户身份」这条说明性负载……
        Assert.Equal(BaselineIdentityMissing, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));

        // ……而推送入口返回 null：身份问题不是推荐，不得推给面板覆盖上一次内容
        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋"));
    }

    // ---------- 夹具 ----------

    private static Memory Preference(string text)
        => new() { Id = Guid.NewGuid().ToString(), Text = text, UserId = TestUser };

    private static RecommendationToolProvider.RecommendationPayload PayloadWithProducts(
        IReadOnlyList<RecommendationToolProvider.RecommendedProduct>? products = null)
        => new(
            "根据您的兴趣，为您推荐：",
            true,
            ["鞋类"],
            products ?? [new RecommendationToolProvider.RecommendedProduct(3, "专业跑鞋", "鞋类", 129.99m, "👟", "因为你提到「跑步」")]);

    /// <summary>
    /// 测试装配（沿用 RecommendationToolProviderTests 的口径）：替身 <see cref="IProductRepository"/> +
    /// 真实 <see cref="ProductCatalog"/> + 真实 <see cref="RecommendationService"/>，provider 经 DI 解析。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;

        public Harness(IReadOnlyList<Memory>? memories = null, IReadOnlyList<Product>? products = null)
        {
            var repository = Substitute.For<IProductRepository>();
            repository.GetAll().Returns(products ?? ProductSeedData.Products);

            Store = Substitute.For<IMemoryStore>();
            Store.GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>())
                .Returns(_ => AsAsyncEnumerable(memories ?? []));

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<IProductCatalogService>(_ => new ProductCatalog(repository));
            services.AddScoped<RecommendationService>();
            services.AddSingleton<ICurrentUserAccessor>(Accessor);
            services.AddSingleton(Store);
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
            services.AddSingleton<RecommendationToolProvider>();

            _services = services.BuildServiceProvider();
            Provider = _services.GetRequiredService<RecommendationToolProvider>();
        }

        public ICurrentUserAccessor Accessor { get; } = new CurrentUserAccessor();

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
