using AIShop.Core.Interfaces;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;

namespace AIShop.Service.Tests;

/// <summary>
/// AddRag DI 注册测试（Task 9，design §5.6，AB-1/AB-2）。
/// 验证：① AddRag() 注册后所有 RAG 组件从容器可解析（DI 组装完整性，AB-1）；
/// ② 新增异构知识源只需「新增 record 类型 + collection 注册」即可解析，不改既有表结构（AB-2）；
/// ③ RagSearchService 构造注入的是 VectorData 抽象而非 SqliteVec 具体类型（AB-1 代码结构证明）。
/// fake embedding（AI-4：确定性假向量，不调真实 LLM / 不加载 ONNX 模型）+ 临时 SQLite 文件库。
/// 注意：ServiceCollection.BuildServiceProvider() 不会启动 HostedService（只有 IHost.StartAsync 会），
/// 故解析 RagIndexerHostedService 只创建实例、不触发 ExecuteAsync 的真实索引构建。
/// </summary>
public sealed class RagDependencyInjectionTests : IAsyncLifetime
{
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        // 临时文件库（Guid 唯一命名）：与 AddRag 默认的 aishop.rag.db 隔离，避免测试互相干扰
        _dbPath = Path.Combine(Path.GetTempPath(), $"rag_di_{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // SQLite 连接池会锁住文件库，清池后才能删除临时库（learnings T16/R1 同款清理）
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void AddRag_AllComponents_ResolvableFromProvider()
    {
        // AB-1：AddRag() 注册后检索/索引/collection/embedding 全部可从容器解析（DI 组装完整性）
        var fakeEmbedding = new FakeEmbeddingGenerator();
        var services = new ServiceCollection();
        services.AddRag($"Data Source={_dbPath}");
        // 覆盖为 fake embedding：AddRag 的默认注册是真实 ONNX EmbeddingGenerator（模型缺失会抛异常），
        // MS DI 对同一服务类型「后注册覆盖先注册」→ 本测试无需真实模型（AI-4），也不触碰真实索引构建
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(fakeEmbedding);

        using var provider = services.BuildServiceProvider();

        // 后注册的 fake 生效（证明覆盖注册成功，AddRag 真实 embedding 未被实例化）
        Assert.Same(fakeEmbedding, provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        // 检索/索引/向量库抽象均能解析且为预期实现类型（DI 完整装配，AB-1）
        Assert.IsType<RagSearchService>(provider.GetRequiredService<IRagSearchService>());
        Assert.IsType<RagIndexer>(provider.GetRequiredService<IRagIndexer>());
        Assert.NotNull(provider.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>());

        // AddHostedService 注册生效：容器含恰一个 RagIndexerHostedService（启动预构建，design §2.4）
        Assert.IsType<RagIndexerHostedService>(
            Assert.Single(provider.GetServices<IHostedService>().OfType<RagIndexerHostedService>()));
    }

    [Fact]
    public void AddRag_SecondCollectionType_Resolvable_ProvesExtensionPattern()
    {
        // AB-2：新增异构知识源（如订单 FAQ）只需「新增 record 类型 + collection 注册」，
        // 既有 product collection 与容器解析链路零改动（不改既有表结构，design §7 out-of-scope 的证明模式）
        var services = new ServiceCollection();
        services.AddRag($"Data Source={_dbPath}");
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        // 新增领域 collection（占位 record OrderFaqRecord 见文件底部）——AB-2 扩展模式的核心一步
        services.AddSqliteCollection<string, OrderFaqRecord>("order_faq", _ => $"Data Source={_dbPath}");

        using var provider = services.BuildServiceProvider();

        // 两个 collection 都能解析：证明异构源可并行注册、零表结构改动
        Assert.NotNull(provider.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>());
        Assert.NotNull(provider.GetRequiredService<VectorStoreCollection<string, OrderFaqRecord>>());
    }

    [Fact]
    public void RagSearchService_ConstructorInjectsAbstractCollection_NotSqliteVecConcrete()
    {
        // AB-1 代码结构证明：构造注入类型为 VectorData 抽象 VectorStoreCollection<string, ProductDocumentRecord>，
        // 不出现 SqliteVec 具体类型 → provider 可替换（换 provider 只改 AddRag 内部注册）
        var ctor = typeof(RagSearchService).GetConstructors().Single();
        var collectionParam = ctor.GetParameters().Single(p => p.Name == "collection");

        Assert.Equal(typeof(VectorStoreCollection<string, ProductDocumentRecord>), collectionParam.ParameterType);
        Assert.DoesNotContain("SqliteVec", collectionParam.ParameterType.FullName);
    }

    /// <summary>
    /// fake IEmbeddingGenerator（AI-4）：确定性 512 维假向量（内容与文本 UTF-8 字节派生，同文本恒等），
    /// 不调任何外部服务、不加载 ONNX 模型。本测试类仅用于 DI 解析，GenerateAsync 不会被真实调用。
    /// </summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texts = values as string[] ?? values.ToArray();
            var result = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
            foreach (var text in texts)
            {
                result.Add(new Embedding<float>(VectorFor(text)));
            }
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        /// <summary>确定性 512 维向量：从文本 UTF-8 字节派生，同文本恒等（AI-4 确定性前提）。</summary>
        private static ReadOnlyMemory<float> VectorFor(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var vector = new float[RagOptions.EmbeddingDimensions];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = bytes.Length == 0 ? 0f : (bytes[i % bytes.Length] - 128f) / 128f;
            }
            return vector;
        }
    }

    /// <summary>
    /// AB-2 占位 record：模拟「未来异构知识源（如订单 FAQ）」的领域 record（VectorData 标注与 ProductDocumentRecord 同构）。
    /// 仅用于测试证明「新增 record 类型 + collection 注册即可解析」，不实际实现订单 FAQ 功能（design §7 out of scope）。
    /// </summary>
    private sealed class OrderFaqRecord
    {
        [VectorStoreKey]
        public string Id { get; set; } = "";

        [VectorStoreData]
        public string Question { get; set; } = "";

        [VectorStoreData]
        public string Answer { get; set; } = "";

        [VectorStoreVector(RagOptions.EmbeddingDimensions)]
        public ReadOnlyMemory<float> Embedding { get; set; }
    }
}
