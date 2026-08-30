using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Serilog;

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

    /// <summary>
    /// 索引脏标记（AI-5，内存标志）：增量同步失败（Upsert/Remove 抛异常）时置位。
    /// 索引是派生数据，权威在业务库；脏标记让「下次 EnsureIndexedAsync」走全量重建兜底，索引最终一致。
    /// 设计取舍：用内存标志而非 DB 标志——首期静态种子数据量小、单实例部署，重建成本低；
    /// 未来多实例部署时可升级为 DB 标志（见 design §5.5「内存标志或 DB 标志」）。
    /// </summary>
    private bool _dirty;

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
        // 快速路径：已构建且无脏标记直接返回（幂等，AI-1），避免每次检索都走锁
        if (_indexed && !_dirty)
        {
            return;
        }

        // 慢路径：SemaphoreSlim 互斥防多会话并发重复构建（R15）
        await _buildLock.WaitAsync(ct);
        try
        {
            // double-check：等待锁期间可能已被其他会话构建完成 / 脏标记已被重建清除
            if (_indexed && !_dirty)
            {
                return;
            }

            // 统一走全量重建：既覆盖「首次未构建」，也覆盖「脏标记兜底」（RebuildAsync 从权威业务库
            // 重建全量，正是最终一致兜底所需，AI-5）。重建成功后才清除脏标记——期间若 RebuildAsync
            // 抛异常，_indexed/_dirty 保持原状 → 下次调用重试。
            await RebuildAsync(ct);

            _indexed = true;
            _dirty = false;
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
    /// 增量新增/更新（design §5.5，AI-5）：单条商品按 key upsert（REPLACE，幂等）。
    /// 失败不抛给业务（业务正确性不依赖索引）：仅 Log.Warning + 置脏标记，
    /// 由下次 EnsureIndexedAsync 全量重建兜底（最终一致）。
    /// 设计取舍：增量路径不参与 _buildLock——保证业务写路径不被索引构建阻塞（design「不阻塞业务返回」），
    /// 代价是与全量重建并发时可能触发 SQLite 写锁竞争 → 置脏标记走兜底，最终一致仍成立。
    /// </summary>
    public async Task UpsertProductAsync(Product product, CancellationToken ct = default)
    {
        try
        {
            var document = BuildDocument(product);
            var embeddings = await _embeddingGenerator.GenerateAsync([document.Text], cancellationToken: ct);
            var record = ProductDocumentMapping.ToRecord(document);
            record.Embedding = embeddings[0].Vector;
            await _collection.UpsertAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 增量失败仅置脏标记，不抛给业务；OperationCanceledException 正常传播（调用方取消语义）
            Log.Warning(ex, "RAG index upsert failed for product {ProductId}, marking index dirty", product.Id);
            _dirty = true;
        }
    }

    /// <summary>
    /// 增量删除（design §5.5，AI-5）：按 collection key "product-{id}" 移除；删不存在 key 无副作用（幂等）。
    /// 失败处理与 UpsertProductAsync 一致：Log.Warning + 置脏标记，不抛给业务。
    /// </summary>
    public async Task RemoveProductAsync(int productId, CancellationToken ct = default)
    {
        try
        {
            await _collection.DeleteAsync($"product-{productId}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "RAG index remove failed for product {ProductId}, marking index dirty", productId);
            _dirty = true;
        }
    }
}
