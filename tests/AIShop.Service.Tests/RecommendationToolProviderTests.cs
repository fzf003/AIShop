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

namespace AIShop.Service.Tests;

/// <summary>
/// recommend_products 工具（T5 段）：对话关键词 → 推荐链路、JSON 契约与「零大模型调用」护栏。
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
        using var harness = new Harness(throwingMemoryService, throwingChatClient);
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
    }

    // ---------- 夹具 ----------

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ProductsOf(string json) => Parse(json).GetProperty("products");

    private static string? MessageOf(string json) => Parse(json).GetProperty("message").GetString();

    private static bool IsRequired(JsonElement schema, string propertyName)
        => schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array
            && required.EnumerateArray().Any(element => element.GetString() == propertyName);

    /// <summary>
    /// 测试装配：工具 provider 经 DI 容器解析（非直构），使「依赖了被禁服务」在装配期即暴露
    /// （解析到一调用即抛异常的替身）。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;

        public Harness(IMemoryService? memoryService = null, IChatClient? chatClient = null)
        {
            var repository = Substitute.For<IProductRepository>();
            repository.GetAll().Returns(ProductSeedData.Products);

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<IProductCatalogService>(_ => new ProductCatalog(repository));
            services.AddScoped<RecommendationService>();
            services.AddSingleton<ICurrentUserAccessor>(Accessor);
            services.AddSingleton(Substitute.For<IMemoryStore>());
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
            if (memoryService is not null) services.AddSingleton(memoryService);
            if (chatClient is not null) services.AddSingleton(chatClient);
            services.AddSingleton<RecommendationToolProvider>();

            _services = services.BuildServiceProvider();
            Provider = _services.GetRequiredService<RecommendationToolProvider>();
        }

        public ICurrentUserAccessor Accessor { get; } = new CurrentUserAccessor();

        public RecommendationToolProvider Provider { get; }

        public void Dispose() => _services.Dispose();
    }

    /// <summary>任何成员被调用即抛异常的接口替身（证明被禁服务不在工具执行路径上）。
    /// DispatchProxy 要求基类可继承，故此处不能 sealed。</summary>
    private class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"不应调用 {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
    }
}
