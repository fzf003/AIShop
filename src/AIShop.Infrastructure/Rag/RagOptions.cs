namespace AIShop.Infrastructure.Rag;

/// <summary>
/// RAG 配置项收拢（design §8 R17）：连接串 / 模型路径 / 向量维度 / collection 名统一集中，避免散落各处。
/// 维度为 512（bge-small-zh-v1.5 hidden_size，Task1 POC 实测 D-a，非 design 早期假设的 384）。
/// 模型路径相对输出目录（csproj Content 复制目标 = AppContext.BaseDirectory/Rag/Models/...，Task 5 EmbeddingGenerator 加载时拼接）。
/// </summary>
public static class RagOptions
{
    /// <summary>默认向量库连接串：独立文件 aishop.rag.db，与 EF aishop.db 隔离（R6，避免 vec0 原生表与 EF Migrations 混管）。</summary>
    public const string DefaultConnectionString = "Data Source=aishop.rag.db";

    /// <summary>商品知识 collection 名（AddSqliteCollection 的 name 参数；AB-2 扩展模式：新领域新增 record + collection 即可）。</summary>
    public const string CollectionName = "product";

    /// <summary>商品领域判别值（ProductDocument.Domain，供检索 options.Filter 过滤，AK-3）。</summary>
    public const string Domain = "product";

    /// <summary>embedding 向量维度：512（bge-small-zh-v1.5，CLS pooling 不降维，D-a）；与 [VectorStoreVector(512)] 保持一致。</summary>
    public const int EmbeddingDimensions = 512;

    /// <summary>ONNX 模型相对输出目录路径（启动预构建 / 检索懒构建兜底时加载）。</summary>
    public const string EmbeddingModelPath = "Rag/Models/bge-small-zh-v1.5/model.onnx";

    /// <summary>tokenizer 词表路径（Xenova 的 tokenizer.json 无法被 BertTokenizer.Create 加载，D-d：用 vocab.txt + BertOptions）。</summary>
    public const string EmbeddingVocabPath = "Rag/Models/bge-small-zh-v1.5/vocab.txt";
}
