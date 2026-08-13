namespace AIShop.Api.Features.Chat;

/// <summary>
/// R8 — 推荐快照（用户维度）。/api/chat 算完 <see cref="ChatReply"/> 后写入内存缓存
/// <c>recommend_{username}</c>（TTL 10min），/api/recommendations 优先读该快照构造
/// <see cref="RecommendationResponse"/>，保证推荐栏与聊天产物 100% 一致（推荐以聊天产物为准，
/// 不再由两套独立计算产生不一致）。
/// </summary>
/// <param name="Recommended">聊天推荐集合（无推荐时为 null）。</param>
/// <param name="Other">聊天其他商品集合（精选兜底时为 All.Take(6)）。</param>
/// <param name="MatchedCategories">命中关键词对应商品分类集合（无推荐时为 null）。</param>
/// <param name="HasRecommendation">聊天是否产生推荐（false 时 Recommended 为 null）。</param>
/// <param name="Message">聊天提示语（RecommendationResponse.Message 直接取快照，保持文案一致）。</param>
public sealed record RecommendationSnapshot(
    List<ProductDto>? Recommended,
    List<ProductDto>? Other,
    string[]? MatchedCategories,
    bool HasRecommendation,
    string Message);
