using AIShop.AgentTelemetry;
using AIShop.AguiHost.Agents;
using AIShop.AguiHost.Model;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.MemoryService;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Serilog;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 底座 DI 装配与启动引导（agui-host T3 + T13）。
/// 底座注册与 AIShop.Api/Program.cs 同源（AddInfrastructure / AddRagService / AddMemoryService / ModelRouter /
/// AgentTelemetry 绑定），差异仅在：业务库/向量库/记忆库换为 AguiHost 独立连接串或文件（<c>agui.db</c>/
/// <c>agui.rag.db</c>/<c>agui.memory.db</c>，数据隔离 spec 验收 5；老 aishop.db / aishop.rag.db 零接触）。
/// internal + InternalsVisibleTo 暴露给 AIShop.AguiHost.Tests，供宿主级测试直接驱动装配/启动引导。
/// </summary>
internal static class AguiServiceCollectionExtensions
{
    /// <summary>AguiHost 独立业务库连接串（缺省回退点）。老 aishop.db 零接触。</summary>
    internal const string DefaultDbConnection = "Data Source=agui.db";

    /// <summary>AguiHost 独立 RAG 向量库连接串（缺省回退点）。老 aishop.rag.db 零接触。</summary>
    internal const string DefaultRagConnection = "Data Source=agui.rag.db";

    /// <summary>AguiHost 独立会话库连接串（T12，缺省回退点）。老 aishop.db 零接触。</summary>
    internal const string DefaultSessionDbConnection = "Data Source=agui.sessions.db";

    /// <summary>AguiHost 独立记忆库路径（T13，缺省回退点；SqliteMemoryStore 按路径建库）。老 aishop.db 零接触。</summary>
    internal const string DefaultMemoryDatabasePath = "agui.memory.db";

    /// <summary>
    /// 注册 AguiHost 底座 DI：EF 仓储（独立业务库）+ RAG 语义检索（独立向量库）+ Mem0 记忆（独立记忆库）+
    /// 购物工具工厂/模型路由/全局默认 <see cref="IChatClient"/> + Agent 遥测配置绑定。装配契约（spec §4.2/§4.3/§13.2）：
    /// <c>AddInfrastructure(dbConnection)</c> / <c>AddRagService(ragConnection)</c> / <c>AddMemoryService(memoryDatabasePath)</c>，
    /// 参数缺省时回退 <see cref="DefaultDbConnection"/>/<see cref="DefaultRagConnection"/>/<see cref="DefaultMemoryDatabasePath"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">应用配置（读 AgentTelemetry 节绑定遥测选项；Models 节供 ModelRouter 延迟解析）。</param>
    /// <param name="dbConnection">EF 业务库连接串；null 时用 <see cref="DefaultDbConnection"/>。</param>
    /// <param name="ragConnection">RAG 向量库连接串；null 时用 <see cref="DefaultRagConnection"/>。</param>
    /// <param name="memoryDatabasePath">Mem0 记忆库路径（<c>SqliteMemoryStore</c> 按路径建库）；null 时用
    /// <see cref="DefaultMemoryDatabasePath"/>。老 <c>AddMemoryService()</c> 无参默认 aishop.db 不受影响。</param>
    internal static IServiceCollection AddAguiBaseServices(
        this IServiceCollection services,
        IConfiguration config,
        string? dbConnection = null,
        string? ragConnection = null,
        string? memoryDatabasePath = null)
    {
        // 独立 SQLite 业务库 agui.db：复用 Infra AppDbContext + EF 仓储（MigrateAsync 由 InitializeAsync 执行，勿 EnsureCreated）
        dbConnection ??= DefaultDbConnection;
        services.AddInfrastructure(dbConnection);

        // 独立 RAG 向量库 agui.rag.db（AddRagService 可选连接串；老 AddRagService() 无参调用行为零变化）
        ragConnection ??= DefaultRagConnection;
        services.AddRagService(ragConnection);

        // 购物工具工厂（5 购物工具复用，与老 Agent 同源）：注入 IServiceScopeFactory + ICurrentUserAccessor + IProductSemanticSearch
        services.AddSingleton<CartToolProvider>();
        // 模型管道 / 多模型（读 Models 节，Agent 语义检索链路复用）
        services.AddSingleton<ModelRouter>();

        // C5 M1（agui-model-switch）：AguiHost 自建「模型 → 底层客户端」工厂（读 Models 节 + ActiveModel 缺省，
        // 每模型懒建缓存 + 每客户端 OTel 外包；见 src/AIShop.AguiHost/Model/）。M1 仅【增量注册工厂类型】——
        // 老 ModelRouter 仍是下方全局 IChatClient seam 的来源（RouterChatClient 尚未接线、无人解析本工厂），
        // M4 才切换装配面（移除 ModelRouter 注册 + 全局 IChatClient = 工厂 GetDefaultClient）。
        services.AddSingleton<IModelChatClientFactory, AguiModelClientFactory>();


        // Agent 遥测：绑定 "AgentTelemetry" 配置节，注册 AgentTelemetryOptions 单例（同 Api/Program.cs L62-66）
        var agentTelemetrySection = config.GetSection("AgentTelemetry");
        services.Configure<AgentTelemetryOptions>(agentTelemetrySection);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value);

