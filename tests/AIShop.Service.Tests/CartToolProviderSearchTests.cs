using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Infrastructure.Services;
using AIShop.Service.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AIShop.Service.Tests;

/// <summary>
/// search_product 纯语义检索：委托 <see cref="IProductSemanticSearch"/> + 输出格式兼容；
/// 语义检索异常 / 未注册 → 返回无结果提示，不崩溃。
/// </summary>
public sealed class CartToolProviderSearchTests
{
    /// <summary>构造被测工具：注入受控 IProductSemanticSearch（null 表示 RAG 未注册的中间态）。</summary>
    private static CartToolProvider BuildProvider(IProductSemanticSearch? semanticSearch)
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        return new CartToolProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CurrentUserAccessor(),
            semanticSearch);
    }

    [Fact]
    public async Task ShouldFormatOutput_WhenSemanticSearchReturnsHits()
    {
        // 语义检索命中 → 输出带相关度分 + 类别（[0.90] #5 名称（类别）— ¥价格），LLM 可据此判断匹配强弱
        var semantic = Substitute.For<IProductSemanticSearch>();
        semantic.SearchAsync("咖啡", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProductSearchHit>>(
            [
                new ProductSearchHit(5, "意式浓缩咖啡机", "厨房用品", 349.99m, 0.9),
                new ProductSearchHit(3, "专业跑鞋", "鞋类", 129.99m, 0.8),
            ]));

        var result = await BuildProvider(semantic).SearchProductAsync("咖啡");

        var expected = "找到 2 个商品：\n[0.90] #5 意式浓缩咖啡机（厨房用品） — ¥349.99\n[0.80] #3 专业跑鞋（鞋类） — ¥129.99";
        Assert.Equal(expected, result);

        // 委托确已到达 IProductSemanticSearch（domain 用默认 null）
        await semantic.Received(1).SearchAsync("咖啡", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShouldReturnNotFound_WhenSemanticSearchReturnsEmpty()
    {
        // 无命中 → 返回 `未找到包含「{keyword}」的商品`（工具契约文案）
        var semantic = Substitute.For<IProductSemanticSearch>();
        semantic.SearchAsync("咖啡机", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProductSearchHit>>([]));

        var result = await BuildProvider(semantic).SearchProductAsync("咖啡机");

        Assert.Equal("未找到包含「咖啡机」的商品", result);
    }

    [Fact]
    public async Task ShouldReturnNotFound_NoCrash_WhenSemanticSearchThrows()
    {
        // 语义检索异常（模型缺失 / 索引未建 / 存储错误）→ 不崩溃，返回无结果提示
        var semantic = Substitute.For<IProductSemanticSearch>();
        semantic.SearchAsync("咖啡", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProductSearchHit>>(new InvalidOperationException("模拟语义检索失败")));

        var result = await BuildProvider(semantic).SearchProductAsync("咖啡");

        Assert.Equal("未找到包含「咖啡」的商品", result);
    }

    [Fact]
    public async Task ShouldReturnNotFound_WhenSemanticSearchUnregistered()
    {
        // 语义搜索未注册（宿主未启用 RAG）→ 返回无结果提示，不崩溃
        var result = await BuildProvider(semanticSearch: null).SearchProductAsync("运动");

        Assert.Equal("未找到包含「运动」的商品", result);
    }

    [Fact]
    public void ShouldResolveProvider_WithNullFallback_WhenNotRegistered()
    {
        // 中间态兼容：无 IProductSemanticSearch 注册时，CartToolProvider 仍可解析（可选参数按默认 null 注入）
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new CurrentUserAccessor());
        services.AddSingleton<CartToolProvider>();
        using var provider = services.BuildServiceProvider();

        var cartTools = provider.GetRequiredService<CartToolProvider>();
        Assert.NotNull(cartTools);
    }
}
