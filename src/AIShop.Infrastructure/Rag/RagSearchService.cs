using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Serilog;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// RAG 混合检索服务（design §5.3，Task 8）：search_product 的「关键词命中 ∪ 向量召回」RRF 融合 + 知识检索。
/// 不直接依赖 SqliteVec 具体类型（AB-1）：只注入 VectorData 抽象 VectorStoreCollection，换 provider 只需改 AddRag 内部注册。
/// </summary>
public sealed class RagSearchService : IRagSearchService
{
    private readonly VectorStoreCollection<string, ProductDocumentRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IRagIndexer _indexer;
    private readonly IServiceScopeFactory _scopeFactory;

    public RagSearchService(
        VectorStoreCollection<string, ProductDocumentRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IRagIndexer indexer,
        IServiceScopeFactory scopeFactory)
    {
        _collection = collection;
        _embeddingGenerator = embeddingGenerator;
        _indexer = indexer;
        // 关键词路需要读商品库（IProductRepository 为 Scoped，本服务是 Singleton），
        // 经 IServiceScopeFactory 每请求解析 scoped 实例，避免根容器解析 Scoped 的隐患（与 RagIndexer 同模式）
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSearchHit>> SearchProductsAsync(
        string query, int top = 5, CancellationToken ct = default)
    {
        var keywordHits = KeywordRoute(query);

        IReadOnlyList<ProductSearchHit> vectorHits = [];
        try
        {
            // 向量路 top*2 多取：RRF 融合后还要截断到 top，多取给融合留排序空间，避免融合结果过早被挤掉
            vectorHits = await VectorRouteAsync(query, top * 2, RagOptions.Domain, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AI-3 降级：向量路任一步异常（模型缺失 / 索引未建 / 存储错误）→ 仅返回关键词路，不崩溃；
            // OperationCanceledException 正常传播（调用方取消语义）
            Log.Warning(ex, "商品向量检索失败，降级为仅关键词路：{Query}", query);
        }

        // 两路均为「排名序列表」，RRF 以排名序融合（score = Σ 1/(k+rank)），平分按 ProductId 升序决胜（AI-2 确定性）
        return RrfFusion.Fuse(
            [keywordHits, vectorHits],
            h => h.ProductId.ToString(),
            top: top);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchKnowledgeAsync(
        string query, string? domain = null, int top = 3, CancellationToken ct = default)
    {
        try
        {
            // 懒构建兜底：覆盖「启动预构建失败 / 首次访问」场景（design §2.4）
            await _indexer.EnsureIndexedAsync(ct);

            // 显式生成查询向量：ProductDocumentRecord.Embedding 是 SqliteVec 原生支持类型，
            // 检索不会自动生成 embedding，必须显式传入（design §4.2）
            var embedding = (await _embeddingGenerator.GenerateAsync([query], cancellationToken: ct))[0];
            var options = new VectorSearchOptions<ProductDocumentRecord>();
            if (!string.IsNullOrWhiteSpace(domain))
            {
                // AK-3 领域过滤：VectorData 10.x 的 VectorSearchFilter 已过时，用 LINQ 表达式（D-c POC 实测）
                options.Filter = r => r.Domain == domain;
            }

            var hits = new List<KnowledgeSearchHit>();
            await foreach (var result in _collection.SearchAsync(embedding.Vector, top, options, ct))
            {
                var record = result.Record;
                hits.Add(new KnowledgeSearchHit(
                    record.Id,
                    record.Name,
                    record.Category,
                    record.Text,
                    result.Score ?? 0));
            }

            return hits;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AI-3 降级：知识检索任一步异常（模型缺失 / 索引构建失败 / 存储错误）→ 返回空结果、不抛给 TextSearchProvider
            // 工具链（否则异常会传到 Agent 循环导致整个对话失败）。与 search_product 的降级对齐——知识检索没有关键词兜底路，
            // 空结果即「无检索上下文」，TextSearchProvider 按空结果注入、Agent 无参考资料正常回复（AI-3 空结果语义）。
            // OperationCanceledException 正常传播（调用方取消语义）
            Log.Warning(ex, "知识检索失败，返回空结果：{Query}", query);
            return [];
        }
    }

    /// <summary>
    /// 关键词路（design §5.3，AR-3 回归关键）：与现状 CartToolProvider.SearchProductAsync **相同谓词**
    /// （Name.Contains || Tags.Any，OrdinalIgnoreCase），仅加显式排序。
    /// 匹配集不劣化（AR-3）；名称命中优先、组内按 ProductId 升序——显式排序保证 AI-2 确定性，
    /// 不依赖仓储返回顺序（ProductRepository.GetAll 是 DB 序，未保证按 Id 升序）。
    /// </summary>
    private IReadOnlyList<ProductSearchHit> KeywordRoute(string query)
    {
        using var scope = _scopeFactory.CreateScope();
        var all = scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAll();

        // 名称命中优先：先所有 Name 命中（按 ProductId 升序），再 Tag 命中（Name 未命中的），保持与现状一致的两段式语义
        var nameHits = all
            .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id);
        var tagHits = all
            .Where(p => !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     && p.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Id);

        // Score 占位 0：关键词路不参与 RRF 得分计算（仅排名序参与融合），语义见 Core/Models/RagSearchHits.cs 注释
        return nameHits.Concat(tagHits)
            .Select(p => new ProductSearchHit(p.Id, p.Name, p.Category, p.Price, 0))
            .ToList();
    }

    /// <summary>
    /// 向量路（design §5.3）：生成查询向量 → VectorStoreCollection.SearchAsync（注入抽象，AB-1）。
    /// domain 非空时经 options.Filter（LINQ 表达式）限定领域（AK-3，为多领域预留）。
    /// </summary>
    private async Task<IReadOnlyList<ProductSearchHit>> VectorRouteAsync(
        string query, int top, string? domain, CancellationToken ct)
    {
        // 懒构建兜底：覆盖「启动预构建失败 / 首次访问」场景（design §2.4）
        await _indexer.EnsureIndexedAsync(ct);

        var embedding = (await _embeddingGenerator.GenerateAsync([query], cancellationToken: ct))[0];
        var options = new VectorSearchOptions<ProductDocumentRecord>();
        if (!string.IsNullOrWhiteSpace(domain))
        {
            options.Filter = r => r.Domain == domain;
        }

        var hits = new List<ProductSearchHit>();
        await foreach (var result in _collection.SearchAsync(embedding.Vector, top, options, ct))
        {
            var record = result.Record;
            hits.Add(new ProductSearchHit(
                record.ProductId,
                record.Name,
                record.Category,
                (decimal)record.Price,
                result.Score ?? 0));
        }

        return hits;
    }
}
