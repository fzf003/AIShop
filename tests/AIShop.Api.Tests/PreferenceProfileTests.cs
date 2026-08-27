using System.Text.Json;
using AIShop.Core.ValueObjects;

namespace AIShop.Api.Tests;

/// <summary>
/// PreferenceProfile 值对象行为测试：JSON 容错解析、TopKeywords 排序、Merge 累加、TrimToTop 截断。
/// </summary>
public sealed class PreferenceProfileTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ---------- FromKeywordsJson（容错解析） ----------

    [Fact]
    public void ShouldParseWeights_FromValidJson()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3,"健身":2}""", DateTime.UtcNow);

        Assert.Equal(3, profile.KeywordWeights["咖啡"]);
        Assert.Equal(2, profile.KeywordWeights["健身"]);
    }

    [Fact]
    public void ShouldThrow_WhenJsonInvalid()
    {
        // 非法 JSON 视为存储损坏，抛 JsonException 由调用方决定降级（读取容错 / worker 跳过）
        Assert.Throws<JsonException>(() => PreferenceProfile.FromKeywordsJson(UserId, "not-json", DateTime.UtcNow));
    }

    [Fact]
    public void ShouldReturnEmpty_WhenJsonNullOrEmpty()
    {
        Assert.Empty(PreferenceProfile.FromKeywordsJson(UserId, null, DateTime.UtcNow).KeywordWeights);
        Assert.Empty(PreferenceProfile.FromKeywordsJson(UserId, "", DateTime.UtcNow).KeywordWeights);
        Assert.Empty(PreferenceProfile.FromKeywordsJson(UserId, "   ", DateTime.UtcNow).KeywordWeights);
    }

    // ---------- TopKeywords（排序 + 截断） ----------

    [Fact]
    public void ShouldReturnKeywordsByWeightDesc_WhenWeightsProvided()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3,"健身":2,"音乐":1}""", DateTime.UtcNow);

        Assert.Equal(["咖啡", "健身", "音乐"], profile.TopKeywords(5));
    }

    [Fact]
    public void ShouldTruncateToMax_WhenMoreKeywordsThanMax()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3,"健身":2,"音乐":1,"跑步":4}""", DateTime.UtcNow);

        Assert.Equal(["跑步", "咖啡"], profile.TopKeywords(2));
    }

    [Fact]
    public void ShouldOrderTieByKeyOrdinal_WhenWeightsEqual()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"b":1,"a":1,"c":1}""", DateTime.UtcNow);

        Assert.Equal(["a", "b", "c"], profile.TopKeywords(5));
    }

    // ---------- Merge（累加 + 不可变） ----------

    [Fact]
    public void ShouldIncrementWeight_WhenMergingNewKeywords()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3}""", DateTime.UtcNow);

        var merged = profile.Merge(["咖啡", "健身"]);

        Assert.Equal(4, merged.KeywordWeights["咖啡"]);
        Assert.Equal(1, merged.KeywordWeights["健身"]);
        Assert.Equal(3, profile.KeywordWeights["咖啡"]);  // 原实例不变（不可变）
    }

    [Fact]
    public void ShouldSkipBlankKeywords_WhenMerging()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, "{}", DateTime.UtcNow);

        var merged = profile.Merge(["  ", "咖啡", ""]);

        Assert.Single(merged.KeywordWeights);
        Assert.Equal(1, merged.KeywordWeights["咖啡"]);
    }

    // ---------- TrimToTop（Top-20 截断，对齐 worker 语义） ----------

    [Fact]
    public void ShouldTrimToTopByWeightDesc_WhenExceedingMax()
    {
        var profile = PreferenceProfile.FromKeywordsJson(
            UserId, """{"咖啡":6,"健身":5,"音乐":4,"户外":3,"家居":2,"送礼":1}""", DateTime.UtcNow);

        var trimmed = profile.TrimToTop(3);

        Assert.Equal(3, trimmed.KeywordWeights.Count);
        Assert.Equal(["咖啡", "健身", "音乐"], trimmed.TopKeywords(5));
    }

    [Fact]
    public void ShouldKeepAll_WhenWithinMax()
    {
        var profile = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3,"健身":2}""", DateTime.UtcNow);

        var trimmed = profile.TrimToTop();

        Assert.Equal(2, trimmed.KeywordWeights.Count);
    }

    [Fact]
    public void ShouldRoundTrip_ToAndFromJson()
    {
        var original = PreferenceProfile.FromKeywordsJson(UserId, """{"咖啡":3,"健身":2}""", DateTime.UtcNow);

        var restored = PreferenceProfile.FromKeywordsJson(UserId, original.ToKeywordsJson(), original.UpdatedAt);

        Assert.Equal(original.KeywordWeights, restored.KeywordWeights);
    }
}
