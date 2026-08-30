using System.Text.Json;
using AIShop.Core.Models;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// Core ProductDocument ↔ Infrastructure ProductDocumentRecord 映射（design §4.3）。
/// 放在 Infrastructure/Rag 而非 Core（D2：存储细节不进领域层，Core 模型保持零依赖零标签）。
/// </summary>
public static class ProductDocumentMapping
{
    /// <summary>
    /// 领域模型 → 存储 DTO。
    /// 非显然取舍：
    /// 1. Emoji 不落库（design §4.3：检索回链商品时经 IProductRepository 取，或直接忽略）——DTO 无对应属性即结构保证；
    /// 2. Tags → TagsJson 用 System.Text.Json 序列化（规避 SqliteMapper 对 string[] 数组属性映射的不确定性）；
    /// 3. Price decimal → double（SqliteVec 数值列对 decimal 支持不可靠）。
    /// Embedding 由调用方（RagIndexer）在批量 embedding 后显式填充，本方法不生成向量。
    /// </summary>
    public static ProductDocumentRecord ToRecord(ProductDocument doc) =>
        new()
        {
            Id = doc.Id,
            Domain = doc.Domain,
            ProductId = doc.ProductId,
            Name = doc.Name,
            Category = doc.Category,
            TagsJson = JsonSerializer.Serialize(doc.Tags),
            Price = (double)doc.Price,
            Text = doc.Text,
        };
}
