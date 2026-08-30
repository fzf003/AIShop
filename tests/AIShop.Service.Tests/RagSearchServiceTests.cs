using System.Linq.Expressions;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace AIShop.Service.Tests;

/// <summary>
/// RagSearchService 混合检索测试（Task 8，design §5.3，AR-1/AR-3/AR-4、AI-2/AI-3、AK-3/AB-1）。
/// fake embedding（AI-4：确定性假向量，不调真实 LLM / 外部 Embedding API）+ 真实 SqliteVec 临时文件库
/// （与 RagIndexerTests 同模式）：索引经 RagIndexer + fake embedding 构建，检索走真实 provider 全链路。
/// 每个测试实例独立临时库（Guid 唯一命名），无跨测试共享状态。
/// </summary>
public sealed class RagSearchServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private FakeEmbeddingGenerator _embeddings = null!;
    private RagSearchService _service = null!;
    private VectorStoreCollection<string, ProductDocumentRecord> _collection = null!;

    public async Task InitializeAsync()
    {
        // 临时文件库而非 in-memory 共享连接：SqliteConnection 非线程安全，文件库多连接并发安全（learnings T16 同款经验）
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag_search_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        // 与 AddRag（design §5.6）一致的注册方式：vector store + collection（注册抽象 VectorStoreCollection，AB-1）
        services.AddSqliteVectorStore(_ => connectionString);
        services.AddSqliteCollection<string, ProductDocumentRecord>(RagOptions.CollectionName, _ => connectionString);
        // IProductRepository 在应用中为 Scoped（DependencyInjection.cs），RagSearchService（Singleton）经 scopeFactory 解析；
        // 测试用共享实例（Scoped 工厂每次返回同一 _repo），使关键词路读到 18 条种子数据
        services.AddScoped<IProductRepository>(_ => new FakeProductRepository());

        _provider = services.BuildServiceProvider();
        _collection = _provider.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>();
        _embeddings = new FakeEmbeddingGenerator();

        // 经 EnsureIndexedAsync 构建 18 条商品索引并置位 _indexed（后续检索的懒构建兜底走短路，不重复重建）
        var indexer = new RagIndexer(
            _collection, _embeddings, _provider.GetRequiredService<IServiceScopeFactory>());
        await indexer.EnsureIndexedAsync();

        _service = new RagSearchService(
            _collection, _embeddings, indexer, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        // SQLite 连接池会锁住文件库，清池后才能删除临时库（learnings T16/R1 同款清理）
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task SearchProductsAsync_MixedRoutes_TopKContainsKeywordAndVectorHits()
    {
        // AR-1：混合检索 top-k = 「关键词命中 ∪ 向量召回」经 RRF 融合后的排序结果
        // 关键词路：「咖啡」字面命中 意式浓缩咖啡机(5)；向量路：override 让查询向量 = 专业跑鞋(3) 的文档向量 → 向量召回 3
        var product3 = await GetSingleAsync(r => r.ProductId == 3);
        _embeddings.QueryOverrides["咖啡"] = product3!.Text;

        var results = await _service.SearchProductsAsync("咖啡");

        var ids = results.Select(r => r.ProductId).ToArray();
        // 两路各自命中的商品都被融合进 top-k（top=5）
        Assert.Contains(5, ids); // 关键词路贡献（字面「咖啡」）
        Assert.Contains(3, ids); // 向量路贡献（RRF 融合）
        Assert.True(results.Count <= 5, $"top-k 截断后不应超过 5 条，实际 {results.Count}");
        // 注意：返回的 hit 是 RrfFusion 的「代表元素」（原路原始对象），Score 字段未改写为 RRF 得分；
        // 关键词路占位 0、向量路为原始距离分——此处不做 Score>0 断言（该语义见 Core/Models/RagSearchHits.cs 注释）
    }

    [Fact]
    public async Task SearchProductsAsync_KeywordRoute_MatchSetAndOrdering_MatchesCurrentPredicate()
    {
        // AR-3 回归：关键词路匹配集与现状 CartToolProvider.SearchProductAsync 的谓词一致，不劣化。
        // 用 FailNext 让向量路抛异常降级（AI-3 路径），使返回即关键词路本身，便于精确断言匹配集与顺序。
        // 期望集按种子数据手工推演（谓词 = Name.Contains || Tags.Any，OrdinalIgnoreCase；名称命中优先、组内按 ProductId 升序）
        _embeddings.FailNext = true;
        var coffee = await _service.SearchProductsAsync("咖啡");
        Assert.Equal([5], coffee.Select(r => r.ProductId).ToArray()); // 意式浓缩咖啡机（名称命中）

        _embeddings.FailNext = true;
        var runningShoes = await _service.SearchProductsAsync("跑鞋");
        Assert.Equal([3], runningShoes.Select(r => r.ProductId).ToArray()); // 专业跑鞋（名称命中）

        _embeddings.FailNext = true;
        var sports = await _service.SearchProductsAsync("运动");
        // 名称命中：智能运动手表(10) 名称含「运动」；Tag 命中（名称未命中者）：专业跑鞋(3)、高级瑜伽垫(6) 的 Tags 含「运动」。
        // 顺序 = 名称命中优先、组内按 ProductId 升序 → [10, 3, 6]
        Assert.Equal([10, 3, 6], sports.Select(r => r.ProductId).ToArray());
    }

    [Fact]
    public async Task SearchProductsAsync_ComplementaryRoutes_RecallsBothAAndB()
    {
        // AR-4 互补性：关键词路单独仅命中 A、向量路单独仅命中 B，混合路同时召回 A 与 B
        // 关键词路：「跑鞋」字面仅命中 专业跑鞋(3) = A；向量路：override 让查询向量 = 意式浓缩咖啡机(5) 的文档向量 → B
        var product5 = await GetSingleAsync(r => r.ProductId == 5);
        _embeddings.QueryOverrides["跑鞋"] = product5!.Text;

        var results = await _service.SearchProductsAsync("跑鞋");
        var ids = results.Select(r => r.ProductId).ToArray();

        // A（仅关键词路命中，向量路漏）与 B（仅向量路命中，关键词路漏）都出现在融合结果中——证明 RRF 互补（AR-4）
        Assert.Contains(3, ids);
        Assert.Contains(5, ids);
    }

    [Fact]
    public async Task SearchProductsAsync_WhenVectorRouteFails_DegradesToKeywordOnly_NoException()
    {
        // AI-3：向量路异常（模拟模型缺失/embedding 失败）→ 不抛异常、降级为仅关键词路结果
        _embeddings.FailNext = true;
        var results = await _service.SearchProductsAsync("咖啡");

        Assert.Equal([5], results.Select(r => r.ProductId).ToArray());

        // 向量路异常时无匹配查询 → 返回空结果（工具层据此输出「未找到」文案，本层不崩溃）
        _embeddings.FailNext = true;
        var noMatch = await _service.SearchProductsAsync("不存在的商品xyz");
        Assert.Empty(noMatch);
    }

    [Fact]
    public async Task SearchProductsAsync_SameQueryRepeated_ReturnsIdenticalResults()
    {
        // AI-2 确定性：相同索引状态 + 相同查询重复执行 → 结果顺序与内容一致（fake embedding 确定性 + RRF 平分按 ProductId 升序决胜）
        var product3 = await GetSingleAsync(r => r.ProductId == 3);
        _embeddings.QueryOverrides["咖啡"] = product3!.Text;

        var first = await _service.SearchProductsAsync("咖啡");
        var second = await _service.SearchProductsAsync("咖啡");

        Assert.Equal(
            first.Select(r => (r.ProductId, r.Score)).ToArray(),
            second.Select(r => (r.ProductId, r.Score)).ToArray());
        Assert.NotEmpty(first); // 非平凡断言：结果非空，重复执行比对才有意义
    }

    [Fact]
    public async Task SearchKnowledgeAsync_DomainFilter_ReturnsOnlyThatDomain()
    {
        // AK-3：domain 非空时经 options.Filter（LINQ 表达式 r => r.Domain == domain）限定领域。
        // 构造一个「order」领域的知识记录，override 让查询向量与它完全一致 → 不滤除时它必居首；
        // 断言 domain 过滤后它被排除/被包含，证明 Filter 真实生效（而非靠向量相似度偶然）。
        var orderText = "订单退货政策。类别：售后。标签：退货、政策、无理由。价格：¥0";
        await InsertRecordAsync(
            id: "order-1", domain: "order", productId: 0,
            name: "退货政策文档", category: "售后", text: orderText,
            embedding: FakeEmbeddingGenerator.VectorFor(orderText));
        _embeddings.QueryOverrides["退货"] = orderText;

        // domain="product"：order 领域记录被过滤，返回商品领域记录（不含 order-1）
        var productResults = await _service.SearchKnowledgeAsync("退货", domain: RagOptions.Domain, top: 3);
        Assert.NotEmpty(productResults);
        Assert.DoesNotContain(productResults, r => r.DocumentId == "order-1");

        // domain="order"：只返回该领域记录（order-1 应居首）
        var orderResults = await _service.SearchKnowledgeAsync("退货", domain: "order", top: 3);
        Assert.Contains(orderResults, r => r.DocumentId == "order-1");
    }

    [Fact]
    public async Task SearchKnowledgeAsync_WhenEmbeddingFails_ReturnsEmpty_NoException()
    {
        // AI-3：知识检索异常（模拟模型缺失 / embedding 推理失败）→ 不抛异常、返回空结果，
        // 与 SearchProductsAsync 的向量路降级一致（不崩溃）。知识检索没有关键词兜底路，
        // 空结果即「无检索上下文」——TextSearchProvider 按空结果注入、Agent 无参考资料正常回复（AI-3 空结果语义）。
        _embeddings.FailNext = true;
        var results = await _service.SearchKnowledgeAsync("咖啡", domain: RagOptions.Domain, top: 3);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_InjectVectorDataAbstraction_NotSqliteVecConcreteType()
    {
        // AB-1：构造注入类型为 VectorData 抽象 VectorStoreCollection<string, ProductDocumentRecord>，
        // 不出现 SqliteVec 具体类型（换 provider 只需改 AddRag 内部注册，Agent/工具/检索实现零改动）
        var ctor = typeof(RagSearchService).GetConstructors().Single();
        var collectionParam = ctor.GetParameters().Single(p => p.Name == "collection");

        Assert.Contains("VectorStoreCollection", collectionParam.ParameterType.Name);
        Assert.DoesNotContain("SqliteVec", collectionParam.ParameterType.FullName);
    }

    /// <summary>按 filter 取单条记录（期望恰 1 条）。</summary>
    private async Task<ProductDocumentRecord?> GetSingleAsync(Expression<Func<ProductDocumentRecord, bool>> filter)
    {
        var matches = new List<ProductDocumentRecord>();
        await foreach (var record in _collection.GetAsync(filter, 1, null))
        {
            matches.Add(record);
        }

        return matches.SingleOrDefault();
    }

    /// <summary>直接向 collection 写入一条记录（供 AK-3 构造异构领域记录用；与索引路径无关）。</summary>
    private async Task InsertRecordAsync(
        string id, string domain, int productId, string name, string category, string text, ReadOnlyMemory<float> embedding)
    {
        await _collection.UpsertAsync(new ProductDocumentRecord
        {
            Id = id,
            Domain = domain,
            ProductId = productId,
            Name = name,
            Category = category,
            TagsJson = "[]",
            Text = text,
            Embedding = embedding,
        });
    }

    /// <summary>
    /// fake IProductRepository：返回 18 条种子数据（与真实 ProductRepository.GetAll 语义一致）。
    /// </summary>
    private sealed class FakeProductRepository : IProductRepository
    {
        public IReadOnlyList<Product> GetAll() => ProductSeedData.Products;

        public Product? QueryFilter(Func<Product, bool> predicate) => ProductSeedData.Products.FirstOrDefault(predicate);
    }

    /// <summary>
    /// fake IEmbeddingGenerator（AI-4）：确定性 512 维假向量（内容与输入文本哈希相关，同文本恒等），
    /// 不调任何外部服务。QueryOverrides 把「查询文本 → 目标记录文本」映射为相同向量，
    /// 使对应记录在向量路中稳定居首——测试无需预测哈希相似度的偶然排名，结果确定可复现。
    /// FailNext 支持「本次抛异常、下次恢复」（AI-3 降级路径）。
    /// </summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Queue<Exception> _failures = new();

        public FakeEmbeddingGenerator(params Exception[] failures)
        {
            foreach (var failure in failures)
            {
                _failures.Enqueue(failure);
            }
        }

        /// <summary>查询文本 → 目标记录文本（取该记录的确定性向量作为查询向量）。</summary>
        public Dictionary<string, string> QueryOverrides { get; } = new();

        /// <summary>GenerateAsync 累计调用次数。</summary>
        public int CallCount { get; private set; }

        /// <summary>若为 true，下一次 GenerateAsync 抛异常后自动清除（模拟「本次失败、下次恢复」，供降级测试用）。</summary>
        public bool FailNext { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("模拟向量检索阶段 embedding 失败");
            }

            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            var texts = values as string[] ?? values.ToArray();
            var result = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
            foreach (var text in texts)
            {
                // 命中 override 时用目标记录的确定性向量作为该查询的向量（使目标记录在向量路中居首）
                var target = QueryOverrides.TryGetValue(text, out var overrideTarget) ? overrideTarget : text;
                result.Add(new Embedding<float>(VectorFor(target)));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        /// <summary>确定性 512 维向量：从文本 UTF-8 字节派生，同文本恒等（AI-4 确定性前提）。</summary>
        public static ReadOnlyMemory<float> VectorFor(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var vector = new float[RagOptions.EmbeddingDimensions];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = MathF.Sin((i + 1) * 0.01f) + bytes[i % bytes.Length] * 0.0001f;
            }

            return vector;
        }
    }
}
