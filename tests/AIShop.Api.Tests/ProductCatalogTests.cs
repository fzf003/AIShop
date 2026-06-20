using AIShop.Infrastructure.Services;

namespace AIShop.Api.Tests;

public sealed class ProductCatalogTests
{
    [Fact]
    public void SplitProducts_MatchingKeywords_ReturnsSplit()
    {
        var (recommended, others) = ProductCatalog.SplitProducts(["跑步"]);

        Assert.Contains(recommended, p => p.Name == "专业跑鞋");
        Assert.Contains(recommended, p => p.Name == "高级瑜伽垫");
        Assert.DoesNotContain(recommended, p => p.Name == "经典皮夹克");
        Assert.Contains(others, p => p.Name == "经典皮夹克");
    }

    [Fact]
    public void SplitProducts_EmptyKeywords_ReturnsEmptyRecommended()
    {
        var (recommended, others) = ProductCatalog.SplitProducts([]);

        Assert.Empty(recommended);
        Assert.Equal(6, others.Length);
    }

    [Fact]
    public void SplitProducts_AllProductsCoveredByCombinedKeywords()
    {
        var allKeywords = ProductCatalog.KeywordMap.Keys.ToArray();
        var (recommended, _) = ProductCatalog.SplitProducts(allKeywords);

        Assert.Equal(ProductCatalog.All.Length, recommended.Length);
    }

    [Fact]
    public void SplitProducts_NonMatchingKeywords_ReturnsEmptyRecommended()
    {
        var (recommended, others) = ProductCatalog.SplitProducts(["不存在的关键词"]);

        Assert.Empty(recommended);
        Assert.Equal(ProductCatalog.All.Length, others.Length);
    }

    [Fact]
    public void PromoteProduct_KeywordExpansionMapsToTags()
    {
        var (recommended, _) = ProductCatalog.SplitProducts(["运动"]);

        // "运动" maps to many tags, should match running shoes, yoga mat, smart watch, etc.
        Assert.Contains(recommended, p => p.Name == "专业跑鞋");
        Assert.Contains(recommended, p => p.Name == "高级瑜伽垫");
        Assert.Contains(recommended, p => p.Name == "智能运动手表");
    }

    [Fact]
    public void MatchProducts_EmptyPreferences_ReturnsEmpty()
    {
        var result = ProductCatalog.MatchProducts([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_WithPreferences_ReturnsScored()
    {
        var result = ProductCatalog.MatchProducts(["咖啡"]);

        Assert.Contains(result, p => p.Name == "意式浓缩咖啡机");
        Assert.InRange(result.Length, 1, 6);
    }

    [Fact]
    public void KeywordMap_ContainsAllExpectedKeys()
    {
        var expected = new[]
        {
            "夹克", "鞋子", "靴子", "音乐", "咖啡", "健身", "瑜伽", "烹饪",
            "科技", "阅读", "户外", "时尚", "环保", "巧克力", "跑步", "家居",
            "送礼", "爱好", "耳机", "手表", "运动", "音频", "数码"
        };

        foreach (var key in expected)
        {
            Assert.True(ProductCatalog.KeywordMap.ContainsKey(key),
                $"KeywordMap should contain '{key}'");
        }
    }
}
