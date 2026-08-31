using Microsoft.Extensions.VectorData;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// 商品知识文档的向量存储 DTO（design §4.2）。
/// 与 Core <see cref="AIShop.Core.Models.ProductDocument"/> 的分工：Core 是领域模型（零标签零引用，D2），
/// 本类型承载「向量库怎么存它」的存储标注——关注点分离，存储细节（列名、向量维度、序列化形式）不渗入领域层。
/// </summary>
public sealed class ProductDocumentRecord
{
    /// <summary>"product-{id}"，全局唯一，= collection key（对应 Core ProductDocument.Id）</summary>
    [VectorStoreKey]
    public string Id { get; set; } = "";

    /// <summary>"product" 领域判别，供检索时 options.Filter（LINQ 表达式）过滤（AK-3）</summary>
    [VectorStoreData]
    public string Domain { get; set; } = "";

    /// <summary>回链商品 Id（检索命中后经 IProductRepository 取展示所需信息）</summary>
    [VectorStoreData]
    public int ProductId { get; set; }

    /// <summary>商品名称（冗余存储，检索结果可直接展示标题）</summary>
    [VectorStoreData]
    public string Name { get; set; } = "";

    /// <summary>商品类别</summary>
    [VectorStoreData]
    public string Category { get; set; } = "";

    /// <summary>Tags 的 JSON 序列化。用 JSON 字符串而非 string[] 属性：规避 SqliteMapper 对数组属性映射的不确定性（design §4.2）。</summary>
    [VectorStoreData]
    public string TagsJson { get; set; } = "[]";

    /// <summary>价格用 double 而非 decimal：SqliteVec 数值列对 decimal 支持不可靠（design §4.2）；展示回 ProductSearchHit 时 (decimal) 转换。</summary>
    [VectorStoreData]
    public double Price { get; set; }

    /// <summary>embedding 输入文本（= Core ProductDocument.Text 计算属性，design §4.1 拼接规则）</summary>
    [VectorStoreData]
    public string Text { get; set; } = "";

    /// <summary>512 维句向量（bge-small-zh-v1.5 hidden_size，Task1 POC 实测 D-a，非 design 早期假设的 384）。
    /// ReadOnlyMemory&lt;float&gt; 是 SqliteVec 原生支持类型 → upsert 不会自动生成向量，
    /// 索引由 ProductSemanticSearch 显式填充 Embedding；检索时显式生成查询向量后传入 SearchAsync。
    /// 无类级 [VectorStoreRecord] 标注（VectorData 10.x 不存在该属性，D-c POC 实测）；[VectorStoreVector] 必须传维度。</summary>
    [VectorStoreVector(512)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
