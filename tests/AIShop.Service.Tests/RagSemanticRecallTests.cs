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
/// RAG 语义召回集成测试（Task 12，design §5.3 / §8 R13，spec AR-2）。
/// 用**真实 bge-small-zh-v1.5 ONNX 模型**（非 fake embedding）+ 18 条种子数据构建索引，
/// 验证「适合送礼的咖啡机」类模糊语义查询能召回正确商品（AR-2 真实语义召回）。
/// 验收口径 = 「top-k 含正确商品」（handoff-1 R10 结论：18 条量级向量补充项语义发散，
/// 以 top-k 包含目标为口径而非 top1 严格唯一）。模型文件缺失时整类 skip（R13，
/// 复用 EmbeddingModelFactAttribute 开关；~95MB 不提交 git，CI/未下载模型的机器不失败）。
/// 测试不调用任何真实 LLM / 外部 Embedding API（AI-4：本地 ONNX 推理）。
/// </summary>
public sealed class RagSemanticRecallTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private EmbeddingGenerator _embeddings = null!;
    private RagSearchService _service = null!;
    private VectorStoreCollection<string, ProductDocumentRecord> _collection = null!;

    public async Task InitializeAsync()
    {
        // 临时文件库而非 in-memory 共享连接：SqliteConnection 非线程安全，
        // 文件库多连接并发安全（learnings T16 同款经验）；每个测试实例独立 Guid 命名库，无跨测试共享状态
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag_semantic_{Guid.NewGuid():N}.db");
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

        // 真实本地 ONNX embedding（AI-4 允许）：与生产链路完全一致（512 维 + vocab.txt + CLS pooling + L2 normalize）
        _embeddings = new EmbeddingGenerator(
            Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingModelPath),
            Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingVocabPath));

        // 经真实模型 + RagIndexer 构建 18 条商品索引（与生产启动预构建同链路，AI-1）
        var indexer = new RagIndexer(
            _collection, _embeddings, _provider.GetRequiredService<IServiceScopeFactory>());
        await indexer.EnsureIndexedAsync();

        _service = new RagSearchService(
            _collection, _embeddings, indexer, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public async Task DisposeAsync()
    {
        // 先释放 ONNX InferenceSession（原生句柄），再释放 provider、清 SQLite 连接池后删临时库
        _embeddings.Dispose();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [EmbeddingModelFact]
    public async Task SearchProductsAsync_FuzzySemanticQuery_TopKContainsTargetProduct()
    {
        // AR-2：真实语义召回——「适合送礼的咖啡机」不含「意式浓缩」字面（Name/Tags 均无此短语），
        // 经真实 embedding 向量路召回咖啡机类 ProductId=5（handoff-1 R10 实测该查询 cos(Product5)=0.409 居首）
        var results = await _service.SearchProductsAsync("适合送礼的咖啡机", top: 5);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.ProductId == 5);
    }

    [EmbeddingModelFact]
    public async Task SearchProductsAsync_QueryWithoutCoffeeMachineLiteral_RecallsViaVectorRouteOnly()
    {
        // AR-2 第二半：召回不依赖关键词字面命中——查询词不含「咖啡机」字面仍召回咖啡机类。
        // 先断言该查询字面上不命中任何商品（Name / Tags 均无此短语）→ 命中必来自向量路（独立召回证明）
        const string query = "制作浓缩咖啡的机器";
        Assert.DoesNotContain(ProductSeedData.Products, p =>
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || p.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

        var results = await _service.SearchProductsAsync(query, top: 5);

        Assert.Contains(results, r => r.ProductId == 5);
    }

    /// <summary>
    /// fake IProductRepository：返回 18 条种子数据（与真实 ProductRepository.GetAll 语义一致）。
    /// </summary>
    private sealed class FakeProductRepository : IProductRepository
    {
        public IReadOnlyList<Product> GetAll() => ProductSeedData.Products;

        public Product? QueryFilter(Func<Product, bool> predicate) => ProductSeedData.Products.FirstOrDefault(predicate);
    }
}

/// <summary>
/// R13 开关机制验证（Task 12 勾选项 3）：模型文件缺失时「整类 skip 且不影响其余测试」。
/// 独立于 RagSemanticRecallTests（不实现 IAsyncLifetime、不加载模型）——模型缺失时本类仍可执行，
/// 依模型文件存在性做条件断言，证明 EmbeddingModelFactAttribute 开关在两种环境下行为正确；
/// 而真实模型测试类的方法带 [EmbeddingModelFact]，模型缺失时在 xUnit 发现阶段整体跳过、不跑索引构建。
/// </summary>
public sealed class EmbeddingModelFactAttributeTests
{
    [Fact]
    public void EmbeddingModelFactAttribute_SkipFollowsModelFileExistence()
    {
        var attr = new EmbeddingModelFactAttribute();

        // 开关 = 模型文件（model.onnx + vocab.txt）是否复制到输出目录（csproj Content 复制，R13）
        var modelExists =
            File.Exists(Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingModelPath))
            && File.Exists(Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingVocabPath));

        if (modelExists)
        {
            // 模型可用：不跳过，真实模型测试正常执行
            Assert.Null(attr.Skip);
        }
        else
        {
            // 模型缺失：特性在发现阶段设置 Skip 消息（xUnit 跳过整方法），其余 [Fact] 不受影响
            Assert.False(string.IsNullOrWhiteSpace(attr.Skip));
        }
    }
}