        // 全局默认 chatClient（与 Api/Program.cs 同一创建逻辑）：模型 seam，被 AGUIShopping 装配与记忆服务
        // （IMemoryService 内部 GetRequiredService<IChatClient>()，见 Infra MemoryDependencyInjection）共用。
        // C3（修复工单，方案 2）：本单例【不再外包 ReplySanitizingChatClient】——保持纯净（仅外层 OTel 遥测包装）。
        // 理由：记忆提取 / 冲突消解 / 精排若走「面向用户的商品编号清洗」中间件，会把回复文本中的商品编号剥落，
        // 污染落库记忆（Mem0 提取的是模型可见的完整内容）。清洗是展示层规则，只应作用于面向用户的 agent 输出，
        // 故隔离到 AGUIShopping 专属 chatClient 路径显式外包（见 Program.cs keyed factory 的 agent 实参），
        // 记忆 / 内部链路一律走下方纯净 seam。遥测（UseOpenTelemetry）与清洗无关，保留在全局包装上。
        services.AddSingleton<IChatClient>(sp =>
            sp.GetRequiredService<ModelRouter>().GetDefaultChatClient()
            .AsBuilder().UseOpenTelemetry(sourceName: sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value.SourceName).Build());



        // Mem0 记忆（T13，挂独立记忆库）：老 AddMemoryService() 写死 aishop.db 会触碰老库违反数据隔离（spec 验收 5），
        // 经 T13 Infra 参数化后传入本宿主独立记忆库路径（缺省 agui.memory.db）——memories / memory_history 表由
        // SqliteMemoryStore.InitializeAsync 自建（InitializeAsync 预热，失败仅 Warning）。IMemoryService 单例解析会
        // 加载本地 bge ONNX 模型（Models/bge-small-zh-v1.5），模型缺失时在 Program keyed factory 降级不挂记忆 provider
        // （见 Program.cs ResolveMemoryService 注释），启动不因模型缺失而崩。
        memoryDatabasePath ??= DefaultMemoryDatabasePath;
        services.AddMemoryService(memoryDatabasePath);

