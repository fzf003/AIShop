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
/// RagIndexer 核心构建测试（Task 6，design §5.5，AI-1/AI-5）。
/// fake embedding（AI-4：确定性假向量，不调真实 LLM / 外部 Embedding API）+ 临时 SQLite 向量库文件。
/// 每个测试实例独立临时库（Guid 唯一命名），串行无关，无跨测试共享状态。
/// </summary>
public sealed class RagIndexerTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private FakeEmbeddingGenerator _embeddings = null!;
    private RagIndexer _indexer = null!;
    private VectorStoreCollection<string, ProductDocumentRecord> _collection = null!;

    public Task InitializeAsync()
    {
        // 临时文件库而非 in-memory 共享连接：SqliteConnection 非线程安全，文件库多连接并发安全（learnings T16 同款经验）
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag_indexer_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        // 与 AddRag（design §5.6）一致的注册方式：vector store + collection（注册抽象 VectorStoreCollection，AB-1）
        services.AddSqliteVectorStore(_ => connectionString);
        services.AddSqliteCollection<string, ProductDocumentRecord>(RagOptions.CollectionName, _ => connectionString);
        // IProductRepository 在应用中为 Scoped（DependencyInjection.cs），RagIndexer 经 IServiceScopeFactory 解析
        services.AddScoped<IProductRepository>(_ => new FakeProductRepository());

        _provider = services.BuildServiceProvider();
        _collection = _provider.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>();
        _embeddings = new FakeEmbeddingGenerator();
        _indexer = new RagIndexer(_collection, _embeddings, _provider.GetRequiredService<IServiceScopeFactory>());

        return Task.CompletedTask;
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
    public async Task Rebuild_IndexesAll18SeedProducts()
    {
        // AI-1：18 条商品种子被索引为独立 collection 的 ProductDocument
        await _indexer.RebuildAsync();

        Assert.Equal(18, await CountRecordsAsync());

        // 抽查 ProductId=5：Id/Domain/Text 符合 design §4.1 契约（Text 是 embedding 输入，须确定性可复现）
        var product5 = await GetSingleAsync(r => r.ProductId == 5);
        Assert.NotNull(product5);
        Assert.Equal("product-5", product5!.Id);
        Assert.Equal(RagOptions.Domain, product5.Domain);
        Assert.Equal("意式浓缩咖啡机", product5.Name);
        var seed5 = ProductSeedData.Products.Single(p => p.Id == 5);
        Assert.Equal(
            $"{seed5.Name}。类别：{seed5.Category}。标签：{string.Join("、", seed5.Tags)}。价格：¥{seed5.Price}",
            product5.Text);
    }

    [Fact]
    public async Task EnsureIndexed_IsIdempotent_DoesNotCreateDuplicateRows()
    {
        // AI-1：重复 EnsureIndexedAsync 幂等（_indexed 短路 + upsert 按 key REPLACE），不产生重复行
        await _indexer.EnsureIndexedAsync();
        Assert.Equal(18, await CountRecordsAsync());

        await _indexer.EnsureIndexedAsync();
        Assert.Equal(18, await CountRecordsAsync());

        // 第二次走快速路径短路：embedding 只被调用一次（证明未重复构建）
        Assert.Equal(1, _embeddings.CallCount);
    }

    [Fact]
    public async Task EnsureIndexed_WhenEmbeddingFails_DoesNotMarkIndexed_AndRetrySucceeds()
    {
        // AI-5 失败路径：第一次 embedding 抛异常（模拟模型缺失/加载失败）→ 失败不置位 → 下次调用重试成功
        var failing = new FakeEmbeddingGenerator(new InvalidOperationException("模拟 embedding 加载失败"));
        var indexer = new RagIndexer(_collection, failing, _provider.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.EnsureIndexedAsync());

        // 失败后未置位成功标志，重建应能补全 18 条
        await indexer.EnsureIndexedAsync();
        Assert.Equal(18, await CountRecordsAsync());

        // 成功置位后第三次调用短路（幂等，AI-1）
        var callsBefore = failing.CallCount;
        await indexer.EnsureIndexedAsync();
        Assert.Equal(callsBefore, failing.CallCount);
    }

    /// <summary>统计 collection 内记录总数（filter 恒真 + 足够大的 top）。</summary>
    private async Task<int> CountRecordsAsync()
    {
        var count = 0;
        await foreach (var _ in _collection.GetAsync(r => r.ProductId >= 0, 100, null))
        {
            count++;
        }

        return count;
    }

    /// <summary>按 filter 取单条记录（期望恰 1 条）。</summary>
    private async Task<ProductDocumentRecord?> GetSingleAsync(System.Linq.Expressions.Expression<Func<ProductDocumentRecord, bool>> filter)
    {
        var matches = new List<ProductDocumentRecord>();
        await foreach (var record in _collection.GetAsync(filter, 1, null))
        {
            matches.Add(record);
        }

        return matches.SingleOrDefault();
    }

    /// <summary>fake IProductRepository：返回 18 条种子数据（与真实 ProductRepository.GetAll 语义一致）。</summary>
    private sealed class FakeProductRepository : IProductRepository
    {
        public IReadOnlyList<Product> GetAll() => ProductSeedData.Products;

        public Product? QueryFilter(Func<Product, bool> predicate) => ProductSeedData.Products.FirstOrDefault(predicate);
    }

    /// <summary>
    /// fake IEmbeddingGenerator（AI-4）：确定性 512 维假向量（内容与输入文本哈希相关，同文本恒等），
    /// 不调任何外部服务。支持预置失败队列：构造传入的异常按调用顺序抛出一次，用于模拟「先抛后恢复」。
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

        /// <summary>GenerateAsync 累计调用次数（用于断言短路幂等）。</summary>
        public int CallCount { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            // 模拟 embedding 失败路径（AI-5/AI-3）：抛一次，由 EnsureIndexedAsync 不置位实现下次重试
            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            var texts = values as string[] ?? values.ToArray();
            var result = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
            foreach (var text in texts)
            {
                result.Add(new Embedding<float>(DeterministicVector(text)));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        /// <summary>确定性 512 维向量：从文本 UTF-8 字节派生，同文本恒等（AI-4 确定性前提）。</summary>
        private static ReadOnlyMemory<float> DeterministicVector(string text)
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
