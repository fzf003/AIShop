using AIShop.Core.StaticData;

namespace AIShop.Api.Tests;

public sealed class ProductKeywordMapTests
{
    private static readonly string[] ExpectedKeys =
    [
        "夹克", "鞋子", "靴子", "音乐", "咖啡", "健身", "瑜伽", "烹饪",
        "科技", "阅读", "户外", "时尚", "环保", "巧克力", "跑步", "家居",
        "送礼", "爱好", "耳机", "手表", "运动", "音频", "数码"
    ];

    [Fact]
    public void Entries_ContainsAllExpectedKeys()
    {
        foreach (var key in ExpectedKeys)
        {
            Assert.True(ProductKeywordMap.Entries.ContainsKey(key),
                $"ProductKeywordMap.Entries should contain '{key}'");
        }
    }

    [Fact]
    public void Entries_HasExactlyTwentyThreeKeys()
    {
        Assert.Equal(23, ProductKeywordMap.Entries.Count);
    }

    [Fact]
    public void Entries_EveryValueIsNonEmpty()
    {
        foreach (var value in ProductKeywordMap.Entries.Values)
        {
            Assert.NotNull(value);
            Assert.NotEmpty(value);
        }
    }
}
