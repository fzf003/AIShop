namespace AIShop.Core.Models;

/// <summary>
/// RAG 知识文档领域模型（首期语料 = 商品描述）。
/// 零依赖：不带 VectorData 属性标签（design D2），存储标注由 Infrastructure 侧 DTO（ProductDocumentRecord）承载。
/// Id = "product-{id}"（全局唯一，即 collection key）；Domain = "product"（领域判别，供检索时 options.Filter 过滤）。
/// </summary>
public sealed record ProductDocument(
    string Id,        // "product-5"（全局唯一，= collection key）
    string Domain,    // "product"（领域判别，供 options.Filter 过滤）
    int ProductId,    // 5（回链商品）
    string Name,      // 意式浓缩咖啡机
    string Category,  // 厨房用品
    string[] Tags,    // [咖啡, 浓缩, 厨房, 早晨]
    decimal Price,    // 349.99
    string Emoji)     // ☕（仅前端展示，不参与 embedding 输入）
{
    /// <summary>
    /// 结构化拼接的描述文本（embedding 输入），拼接规则见 design §4.1。
    /// Text 用实时计算属性而非存储字段：保证任何构造点产出的文本都严格符合模板，
    /// 从结构上杜绝「外部传入不一致 Text 破坏 AI-2 确定性」的可能；代价是放弃在构造时自定义 Text。
    /// </summary>
    public string Text => $"{Name}。类别：{Category}。标签：{string.Join("、", Tags)}。价格：¥{Price}";
}
