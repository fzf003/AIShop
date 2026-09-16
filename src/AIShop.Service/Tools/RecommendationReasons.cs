using AIShop.Core.Entities;
using AIShop.Core.Services;
using AIShop.Core.StaticData;

namespace AIShop.Service.Tools;

/// <summary>
/// 推荐理由文案派生（纯函数：无 IO、无大模型、不依赖任何服务）。
///
/// 规则：按合并后的关键词顺序（<see cref="RecommendationMerger.MergeKeywords"/>，与推荐口径同源），
/// 取**第一个**命中该商品 `Tags ∪ Category` 的关键词——命中判定与
/// <c>ProductCatalog.SplitProducts</c> 同口径（关键词本身或其 ProductKeywordMap 展开命中，序数比较），
/// 故「进了推荐列表的商品」必定能派生出理由。
/// 归属：命中当前对话关键词 → 「因为你提到「{kw}」」；仅命中用户偏好 → 「根据你的偏好「{kw}」」。
/// </summary>
internal static class RecommendationReasons
{
    private const string CurrentKeywordPrefix = "因为你提到「";
    private const string PreferenceKeywordPrefix = "根据你的偏好「";

    /// <summary>派生单个商品的推荐理由；未命中任何关键词返回空串（前端不渲染该 tag）。</summary>
    internal static string Build(
        Product product,
        IReadOnlyList<string> currentKeywords,
        IReadOnlyList<string> prefKeywords)
    {
        var merged = RecommendationMerger.MergeKeywords([.. currentKeywords], [.. prefKeywords]);
        if (merged.Length == 0) return string.Empty;

        var searchable = new HashSet<string>(product.Tags, StringComparer.Ordinal) { product.Category };

        foreach (var keyword in merged)
        {
            if (!Matches(keyword, searchable)) continue;

            // 归属判定：当前关键词优先。合并时会忽略大小写去重且当前关键词在前，
            // 同一关键词同时出现在两处时归属「当前对话」符合用户直觉。
            return currentKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)
                ? $"{CurrentKeywordPrefix}{keyword}」"
                : $"{PreferenceKeywordPrefix}{keyword}」";
        }

        return string.Empty;
    }

    /// <summary>命中判定：关键词本身命中，或其白名单展开命中（与 SplitProducts 的 orderedTags 同口径）。</summary>
    private static bool Matches(string keyword, HashSet<string> searchable)
    {
        if (searchable.Contains(keyword)) return true;

        return ProductKeywordMap.Entries.TryGetValue(keyword, out var expansions)
            && expansions.Any(searchable.Contains);
    }
}