        return services;
    }

    /// <summary>
    /// 注册 AGUIShopping 的持久化会话 store（T12：会话历史持久化）。以 <c>SqliteAgentSessionStore</c> 具体单例 +
    /// 按 <see cref="AGUIShoppingAgent.AgentName"/> keyed 的 <see cref="AgentSessionStore"/> 注册——
    /// 使 <c>MapAGUIServer(agentName, pattern)</c> 命中 keyed store（镜像 <c>AGUIEndpointRouteBuilderExtensions.cs</c>
    /// L113 <c>GetKeyedService&lt;AgentSessionStore&gt;(aiAgent.Name)</c>），取代默认 ephemeral 的 Noop 存储：
    /// SSE 流结束 <c>SaveSessionAsync</c> 落库、同 ThreadId 下次 <c>GetSessionAsync</c> 还原（重启不丢上下文）。
    /// 数据隔离：独立会话库（缺省 <see cref="DefaultSessionDbConnection"/>，不得为老 aishop.db）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="sessionDbConnection">会话库连接串；null 时用 <see cref="DefaultSessionDbConnection"/>。</param>
    internal static IServiceCollection AddAguiSessionStore(this IServiceCollection services, string? sessionDbConnection = null)
    {
        sessionDbConnection ??= DefaultSessionDbConnection;
        services.AddSingleton(new SqliteAgentSessionStore(sessionDbConnection));
        // keyed AgentSessionStore 解析具体单例：MapAGUIServer 按 agent.Name 解析命中（keyed 注册是命中前提）
        services.AddKeyedSingleton<AgentSessionStore>(AGUIShoppingAgent.AgentName,
            static (sp, _) => sp.GetRequiredService<SqliteAgentSessionStore>());
        return services;
    }

    /// <summary>
    /// 启动引导（Program top-level 与宿主级测试共用）：MigrateAsync（勿 EnsureCreated）→ 幂等播种
    /// marla/steve/fzf003 + ProductSeedData 18 商品 → RAG 向量索引预热（EnsureIndexedAsync，失败仅 Warning）→
    /// Mem0 记忆库建表（SqliteMemoryStore.InitializeAsync，失败仅 Warning）。
    /// 若已注册会话 store（AddAguiSessionStore）则预热其 agent_sessions 表（幂等建表，失败仅 Warning）。
    /// 语义对齐 AIShop.Api/Program.cs 的启动逻辑。
    /// </summary>
    /// <param name="sp">已装配 AddAguiBaseServices 的 ServiceProvider。</param>
    internal static async Task InitializeAsync(IServiceProvider sp)
    {
        // EF.IsDesignTime 跳过：避免 dotnet ef 设计时执行启动 DB 逻辑（否则 HostAbortedException），同 Api
        if (EF.IsDesignTime)
            return;

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 建 schema：走 EF Migrations（对齐老宿主约定），禁用 EnsureCreated
        await db.Database.MigrateAsync();

        // 幂等播种测试用户（对应验收 3 的用户归属：marla/steve/fzf003）
        async Task SeedUserAsync(string username, string displayName)
        {
            if (!await db.Users.AnyAsync(u => u.Username == username))
                db.Users.Add(new User { Username = username, DisplayName = displayName });
        }

        await SeedUserAsync("marla", "Marla");
        await SeedUserAsync("steve", "Steve");
        await SeedUserAsync("fzf003", "fzf003");
        await db.SaveChangesAsync();

        // 幂等播种商品：空表才写入 18 个种子商品（覆盖全新库与既有库两路径，重复启动不产生重复行）
        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(ProductSeedData.Products);
            await db.SaveChangesAsync();
        }

        // 预热商品语义搜索：加载 bge 模型 + 构建独立向量库索引（复用上方 scope，避免首次检索卡顿）；
        // 失败仅 Warning（模型缺失 / 存储错误），首次检索懒构建兜底，不阻塞启动（同 Api/Program.cs）
        try
        {
            await scope.ServiceProvider.GetRequiredService<IProductSemanticSearch>().EnsureIndexedAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RAG 索引预热失败，首次检索将懒构建兜底");
        }

        // 预热 Mem0 记忆库（T13）：幂等建 memories / memory_history 表，使首个记忆读写不承担 DDL。
        // 只解析 SqliteMemoryStore（不解析 IMemoryService——后者会加载本地 bge ONNX 模型，属运行时懒加载），
        // 建表失败仅 Warning（如路径不可写），store 首次访问仍会自建表兜底（同 Api/Program.cs L129-137）。
        try
        {
            var memoryStore = scope.ServiceProvider.GetService<SqliteMemoryStore>();
            if (memoryStore is not null)
                await memoryStore.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "记忆库初始化失败");
        }

        // 预热会话持久化 store（T12，若已经 AddAguiSessionStore 注册）：幂等建 agent_sessions 表，使首个会话
        // 请求不承担 DDL。失败仅 Warning（如 SQLite 文件路径不可写），store 首次访问仍会懒建表兜底、会话退化为
        // 不持久，不阻塞启动（仿 RAG 预热语义）。未注册（纯底座测试 / 未接线宿主）时 GetService 返回 null 跳过，
        // 不产生会话库副作用。
        try
        {
            var sessionStore = scope.ServiceProvider.GetService<SqliteAgentSessionStore>();
            if (sessionStore is not null)
                await sessionStore.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "会话持久化 store 初始化失败，会话将退化为不持久（ephemeral）");
        }
    }
}
