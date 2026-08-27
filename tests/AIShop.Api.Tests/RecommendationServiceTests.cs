using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Services;
using NSubstitute;

namespace AIShop.Api.Tests;

/// <summary>
/// RecommendationService 单元测试：验证推荐编排（关键词合并 + 商品匹配 + 精选兜底）的统一口径。
/// 使用 mock 商品仓储 + 真实 ProductCatalog（18 商品），覆盖 /chat 与 /recommendations 共用逻辑。
/// </summary>
public sealed class RecommendationServiceTests
{
    private static readonly RecommendationService Service = CreateService(ProductSeedData.Products);

    private static RecommendationService CreateService(IReadOnlyList<Product> products)
    {
        var repo = Substitute.For<IProductRepository>();
        repo.GetAll().Returns(products);
        return new RecommendationService(new ProductCatalog(repo));
    }

    // ---------- 有推荐 ----------

    [Fact]
    public void ShouldReturnMatchedProducts_WhenKeywordsHitCatalog()
    {
        // 咖啡 → 命中商品
        var result = Service.Build(["咖啡"], ["健身"]);

        Assert.True(result.HasRecommendation);
        Assert.NotEmpty(result.Recommended);
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.NotNull(result.MatchedCategories);
    }

    [Fact]
    public void ShouldMatchInsulatedBottle_WhenMessageContainsInsulationKeyword()
    {
        // 用户消息「不锈钢保温水瓶」含关键词「保温」→ 匹配保温瓶（Id 8）
        var result = Service.Build(["保温"], []);

        Assert.True(result.HasRecommendation);
        Assert.Contains(result.Recommended, p => p.Id == 8);
    }

    // ---------- 兜底：无关键词无偏好 ----------

    [Fact]
    public void ShouldReturnCuratedFallback_WhenNoKeywordsAndNoPreferences()
    {
        var result = Service.Build([], []);

        Assert.False(result.HasRecommendation);
        Assert.Empty(result.Recommended);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal("为您精选商品", result.Message);
        Assert.Null(result.MatchedCategories);
    }

    // ---------- 兜底：有偏好但无商品命中 ----------

    [Fact]
    public void ShouldReturnCuratedFallback_WhenPreferenceKeywordsMissAllProducts()
    {
        // 偏好词「zzz_不存在的关键词」命中不了任何商品标签 → merged 非空但 Recommended 空
        var result = Service.Build(["zzz_不存在"], []);

        Assert.False(result.HasRecommendation);
        Assert.Empty(result.Recommended);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal("为您精选商品", result.Message);
    }

    // ---------- 兜底：空商品库 ----------

    [Fact]
    public void ShouldSayNoSpecificRecommendation_WhenCatalogEmpty()
    {
        var emptyService = new RecommendationService(new EmptyCatalog());

        var result = emptyService.Build([], []);

        Assert.Equal("暂无特定推荐", result.Message);
        Assert.False(result.HasRecommendation);
    }

    /// <summary>空商品目录（验证空库兜底文案）。</summary>
    private sealed class EmptyCatalog : IProductCatalogService
    {
        public IReadOnlyList<Product> All => [];
        public IReadOnlyDictionary<string, string[]> KeywordMap => new Dictionary<string, string[]>();
        public Product[] MatchProducts(string[] preferences) => [];
        public (Product[] Recommended, Product[] Others) SplitProducts(string[] keywords) => ([], []);
    }
}
