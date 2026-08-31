namespace AIShop.Core.Models;

/// <summary>
/// 商品语义检索结果模型：由向量检索命中（ProductDocumentRecord）映射而来。
/// 排序由向量相似度决定（返回顺序即相关度序），Score 保留原始相似度供需要方使用。
/// </summary>
public sealed record ProductSearchHit(
    int ProductId,
    string Name,
    string Category,
    decimal Price,
    double Score);
