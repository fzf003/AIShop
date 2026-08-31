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
    private readonly object _sync = new();
    private bool _indexed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSearchHit>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        await EnsureIndexedAsync(ct);

        // ① 查询文本 → 向量（bge-small-zh ONNX，512 维）
        var embedding = (await embeddingGenerator.GenerateAsync([query], null, ct))[0];

        // ② 向量库 KNN 检索：SqliteVec vec0 在 DB 端算相似度，只返回 top 条命中，不拉全量
        var hits = new List<ProductSearchHit>(top);
        await foreach (var result in collection.SearchAsync(embedding.Vector, top, cancellationToken: ct))
        {
            var record = result.Record;
            hits.Add(new ProductSearchHit(
                record.ProductId, record.Name, record.Category, (decimal)record.Price, result.Score ?? 0));
        }

        return hits;
    }

    /// <summary>
    /// 懒构建索引：首次检索前从商品库生成向量记录并入库（幂等，加锁防并发重复构建）。
    /// </summary>
    private async Task EnsureIndexedAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_indexed) return;
        }

        using var scope = scopeFactory.CreateScope();
        var products = scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAll();

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
        lock (_sync) { _indexed = true; }
    }
}
