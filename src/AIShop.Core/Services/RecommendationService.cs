using AIShop.Core.Interfaces;
using AIShop.Core.ValueObjects;

namespace AIShop.Core.Services;

/// <summary>
/// 推荐编排服务：关键词合并 + 商品匹配 + 精选兜底，统一 /chat 与 /recommendations 口径。
/// 纯逻辑（依赖内存商品目录），无 IO；兜底文案与 HasRecommendation 语义单一来源。
/// </summary>
public sealed class RecommendationService(IProductCatalogService catalog)
{
    /// <summary>
    /// 构建推荐结果：当前关键词优先，偏好补齐；无匹配时精选兜底。
    /// </summary>
    /// <param name="currentKeywords">当前消息关键词（/recommendations 无当前关键词传空）。</param>
    /// <param name="prefKeywords">偏好关键词（已按权重降序）。</param>
    public Recommendation Build(string[] currentKeywords, string[] prefKeywords)
    {
        var merged = RecommendationMerger.MergeKeywords(currentKeywords, prefKeywords);

        // 无当前关键词且无偏好 → 精选兜底（HasRecommendation=false）
        if (merged.Length == 0)
        {
            var fallback = catalog.All.Take(6).ToList();
            return new Recommendation(
                [],
                fallback,
                catalog.All.Count == 0 ? "暂无特定推荐" : "为您精选商品",
                false,
                null);
        }

        var (recommended, others) = catalog.SplitProducts(merged);
        var recList = recommended.ToList();

        if (recList.Count > 0)
        {
            return new Recommendation(
                recList,
                others.Take(12).ToList(),
                "根据您的兴趣，为您推荐：",
                true,
                recList.Select(p => p.Category).Distinct().ToArray());
        }

        // merged>0 但无商品命中 → 精选兜底，避免「空推荐却提示已推荐」的 UX 退化
        return new Recommendation(
            [],
            catalog.All.Take(6).ToList(),
            "为您精选商品",
            false,
            null);
    }
}
