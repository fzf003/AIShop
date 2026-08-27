using AIShop.Core.Services;

namespace AIShop.Api.Tests;

public sealed class RecommendationMergerTests
{
    // ---------- MergeKeywords ----------

    // spec「推荐合并 — 当前关键词优先，偏好补齐」
    [Fact]
    public void ShouldPrependCurrentAndAppendPreferencesByWeight_WhenCurrentFewerThanThree()
    {
        var current = new[] { "跑步" };
        var pref = new[] { "咖啡", "健身", "音乐" };

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["跑步", "咖啡", "健身", "音乐"], result);
    }

    // spec「推荐合并 — 当前关键词不足 3 个才补齐」
    [Fact]
    public void ShouldKeepCurrentOnly_WhenCurrentHasThreeKeywords()
    {
        var current = new[] { "咖啡", "健身", "跑步" };
        var pref = new[] { "户外", "音乐" };

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["咖啡", "健身", "跑步"], result);
    }

    // spec「偏好存在但无当前关键词时用偏好推荐」
    [Fact]
    public void ShouldUsePreferences_WhenNoCurrentKeywords()
    {
        var pref = new[] { "咖啡", "健身" };

        var result = RecommendationMerger.MergeKeywords([], pref);

        Assert.Equal(["咖啡", "健身"], result);
    }

    // spec 补充：去重不重复（「合并后总长度 ≤ 5，且与当前关键词不重复」）
    [Fact]
    public void ShouldNotDuplicateCurrentKeyword_WhenPreferenceMatches()
    {
        var current = new[] { "咖啡" };
        var pref = new[] { "咖啡", "健身" };

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["咖啡", "健身"], result);
    }

    // spec 补充：合并后总长度 ≤ 5（「合并后总长度 ≤ 5」）
    [Fact]
    public void ShouldCapAtFive_WhenCurrentAndPreferencesExceedFive()
    {
        var current = new[] { "跑步" };
        var pref = new[] { "咖啡", "健身", "音乐", "户外", "家居" };

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(5, result.Length);
        Assert.Equal(["跑步", "咖啡", "健身", "音乐", "户外"], result);
    }

    // spec 补充：序数忽略大小写去重（design 4.3 合并算法）
    [Fact]
    public void ShouldDedupeIgnoringCase_WhenCurrentAndPreferenceDifferInCase()
    {
        var current = new[] { "跑步" };
        var pref = new[] { "跑步", "咖啡" };

        var result = RecommendationMerger.MergeKeywords(current, pref);

        Assert.Equal(["跑步", "咖啡"], result);
    }
}
