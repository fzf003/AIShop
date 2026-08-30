using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// RAG 索引构建与维护（design §5.5，Task 6 核心部分）。
/// 索引是派生数据：权威在业务库（IProductRepository），本类保证「索引最终一致」，不阻塞业务写路径。
/// 不直接依赖 SqliteVec 具体类型（AB-1）：只注入 VectorData 抽象 VectorStoreCollection，换 provider 只需改 AddRag 内部注册。
/// </summary>
public sealed class RagIndexer : IRagIndexer
{
    private readonly VectorStoreCollection<string, ProductDocumentRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>并发构建锁（R15）：Singleton 懒构建下多会话并发访问，用 SemaphoreSlim 保证只构建一次。</summary>
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    /// <summary>索引已构建标志：成功置位；失败不置位 → 下次调用重试（AI-5）。</summary>
    private bool _indexed;

    public RagIndexer(
        VectorStoreCollection<string, ProductDocumentRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IServiceScopeFactory scopeFactory)
    {
        _collection = collection;
        _embeddingGenerator = embeddingGenerator;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task EnsureIndexedAsync(CancellationToken ct = default)
    {
        // 快速路径：已构建直接返回（幂等，AI-1），避免每次检索都走锁
        if (_indexed)
        {
            return;
        }

        // 慢路径：SemaphoreSlim 互斥防多会话并发重复构建（R15）
        await _buildLock.WaitAsync(ct);
        try
        {
            // double-check：等待锁期间可能已被其他会话构建完成
            if (_indexed)
            {
                return;
            }

            await RebuildAsync(ct);

            // 成功才置位；失败时异常向上传播、_indexed 保持 false → 下次调用重试（AI-5）
            _indexed = true;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        // 1. 幂等建表：product 数据表 + vec_product 虚拟表（已存在则无操作，重跑全量/增量安全）
        await _collection.EnsureCollectionExistsAsync(ct);

        // 2. 经 IServiceScopeFactory 解析 scoped IProductRepository（RagIndexer 是 Singleton，
        //    不能直接注入 scoped 服务；这正是设计用 scopeFactory 而非直接注入 repo 的原因，见 design §5.5）
        IReadOnlyList<Product> products;
        using (var scope = _scopeFactory.CreateScope())
        {
            products = scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAll();
        }

        // 3. 商品 → ProductDocument（design §4.1 Text 拼接规则，AI-2 确定性）
        var documents = products.Select(BuildDocument).ToArray();
        if (documents.Length == 0)
        {
            // 空目录：索引为建表后的空状态，无记录可写
            return;
        }

        // 4. 批量 embedding（一次调用生成全部文本向量；Text 是确定性拼接，同文本恒等向量）
        var embeddings = await _embeddingGenerator.GenerateAsync(
            documents.Select(d => d.Text), cancellationToken: ct);

        // 5. 映射为 ProductDocumentRecord 并显式填充 Embedding
        //    （[VectorStoreVector] 类型为 SqliteVec 原生支持，upsert 不会自动生成向量，须显式传入）
        var records = documents.Zip(embeddings, (doc, embedding) =>
        {
            var record = ProductDocumentMapping.ToRecord(doc);
            record.Embedding = embedding.Vector;
            return record;
        }).ToArray();

        // 6. 按 collection key 批量 upsert（REPLACE，幂等：同 key 重跑即增量更新，不产生重复行，AI-1）
        await _collection.UpsertAsync(records, ct);
    }

    /// <summary>
    /// 商品 → RAG 知识文档（design §4.1）。Id = "product-{id}" 即 collection key；
    /// Domain = product（供检索 options.Filter 领域过滤，AK-3）。
    /// </summary>
    private static ProductDocument BuildDocument(Product product) =>
        new(
            Id: $"product-{product.Id}",
            Domain: RagOptions.Domain,
            ProductId: product.Id,
            Name: product.Name,
            Category: product.Category,
            Tags: product.Tags,
            Price: product.Price,
            Emoji: product.Emoji);

    /// <summary>
    /// 增量新增/更新（design §5.5）：单条商品按 key upsert（REPLACE，幂等，AI-5）。
    /// 注意：Task 7 在此追加完整增量语义（失败 Log.Warning + 置索引脏标记），此处仅满足接口编译的最小实现——
    /// 业务写端点本变更不实施，静态种子数据下无调用方，机制就位即可。
    /// </summary>
    public async Task UpsertProductAsync(Product product, CancellationToken ct = default)
    {
        var document = BuildDocument(product);
        var embeddings = await _embeddingGenerator.GenerateAsync([document.Text], cancellationToken: ct);
        var record = ProductDocumentMapping.ToRecord(document);
        record.Embedding = embeddings[0].Vector;
        await _collection.UpsertAsync(record, ct);
    }

    /// <summary>
    /// 增量删除（design §5.5）：按 collection key "product-{id}" 移除；删不存在 key 无副作用（幂等，AI-5）。
    /// 注意：Task 7 在此追加失败置脏标记语义，此处仅满足接口编译的最小实现。
    /// </summary>
    public async Task RemoveProductAsync(int productId, CancellationToken ct = default)
    {
        await _collection.DeleteAsync($"product-{productId}", ct);
    }
}
