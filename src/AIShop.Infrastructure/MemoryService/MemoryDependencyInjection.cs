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
    public static IServiceCollection AddMemoryService(this IServiceCollection services)
    {
        // 记忆存储：SqliteMemoryStore（IMemoryStore），与 EF 同用 aishop.db（Mem0 自建 memories / memory_history 表）
        services.AddSingleton<SqliteMemoryStore>(_ => new SqliteMemoryStore("aishop.db"));
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
