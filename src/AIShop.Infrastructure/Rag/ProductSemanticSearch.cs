using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// 商品语义搜索：首次检索前懒构建索引（从商品库生成向量记录），之后查询文本 → bge 向量 → SqliteVec KNN。
/// 检索路径全程 DB 端（向量在向量库、命中字段冗余于记录），不把商品全量读进内存；
/// 索引构建在首次检索时一次性读商品库（建立索引必须遍历全部商品，属不可避免的一次性成本）。
/// </summary>
public sealed class ProductSemanticSearch(
    VectorStoreCollection<string, ProductDocumentRecord> collection,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IServiceScopeFactory scopeFactory) : IProductSemanticSearch
{
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private bool _indexed;

    /// <summary>相似度门控下限（实测校准）：相关≈0.80、明显无关≈0.40 → 取 0.5，弱匹配不返回。</summary>
    private const float MinSimilarity = 0.5f;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSearchHit>> SearchAsync(
        string query, string? domain = null, int top = 5, string? category = null, CancellationToken ct = default)
    {
        var hits = new List<ProductSearchHit>(top);

        await EnsureIndexedAsync(ct);

        if(string.IsNullOrWhiteSpace(query))
        {
            return hits;
        }

        // ① 查询文本 → 向量（bge-small-zh ONNX，512 维）
        var embedding = (await embeddingGenerator.GenerateAsync([query], null, ct))[0];

        // ② domain 过滤（扩展缝）：非空时仅检索该领域记录；当前单 collection 全为 product，传 "product" 等价不过滤
        var options = new VectorSearchOptions<ProductDocumentRecord>();
        if (!string.IsNullOrWhiteSpace(domain))
            options.Filter = r => r.Domain == domain;

        // ③ 向量库 KNN 检索：SqliteVec vec0 在 DB 端算相似度，只返回 top 条命中，不拉全量
       
        await foreach (var result in collection.SearchAsync(embedding.Vector, top, options, ct))
        {
            var record = result.Record;
            // SqliteVec cosine distance（score = 1 - 余弦相似度，越大越不相关，实测校准）→ 转相似度（0-1，高=相关）。
            // 相似度低于阈值（弱匹配）不返回：避免 LLM 拿到无关商品硬推（校准：相关≈0.80 / 明显无关≈0.40）。
            var similarity = 1f - (float)(result.Score ?? 0);
            if (similarity < MinSimilarity)
                continue;
            hits.Add(new ProductSearchHit(
                record.ProductId, record.Name, record.Category, (decimal)record.Price, similarity));
        }

        return hits;
    }

    /// <inheritdoc />
    /// <summary>
    /// 构建索引：从商品库生成向量记录并入库（幂等）。SemaphoreSlim 串行化构建，
    /// 并发调用（启动预热 + 首次检索）时后到者等待构建完成，看到 <see cref="_indexed"/> 后直接返回。
    /// </summary>
    public async Task EnsureIndexedAsync(CancellationToken ct = default)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            if (_indexed) return;

            using var scope = scopeFactory.CreateScope();
            var products = scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAll();

            // 幂等建表（新库 / 清理后首次构建需创建 collection 的数据表 + vec 虚拟表）
            await collection.EnsureCollectionExistsAsync(ct);

            // 从商品构建向量记录：Text = 拼接描述（embedding 输入），命中字段（ProductId/Name/Category/Price）冗余存储供检索展示
            var records = products.Select(p => new ProductDocumentRecord
            {
                Id = $"product-{p.Id}",
                Domain = "product",
                ProductId = p.Id,
                Name = p.Name,
                Category = p.Category,
                Price = (double)p.Price,
                Text = $"{p.Name}。类别：{p.Category}。标签：{string.Join("、", p.Tags)}。价格：¥{p.Price}",
            }).ToList();

            // 批量生成向量 → 填 Embedding → upsert（ReadOnlyMemory&lt;float&gt; 是 SqliteVec 原生支持类型，手动填充）
            var embeddings = await embeddingGenerator.GenerateAsync(records.Select(r => r.Text), null, ct);
            for (var i = 0; i < records.Count; i++)
                records[i].Embedding = embeddings[i].Vector;

            await collection.UpsertAsync(records, ct);
            _indexed = true;
        }
        finally
        {
            _buildLock.Release();
        }
    }
}
