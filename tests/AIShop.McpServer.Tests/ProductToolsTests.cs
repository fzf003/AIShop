using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Services;
using AIShop.McpServer.Tools;

namespace AIShop.McpServer.Tests;

public sealed class ProductToolsTests
{
    private static readonly ProductTools Tools = new(new ProductCatalog());

    [Fact]
    public void MatchProducts_ValidKeywords_ReturnsResults()
    {
        var result = Tools.MatchProducts(["运动", "户外"]);

        Assert.NotEmpty(result);
        Assert.True(result.Length <= 6);
        // "专业跑鞋" tags: ["跑步", "运动", "健身", "体育", "鞋子"] — matches "运动"
        Assert.Contains(result, p => p.Name == "专业跑鞋");
        // "户外徒步靴" tags: ["徒步", "户外", "冒险", "自然", "靴子"] — matches "户外"
        Assert.Contains(result, p => p.Name == "户外徒步靴");
    }

    [Fact]
    public void MatchProducts_EmptyKeywords_ReturnsEmpty()
    {
        var result = Tools.MatchProducts([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_UnknownKeyword_ReturnsEmpty()
    {
        var result = Tools.MatchProducts(["不存在的关键词xyz"]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_ResultLimit_Six()
    {
        // 用多个关键词触发足够多的匹配，验证上限为 6
        var result = Tools.MatchProducts(["运动", "户外", "音乐", "科技", "咖啡", "健身", "阅读"]);

        Assert.True(result.Length <= 6);
    }

    [Fact]
    public void MatchProducts_SingleKeyword_ReturnsMatches()
    {
        // "咖啡" → 意式浓缩咖啡机
        var result = Tools.MatchProducts(["咖啡"]);

        Assert.NotEmpty(result);
        Assert.Contains(result, p => p.Name == "意式浓缩咖啡机");
    }

    [Fact]
    public void MatchProducts_ReturnedDto_HasAllFields()
    {
        var result = Tools.MatchProducts(["运动"]);

        Assert.NotEmpty(result);
        var product = result[0];
        Assert.True(product.Id > 0);
        Assert.False(string.IsNullOrEmpty(product.Name));
        Assert.False(string.IsNullOrEmpty(product.Category));
        Assert.NotEmpty(product.Tags);
        Assert.True(product.Price > 0);
        Assert.False(string.IsNullOrEmpty(product.Emoji));
    }
}
