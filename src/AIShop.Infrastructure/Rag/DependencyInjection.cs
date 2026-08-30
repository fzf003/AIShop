using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Infrastructure;

/// <summary>
/// RAG 底座 DI 注册（design §5.6，Task 9，AB-1/AB-2）。
/// 把所有 RAG 组件组装进容器：embedding（Singleton）、SqliteVec 向量存储 + product collection、
/// 检索/索引服务（Singleton）、启动预构建 HostedService。
/// 换 provider（SqliteVec→PgVector/Qdrant/InMemory）只需改本方法内部注册与连接串（AB-1），
/// Agent/工具/TextSearchProvider/ProductDocumentRecord 零改动。
/// </summary>
public static class RagDependencyInjection
{
    /// <summary>
    /// 注册 RAG 底座。默认向量库 aishop.rag.db（RagOptions.DefaultConnectionString），
    /// 与 EF aishop.db 隔离——避免 vec0 原生表与 EF Migrations 混管，降低 blast radius（R6）。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="vectorConnectionString">向量库连接串；null 时用默认 aishop.rag.db</param>
    public static IServiceCollection AddRag(this IServiceCollection services, string? vectorConnectionString = null)
    {
        vectorConnectionString ??= RagOptions.DefaultConnectionString;

        // embedding 单例：模型路径相对输出目录（csproj Content 复制目标 = AppContext.BaseDirectory）。
        // 模型缺失/加载失败在 EmbeddingGenerator 构造时抛明确异常 → 索引构建失败 → 检索降级关键词路（AI-3）。
        // 不用 design 草案的 RagOptions.GetEmbeddingModelPath(sp)：路径是静态常量、用不到 sp，
        // 引入带未用参的方法会触发 SonarAnalyzer S1172，直接在此内联 Path.Combine 更简洁。
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            new EmbeddingGenerator(
                Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingModelPath),
                Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingVocabPath)));

        // SqliteVec provider：注册 VectorStore + VectorStoreCollection<string, ProductDocumentRecord> 抽象（AB-1）。
        // RagIndexer/RagSearchService 构造只注入抽象类型，不直接依赖 SqliteVec 具体类型，故换 provider 只改本方法。
        services.AddSqliteVectorStore(_ => vectorConnectionString);
        services.AddSqliteCollection<string, ProductDocumentRecord>(RagOptions.CollectionName, _ => vectorConnectionString);

        // 检索/索引服务均无状态、Singleton；IProductRepository 是 Scoped（Infrastructure/DependencyInjection.cs），
        // 经 IServiceScopeFactory 每请求解析 scoped 实例，避免根容器解析 Scoped 的隐患（与 RagIndexer 构造注释一致）
        services.AddSingleton<IRagIndexer, RagIndexer>();
        services.AddSingleton<IRagSearchService, RagSearchService>();

        // 启动预构建：EnsureIndexedAsync 失败仅 Warning、不阻塞启动（RagIndexerHostedService 内部处理），
        // 检索期 RagSearchService 还会再调一次作懒构建兜底（design §2.4）
        services.AddHostedService<RagIndexerHostedService>();

        return services;
    }
}
