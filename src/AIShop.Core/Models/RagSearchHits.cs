namespace AIShop.Core.Models;

/// <summary>
/// 检索结果模型（design §4.1 分组文件，参照 CartEntities.cs 惯例）。
/// Score 不代表排序依据：RRF 融合排序由返回顺序保证（score = Σ 1/(k+rank)，仅排名序参与，见 Core/Services/RrfFusion.cs）；
/// Score 仅保留该命中所属路的原始值——关键词路为 0 占位、向量路为原始距离/相似度，不会被改写为 RRF 融合得分
/// （RrfFusion 返回的是某一路的原始代表对象，无法构造带新分的新元素）。
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
