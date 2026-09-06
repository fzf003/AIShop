using AIShop.Infrastructure.MemoryService;
using Mem0Sharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Infrastructure;

/// <summary>
/// Mem0 记忆服务 DI 注册：
/// - <see cref="SqliteMemoryStore"/>（IMemoryStore）：记忆库与 EF 同用 aishop.db（Mem0 自建 memories 表）
/// - <see cref="IMemoryService"/> 单例：有状态（LlmMemoryExtractor / 门控 / 精排 + 本地 bge 向量），
///   chatClient 用全局默认模型 IChatClient（Program.cs 注册，供记忆提取与精排）
/// </summary>
public static class MemoryDependencyInjection
{
    /// <summary>默认记忆库路径（老宿主沿用，与 EF 业务库同用 aishop.db）；无参调用零行为变化。</summary>
    public const string DefaultMemoryDatabasePath = "aishop.db";

    /// <summary>
    /// 注册 Mem0 记忆服务：<see cref="SqliteMemoryStore"/>（IMemoryStore，独立建 memories / memory_history 表）+ 单例
    /// <see cref="IMemoryService"/>。
    /// <paramref name="databasePath"/> 缺省时回退默认常量 <see cref="DefaultMemoryDatabasePath"/>（老 <c>AddMemoryService()</c>
    /// 无参调用零行为变化）；AguiHost 等新宿主可传入独立记忆库路径（如 <c>"agui.memory.db"</c>）实现数据隔离，不触碰老 aishop.db。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="databasePath">记忆库文件路径（<see cref="SqliteMemoryStore"/> 构造按路径建库）；为 null 时使用默认
    /// <see cref="DefaultMemoryDatabasePath"/>。</param>
    public static IServiceCollection AddMemoryService(this IServiceCollection services, string? databasePath = null)
    {
        // 局部数据库路径：显式传入优先，否则回退默认常量（保持老无参调用行为不变）
        var dbPath = databasePath ?? DefaultMemoryDatabasePath;

        // 记忆存储：SqliteMemoryStore（IMemoryStore），独立于 EF 建 memories / memory_history 表
        services.AddSingleton<SqliteMemoryStore>(_ => new SqliteMemoryStore(dbPath));
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());

        // Mem0 记忆服务单例：LLM 提取事实 + 门控（防注入/新信息/权限）+ 精排 + 本地 bge 向量。
        // 模型统一用 RAG 的 bge（Models/bge-small-zh-v1.5，csproj 已复制到输出）
        services.AddSingleton<IMemoryService>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "bge-small-zh-v1.5");
            return new Mem0Sharp.MemoryService(
                store: sp.GetRequiredService<IMemoryStore>(),
                embeddings: new LocalBgeEmbeddingGenerator(modelDir),
                extractor: new LlmMemoryExtractor(chatClient),
                conflictResolver: new LlmMemoryConflictResolver(chatClient),
                admissionGate: new CompositeAdmissionGate(
                    new PromptInjectionAdmissionGate(),
                    new NoveltyAdmissionGate(),
                    new AuthorityAdmissionGate()),
                reranker: new LlmReranker(chatClient),
                entityExtractor: new RuleBasedEntityExtractor(),
                options: new MemoryOptions
                {
                    EnableHybridSearch = true,
                    DefaultTopK = 3,
                    MinimumScore = 0.03d,
                    MaxCandidateCount = 2,
                });
        });

        return services;
    }
}
