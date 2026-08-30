using AIShop.Core.Models;

namespace AIShop.Core.Interfaces;

/// <summary>
/// RAG 混合检索服务契约（design §5.1）。
/// 实现放 Infrastructure（RagSearchService），此处只定义契约，Core 零依赖（D2）。
/// </summary>
public interface IRagSearchService
{
    /// <summary>
    /// 混合检索商品：关键词命中 ∪ 向量召回，RRF 融合（AR-1）。
    /// 返回按融合得分降序的 top-N；向量路异常时降级仅关键词路（AI-3，不崩溃）。
    /// </summary>
    Task<IReadOnlyList<ProductSearchHit>> SearchProductsAsync(
        string query, int top = 5, CancellationToken ct = default);

    /// <summary>
    /// 语义检索知识文档（首期 = product 领域，AK-2）。
    /// domain 非空时经 options.Filter 限定领域（AK-3，为多领域预留）。
    /// </summary>
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchKnowledgeAsync(
        string query, string? domain = null, int top = 3, CancellationToken ct = default);
}
