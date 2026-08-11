using AIShop.Api.Features.Chat;

namespace AIShop.Api.Tests;

public sealed class RecommendationMergerTests
{
    // ---------- GetTopPreferenceKeywords ----------

    [Fact]
    public void ShouldReturnKeywordsByWeightDesc_WhenWeightsProvided()
    {
        var result = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":3,"健身":2,"音乐":1}""", 5);

        Assert.Equal(["咖啡", "健身", "音乐"], result);
    }

    [Fact]
    public void ShouldTruncateToMax_WhenMoreKeywordsThanMax()
    {
        var result = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":3,"健身":2,"音乐":1,"跑步":4}""", 2);

        Assert.Equal(["跑步", "咖啡"], result);
    }

    [Fact]
    public void ShouldReturnEmpty_WhenJsonIsNull()
    {
        Assert.Empty(RecommendationMerger.GetTopPreferenceKeywords(null, 5));
    }

    [Fact]
    public void ShouldReturnEmpty_WhenJsonIsEmptyOrWhitespace()
    {
        Assert.Empty(RecommendationMerger.GetTopPreferenceKeywords("", 5));
        Assert.Empty(RecommendationMerger.GetTopPreferenceKeywords("   ", 5));
    }

    [Fact]
    public void ShouldReturnEmpty_WhenJsonIsInvalid()
    {
        Assert.Empty(RecommendationMerger.GetTopPreferenceKeywords("not-json", 5));
    }

    [Fact]
    public void ShouldReturnEmpty_WhenJsonIsEmptyObject()
    {
        Assert.Empty(RecommendationMerger.GetTopPreferenceKeywords("{}", 5));
    }

    // ---------- MergeKeywords ----------

    // spec「推荐合并 — 当前关键词优先，偏好补齐」
    [Fact]
    public void ShouldPrependCurrentAndAppendPreferencesByWeight_WhenCurrentFewerThanThree()
    {
        var current = new[] { "跑步" };
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":3,"健身":2,"音乐":1}""", 5);

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["跑步", "咖啡", "健身", "音乐"], result);
    }

    // spec「推荐合并 — 当前关键词不足 3 个才补齐」
    [Fact]
    public void ShouldKeepCurrentOnly_WhenCurrentHasThreeKeywords()
    {
        var current = new[] { "咖啡", "健身", "跑步" };
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"音乐":1,"户外":2}""", 5);

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["咖啡", "健身", "跑步"], result);
    }

    // spec「偏好存在但无当前关键词时用偏好推荐」
    [Fact]
    public void ShouldUsePreferences_WhenNoCurrentKeywords()
    {
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":3,"健身":2}""", 5);

        var result = RecommendationMerger.MergeKeywords([], pref);

        Assert.Equal(["咖啡", "健身"], result);
    }

    // spec 补充：去重不重复（「合并后总长度 ≤ 5，且与当前关键词不重复」）
    [Fact]
    public void ShouldNotDuplicateCurrentKeyword_WhenPreferenceMatches()
    {
        var current = new[] { "咖啡" };
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":3,"健身":2}""", 5);

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["咖啡", "健身"], result);
    }

    // spec 补充：合并后总长度 ≤ 5（「合并后总长度 ≤ 5」）
    [Fact]
    public void ShouldCapAtFive_WhenCurrentAndPreferencesExceedFive()
    {
        var current = new[] { "跑步" };
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"咖啡":6,"健身":5,"音乐":4,"户外":3,"家居":2,"送礼":1}""", 5);

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(5, result.Length);
        Assert.Equal(["跑步", "咖啡", "健身", "音乐", "户外"], result);
    }

    // spec 补充：序数忽略大小写去重（design 4.3 合并算法）
    [Fact]
    public void ShouldDedupeIgnoringCase_WhenCurrentAndPreferenceDifferInCase()
    {
        var current = new[] { "跑步" };
        var pref = RecommendationMerger.GetTopPreferenceKeywords("""{"跑步":3,"咖啡":2}""", 5);

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["跑步", "咖啡"], result);
    }
}
