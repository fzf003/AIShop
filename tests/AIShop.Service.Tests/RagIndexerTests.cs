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
    private FakeProductRepository _repo = null!;

    public Task InitializeAsync()
    {
        // 临时文件库而非 in-memory 共享连接：SqliteConnection 非线程安全，文件库多连接并发安全（learnings T16 同款经验）
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag_indexer_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        // 与 AddRag（design §5.6）一致的注册方式：vector store + collection（注册抽象 VectorStoreCollection，AB-1）
        services.AddSqliteVectorStore(_ => connectionString);
        services.AddSqliteCollection<string, ProductDocumentRecord>(RagOptions.CollectionName, _ => connectionString);
        // IProductRepository 在应用中为 Scoped（DependencyInjection.cs），RagIndexer 经 IServiceScopeFactory 解析；
        // 测试用共享实例（Scoped 工厂每次返回同一 _repo），使「业务库变更 → 增量/全量重建读到新状态」可观察
        _repo = new FakeProductRepository();
        services.AddScoped<IProductRepository>(_ => _repo);

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

    [Fact]
    public async Task UpsertProduct_SameKeyRepeated_IsIdempotent_NoDuplicateRows()
    {
        // AI-5：UpsertProductAsync 同 key 重复调用幂等（按 key REPLACE），不产生重复行
        await _indexer.RebuildAsync();
        Assert.Equal(18, await CountRecordsAsync());

        var product5 = ProductSeedData.Products.Single(p => p.Id == 5);
        await _indexer.UpsertProductAsync(product5);
        await _indexer.UpsertProductAsync(product5);

        // 重复 upsert 后仍 18 条；ProductId=5 恰 1 条（GetSingleAsync 用 SingleOrDefault，重复会抛异常）
        Assert.Equal(18, await CountRecordsAsync());
        var record5 = await GetSingleAsync(r => r.ProductId == 5);
        Assert.NotNull(record5);
        Assert.Equal("product-5", record5!.Id);
    }

    [Fact]
    public async Task RemoveProduct_DeletesExistingKey()
    {
        // AI-5：RemoveProductAsync 按 key 移除对应商品
        await _indexer.RebuildAsync();
        Assert.Equal(18, await CountRecordsAsync());

        await _indexer.RemoveProductAsync(5);
        Assert.Equal(17, await CountRecordsAsync());
        Assert.Null(await GetSingleAsync(r => r.ProductId == 5));
    }

    [Fact]
    public async Task RemoveProduct_NonExistentKey_NoSideEffect()
    {
        // AI-5：删除不存在 key 无副作用（幂等），不抛异常、不影响既有记录
        await _indexer.RebuildAsync();
        Assert.Equal(18, await CountRecordsAsync());

        await _indexer.RemoveProductAsync(999); // 商品种子 Id 为 1..18，999 不存在
        Assert.Equal(18, await CountRecordsAsync());
    }

    [Fact]
    public async Task Upsert_Incremental_ConvergesToSameStateAsFullRebuild()
    {
        // AI-5：增量同步结果与全量重建结果一致（fake embedding，不调真实 LLM）
        await _indexer.RebuildAsync();
        Assert.Equal(18, await CountRecordsAsync());

        // 业务库更新商品 5（改名），用全新 Product 实例避免污染共享种子数据
        _repo.Products = _repo.Products
            .Select(p => p.Id == 5
                ? new Product { Id = 5, Name = "新款意式浓缩咖啡机", Category = p.Category, Tags = p.Tags, Price = p.Price, Emoji = p.Emoji }
                : p)
            .ToArray();
        await _indexer.UpsertProductAsync(_repo.Products.Single(p => p.Id == 5));

        // 增量后：条数不变、商品 5 已更新
        Assert.Equal(18, await CountRecordsAsync());
        Assert.Equal("新款意式浓缩咖啡机", (await GetSingleAsync(r => r.ProductId == 5))!.Name);

        // 全量重建后与增量结果完全一致（条数与商品 5 内容都收敛到业务库当前状态）
        await _indexer.RebuildAsync();
        Assert.Equal(18, await CountRecordsAsync());
        Assert.Equal("新款意式浓缩咖啡机", (await GetSingleAsync(r => r.ProductId == 5))!.Name);
    }

    [Fact]
    public async Task IncrementalFailure_SetsDirty_AndNextEnsureIndexed_FullyRebuilds()
    {
        // AI-5：增量失败置脏标记 → 下次 EnsureIndexedAsync 触发全量重建兜底（最终一致）
        // 先经 EnsureIndexedAsync 置位 _indexed=true，确保后续重建是由「脏标记」触发而非「未构建」触发
        await _indexer.EnsureIndexedAsync();
        Assert.Equal(18, await CountRecordsAsync());

        // 业务库更新商品 5（改名）；随后一次增量 upsert 失败（FailNext 模拟 embedding 抛异常）。
        // 重建收敛场景选「内容变更」：改名可被「重建后 upsert 覆盖」观察到；
        // 「业务库删除 → 脏标记重建收敛删除」的 ghost 场景由 DirtyRebuild_AfterBusinessDelete_RemovesGhostRecord 覆盖。
        _repo.Products = _repo.Products
            .Select(p => p.Id == 5
                ? new Product { Id = 5, Name = "新款意式浓缩咖啡机", Category = p.Category, Tags = p.Tags, Price = p.Price, Emoji = p.Emoji }
                : p)
            .ToArray();
        _embeddings.FailNext = true;
        await _indexer.UpsertProductAsync(_repo.Products.Single(p => p.Id == 5)); // 不抛给业务（AI-5）

        // 增量失败：商品 5 仍为旧名（upsert 在 embedding 阶段即失败，未触达 collection 写）
        Assert.Equal("意式浓缩咖啡机", (await GetSingleAsync(r => r.ProductId == 5))!.Name);

        // 脏标记兜底：_indexed 已为 true，若无脏标记逻辑此处会走快速路径短路、商品 5 保持旧名；
        // 实测触发全量重建 → 收敛到业务库当前状态（商品 5 改名），且 embedding 确实被再次调用
        await _indexer.EnsureIndexedAsync();
        Assert.Equal("新款意式浓缩咖啡机", (await GetSingleAsync(r => r.ProductId == 5))!.Name);
        Assert.Equal(3, _embeddings.CallCount); // 1(初建) + 1(失败增量) + 1(脏标记重建)
    }

    [Fact]
    public async Task DirtyRebuild_AfterBusinessDelete_RemovesGhostRecord()
    {
        // Fix B（AI-5 删除收敛）：脏标记触发的全量重建必须清空 collection，否则
        // 「业务库已删、增量删除失败置脏标记」留下的幽灵记录永远无法被重建清除，
        // 检索会召回已删商品。旧实现 RebuildAsync 是 upsert-only（不清空），此场景下
        // 重建后仍 18 条（product-5 幽灵残留）；修复后开头清空 → 从业务库重建 17 条。
        await _indexer.EnsureIndexedAsync();
        Assert.Equal(18, await CountRecordsAsync());

        // 业务库删除商品 5（未来商品删除场景）：collection 仍残留 product-5（模拟
        // RemoveProductAsync 失败置脏标记、未真正删除——脏标记的重建兜底须收敛删除）
        _repo.Products = _repo.Products.Where(p => p.Id != 5).ToArray();

        // 触发脏标记：一次失败的增量 upsert（FailNext 模拟 embedding 抛异常 → 置脏标记、不改 collection）
        _embeddings.FailNext = true;
        await _indexer.UpsertProductAsync(_repo.Products[0]); // 不抛给业务（AI-5）

        // 脏标记兜底重建：修复后 RebuildAsync 开头清空 collection → 从业务库重建 17 条，幽灵记录被清除
        await _indexer.EnsureIndexedAsync();
        Assert.Equal(17, await CountRecordsAsync());
        Assert.Null(await GetSingleAsync(r => r.ProductId == 5));
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

    /// <summary>
    /// fake IProductRepository：默认返回 18 条种子数据（与真实 ProductRepository.GetAll 语义一致）。
    /// Products 可替换：测试可模拟「业务库变更」（改名/删除），供「增量 vs 全量重建一致性」「脏标记兜底」观察。
    /// </summary>
    private sealed class FakeProductRepository : IProductRepository
    {
        public IReadOnlyList<Product> Products { get; set; } = ProductSeedData.Products;

        public IReadOnlyList<Product> GetAll() => Products;

        public Product? QueryFilter(Func<Product, bool> predicate) => Products.FirstOrDefault(predicate);
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

        /// <summary>若为 true，下一次 GenerateAsync 抛异常后自动清除（模拟「本次增量失败、下次恢复」，供脏标记测试用）。</summary>
        public bool FailNext { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            // 模拟增量失败路径（AI-5）：本次抛异常，随后自动恢复
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("模拟增量 embedding 失败");
            }

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
