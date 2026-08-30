namespace AIShop.Core.Models;

/// <summary>
/// 检索结果模型（design §4.1 分组文件，参照 CartEntities.cs 惯例）。
/// Score 语义 = RRF 融合后的得分：关键词路占位 0、向量路原始距离均不参与排序，
/// 仅排名序参与 RRF（score = Σ 1/(k+rank)）——见 Core/Services/RrfFusion.cs。
/// </summary>
public sealed record ProductSearchHit(
    int ProductId,
    string Name,
    string Category,
    decimal Price,
    double Score);

/// <summary>
/// 知识检索结果：DocumentId 对应 ProductDocument.Id（"product-{id}"），Title 取商品名，
/// Text 为描述片段（embedding 输入同源），Score 同 ProductSearchHit.Score 语义。
/// </summary>
public sealed record KnowledgeSearchHit(
    string DocumentId,
    string Title,
    string Category,
    string Text,
    double Score);
