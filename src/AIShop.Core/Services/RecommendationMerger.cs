namespace AIShop.Core.Services;

/// <summary>
/// 推荐合并纯函数：供 /chat 与 /recommendations 端点复用，无任何依赖。
/// </summary>
public static class RecommendationMerger
{
    /// <summary>
    /// 合并推荐关键词：当前关键词优先，不足 3 个时用偏好（已按权重降序）补齐到 ≤5，
    /// 最终按序数忽略大小写去重并截断到 5 个。
    /// </summary>
    public static string[] MergeKeywords(string[] current, string[] prefKeywords)
    {
        var merged = (current ?? []).ToList();

        if (merged.Count < 3)
        {
            foreach (var kw in prefKeywords ?? [])
            {
                if (!merged.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    merged.Add(kw);

                if (merged.Count >= 5)
                    break;
            }
        }

        return merged
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }
}
