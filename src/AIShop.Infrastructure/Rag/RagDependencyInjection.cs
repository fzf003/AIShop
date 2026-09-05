using AIShop.Core.Interfaces;
using AIShop.Infrastructure.MemoryService;
using AIShop.Infrastructure.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Infrastructure;

/// <summary>
/// RAG 能力 DI 注册（极简版）：本地 bge 嵌入 + SqliteVec 向量库 + 商品语义搜索。
/// 索引由 <see cref="ProductSemanticSearch"/> 首次检索时懒构建，无需启动预构建服务。
/// </summary>
public static class RagDependencyInjection
{
    /// <summary>向量库连接串（独立于 EF 业务库，避免 vec0 原生表与 EF Migrations 混管）。</summary>
    public const string VectorConnectionString = "Data Source=aishop.rag.db";

    /// <summary>商品描述集合名。</summary>
    public const string CollectionName = "product";

    /// <summary>
    /// 注册 RAG 能力：bge embedding（Singleton）、SqliteVec 向量存储 + 商品集合、商品语义搜索。
    /// <paramref name="connectionString"/> 缺省时回退默认常量 <see cref="VectorConnectionString"/>（老 <c>AddRagService()</c> 无参调用零行为变化）；
    /// AguiHost 等新宿主可传入独立向量库连接串（如 <c>"Data Source=agui.rag.db"</c>）实现数据隔离。
    /// 换向量库（SqliteVec→PgVector/Qdrant 等）只需改默认常量与 provider 注册，业务层零改动。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">SqliteVec 向量库连接串；为 null 时使用默认 <see cref="VectorConnectionString"/>。</param>
    public static IServiceCollection AddRagService(this IServiceCollection services, string? connectionString = null)
    {
        // 局部连接串：显式传入优先，否则回退默认常量（保持老无参调用行为不变）
        var conn = connectionString ?? VectorConnectionString;

        // 本地 bge embedding（512 维，中文）：模型文件由 csproj Content 复制到输出目录（gitignore 不入库）
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            new EmbeddingGenerator(
                Path.Combine(AppContext.BaseDirectory, "Models", "bge-small-zh-v1.5", "model.onnx"),
                Path.Combine(AppContext.BaseDirectory, "Models", "bge-small-zh-v1.5", "vocab.txt")));

        // SqliteVec 向量库 + 商品集合（注册 VectorStoreCollection&lt;string, ProductDocumentRecord&gt; 抽象）
        services.AddSqliteVectorStore(_ => conn);
        services.AddSqliteCollection<string, ProductDocumentRecord>(CollectionName, _ => conn);

        // 商品语义搜索（search_product 工具用；内部懒构建索引）
        services.AddSingleton<IProductSemanticSearch, ProductSemanticSearch>();

        return services;
    }
}
