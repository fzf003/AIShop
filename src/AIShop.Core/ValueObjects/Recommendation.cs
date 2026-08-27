using AIShop.Core.Entities;

namespace AIShop.Core.ValueObjects;

/// <summary>
/// 推荐结果（Core 层形状）：商品以 Core 实体承载，Api 层负责转 ProductDto。
/// 统一 /chat 与 /recommendations 的推荐口径。
/// </summary>
public sealed record Recommendation(
    IReadOnlyList<Product> Recommended,
    IReadOnlyList<Product> Other,
    string Message,
    bool HasRecommendation,
    IReadOnlyList<string>? MatchedCategories);
