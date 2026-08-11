using AIShop.Core.StaticData;

namespace AIShop.Api.Tests;

/// <summary>
/// ProductSeedData.Products 种子常量契约测试。
/// 对应 spec「Product 表幂等播种」的种子数据来源：18 条、Id 1..18 无重复、字段非空。
/// </summary>
public sealed class ProductSeedDataTests
{
    [Fact]
    public void ShouldContainExactly18Products_WhenAccessingSeedData()
    {
        Assert.Equal(18, ProductSeedData.Products.Count);
    }

    [Fact]
    public void ShouldHaveSequentialIdsFrom1To18_WithoutDuplicates()
    {
        var ids = ProductSeedData.Products.Select(p => p.Id).ToArray();

        Assert.Equal(Enumerable.Range(1, 18), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void ShouldHaveAllFieldsNonEmpty_ForEverySeedProduct()
    {
        foreach (var product in ProductSeedData.Products)
        {
            Assert.False(string.IsNullOrWhiteSpace(product.Name), $"{product.Id}: Name 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(product.Category), $"{product.Id}: Category 不应为空");
            Assert.NotEmpty(product.Tags);
            Assert.All(product.Tags, tag => Assert.False(string.IsNullOrWhiteSpace(tag)));
            Assert.True(product.Price > 0, $"{product.Id}: Price 应大于 0");
            Assert.False(string.IsNullOrWhiteSpace(product.Emoji), $"{product.Id}: Emoji 不应为空");
        }
    }
}
