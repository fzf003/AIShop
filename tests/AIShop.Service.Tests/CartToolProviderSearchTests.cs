using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Services;
using AIShop.Service.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AIShop.Service.Tests;

/// <summary>
/// Task 10 — CartToolProvider.SearchProductAsync 升级为混合检索（design §5.3/§9，AR-1/AR-3/AI-3）。
/// 工具签名与输出格式保持兼容（非 breaking）；mock IRagSearchService 验证委托 + 格式化；
/// 抛异常 / 未注册时回退纯关键词路兜底不崩溃（AI-3）。不调真实 LLM（AI-4）。
/// </summary>
public sealed class CartToolProviderSearchTests
{
    /// <summary>构造被测工具：注入受控 IRagSearchService（null 表示 RAG 未注册的中间态）+ 提供 18 条种子的 scope 工厂。</summary>
    private static CartToolProvider BuildProvider(IRagSearchService? ragSearchService)
    {
        var services = new ServiceCollection();
        // 关键词路兜底需要读商品库：IProductRepository 在应用中为 Scoped，测试注册共享实例返回 18 条种子
        services.AddScoped<IProductRepository>(_ => new FakeProductRepository());
        var provider = services.BuildServiceProvider();

        return new CartToolProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CurrentUserAccessor(),
            ragSearchService);
    }

    [Fact]
    public async Task SearchProductAsync_WhenRagServiceReturnsHits_FormatsOutputCompatibly()
    {
        // AR-1/AR-3：委托 IRagSearchService 后输出格式仍为 `#Id Name — ¥Price`（§9 兼容口径）
        var ragSearch = Substitute.For<IRagSearchService>();
        ragSearch.SearchProductsAsync("咖啡", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProductSearchHit>>(
            [
                new ProductSearchHit(5, "意式浓缩咖啡机", "厨房用品", 349.99m, 0),
                new ProductSearchHit(3, "专业跑鞋", "鞋类", 129.99m, 0),
            ]));

        var result = await BuildProvider(ragSearch).SearchProductAsync("咖啡");

        var expected = "找到 2 个商品：\n#5 意式浓缩咖啡机 — ¥349.99\n#3 专业跑鞋 — ¥129.99";
        Assert.Equal(expected, result);

        // 委托确已到达 IRagSearchService（工具层不再自建关键词匹配）
        await ragSearch.Received(1).SearchProductsAsync("咖啡", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchProductAsync_WhenRagServiceReturnsEmpty_ReturnsNotFoundMessage()
    {
        // AR-1 兼容：两路均空时返回 `未找到包含「{keyword}」的商品`（与变更前文案一致）
        var ragSearch = Substitute.For<IRagSearchService>();
        ragSearch.SearchProductsAsync("咖啡机", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProductSearchHit>>([]));

        var result = await BuildProvider(ragSearch).SearchProductAsync("咖啡机");

        Assert.Equal("未找到包含「咖啡机」的商品", result);
    }

    [Fact]
    public async Task SearchProductAsync_WhenRagServiceThrows_ReturnsKeywordFallback_NoCrash()
    {
        // AI-3 工具层：IRagSearchService 抛异常 → 不崩溃、回退纯关键词路兜底返回结果
        var ragSearch = Substitute.For<IRagSearchService>();
        ragSearch.SearchProductsAsync("咖啡", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProductSearchHit>>(new InvalidOperationException("模拟混合检索失败")));

        var result = await BuildProvider(ragSearch).SearchProductAsync("咖啡");

        // 关键词路兜底：「咖啡」字面命中 意式浓缩咖啡机(5)
        Assert.Equal("找到 1 个商品：\n#5 意式浓缩咖啡机 — ¥349.99", result);
    }

    [Fact]
    public async Task SearchProductAsync_WhenRagServiceUnregistered_FallsBackToKeywordOnly()
    {
        // 中间态（AddRag 未落地 / 宿主未启用 RAG）：ragSearchService 为 null → 保持既有纯关键词行为，不破坏（AR-3）
        var provider = BuildProvider(ragSearchService: null);

        var result = await provider.SearchProductAsync("运动");

        // 与变更前 CartToolProvider 关键词匹配行为一致（谓词 = Name.Contains || Tags.Any，种子序）：
        // Tag 命中 专业跑鞋(3)、高级瑜伽垫(6)；名称命中 智能运动手表(10)
        var expected = "找到 3 个商品：\n#3 专业跑鞋 — ¥129.99\n#6 高级瑜伽垫 — ¥59.99\n#10 智能运动手表 — ¥199.99";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenRagServiceNotRegistered_DiResolvesProvider_WithNullFallback()
    {
        // 中间态兼容：AddRag（Task 9）落地前，DI 容器无 IRagSearchService 注册，
        // CartToolProvider 仍可解析（可选参数按默认 null 注入）——保证既有 WebApplicationFactory 测试不被破坏
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new CurrentUserAccessor());
        services.AddSingleton<CartToolProvider>();
        using var provider = services.BuildServiceProvider();

        var cartTools = provider.GetRequiredService<CartToolProvider>();
        Assert.NotNull(cartTools);
    }

    /// <summary>fake IProductRepository：返回 18 条种子数据（与真实 ProductRepository.GetAll 语义一致）。</summary>
    private sealed class FakeProductRepository : IProductRepository
    {
        public IReadOnlyList<Product> GetAll() => ProductSeedData.Products;

        public Product? QueryFilter(Func<Product, bool> predicate) => ProductSeedData.Products.FirstOrDefault(predicate);
    }
}
