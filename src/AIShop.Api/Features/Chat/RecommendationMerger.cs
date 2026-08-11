using System.Text.Json;

namespace AIShop.Api.Features.Chat;

/// <summary>
/// 推荐合并纯函数：供 /chat 与 /recommendations 端点复用，无任何依赖。
/// </summary>
public static class RecommendationMerger
{
    /// <summary>
    /// 反序列化用户偏好 JSON（<c>Dictionary&lt;string,int&gt;</c>），按权重降序取 Top-N 关键词。
    /// </summary>
    /// <param name="keywordsJson">偏好 JSON，如 <c>{"咖啡":3,"健身":2,"音乐":1}</c>；null/空/非法 JSON 返回空数组。</param>
    /// <param name="max">最多返回的关键词个数。</param>
    public static string[] GetTopPreferenceKeywords(string? keywordsJson, int max)
    {
        if (string.IsNullOrWhiteSpace(keywordsJson))
            return [];

        Dictionary<string, int> weights;
        try
        {
            weights = JsonSerializer.Deserialize<Dictionary<string, int>>(keywordsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        return weights
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .Take(max)
            .ToArray();
    }

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
