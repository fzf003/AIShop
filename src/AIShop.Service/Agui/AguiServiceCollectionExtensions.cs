#pragma warning disable MAAI001 // ContextWindowCompactionStrategy 为 MAF [Experimental]（上下文压缩 API）
using AIShop.AgentTelemetry;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.MemoryService;
using AIShop.Service.Tools;
using Microsoft.Agents.AI.Compaction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;

namespace AIShop.Service.Agui;

/// <summary>
/// AguiHost 底座 DI 装配与启动引导（agui-host T3 + T13 + C5）。
/// 底座注册与 AIShop.Api/Program.cs 同源（AddInfrastructure / AddRagService / AddMemoryService / AgentTelemetry
/// 绑定；模型 seam 为 AguiHost 自建 IModelChatClientFactory/RouterChatClient，本宿主不注册老 Service ModelRouter），
/// 差异仅在：业务库/向量库/记忆库换为 AguiHost 独立连接串或文件（<c>agui.db</c>/
/// <c>agui.rag.db</c>/<c>agui.memory.db</c>，数据隔离 spec 验收 5；老 aishop.db / aishop.rag.db 零接触）。
/// internal + InternalsVisibleTo 暴露给 AIShop.AguiHost.Tests，供宿主级测试直接驱动装配/启动引导。
/// </summary>
public static class AguiServiceCollectionExtensions
{
    /// <summary>AguiHost 独立业务库连接串（缺省回退点）。老 aishop.db 零接触。</summary>
    public const string DefaultDbConnection = "Data Source=agui.db";

    /// <summary>AguiHost 独立 RAG 向量库连接串（缺省回退点）。老 aishop.rag.db 零接触。</summary>
    public const string DefaultRagConnection = "Data Source=agui.rag.db";

    /// <summary>AguiHost 独立会话库连接串（T12，缺省回退点）。老 aishop.db 零接触。</summary>
    public const string DefaultSessionDbConnection = SessionStoreDependencyInjection.DefaultSessionDbConnection;

    /// <summary>AguiHost 独立记忆库路径（T13，缺省回退点；SqliteMemoryStore 按路径建库）。老 aishop.db 零接触。</summary>
    public const string DefaultMemoryDatabasePath = "agui.memory.db";

    /// <summary>
    /// 注册 AguiHost 底座 DI：EF 仓储（独立业务库）+ RAG 语义检索（独立向量库）+ Mem0 记忆（独立记忆库）+
    /// 购物工具工厂/自建模型工厂与 RouterChatClient/全局默认 <see cref="IChatClient"/> + Agent 遥测配置绑定。
    /// 装配契约（spec §4.2/§4.3/§13.2）：<c>AddInfrastructure(dbConnection)</c> / <c>AddRagService(ragConnection)</c> /
    /// <c>AddMemoryService(memoryDatabasePath)</c>，参数缺省时回退 <see cref="DefaultDbConnection"/>/
    /// <see cref="DefaultRagConnection"/>/<see cref="DefaultMemoryDatabasePath"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">应用配置（读 AgentTelemetry 节绑定遥测选项；Models 节 + ActiveModel 供 AguiModelClientFactory 解析）。</param>
    /// <param name="dbConnection">EF 业务库连接串；null 时用 <see cref="DefaultDbConnection"/>。</param>
    /// <param name="ragConnection">RAG 向量库连接串；null 时用 <see cref="DefaultRagConnection"/>。</param>
    /// <param name="memoryDatabasePath">Mem0 记忆库路径（<c>SqliteMemoryStore</c> 按路径建库）；null 时用
    /// <see cref="DefaultMemoryDatabasePath"/>。老 <c>AddMemoryService()</c> 无参默认 aishop.db 不受影响。</param>
    public static IServiceCollection AddAguiBaseServices(
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

        // 推荐工具工厂（agui-client-support T7）：向 AGUIShopping 追加 recommend_products（独立 provider，不改
        // CartToolProvider.CreateTools() 的返回集合）。依赖 = IServiceScopeFactory（scope 解析 Scoped 的
        // RecommendationService/IProductCatalogService）+ ICurrentUserAccessor + IMemoryStore + IMemoryCache——
        // 后两者由本方法内 AddMemoryService 与 AddInfrastructure(AddMemoryCache) 注册（生产恒可解析）；
        // 主构造为纯委托持有、不触碰 DB，故注册为 Singleton 与 CartToolProvider 同款（真正的 DB/记忆读取
        // 发生在工具被调用时，经 scope 解析的 Scoped 服务完成）。
        services.AddSingleton<RecommendationToolProvider>();

        // C5（agui-model-switch，M4 收口装配面）：AguiHost 自建「模型 → 底层客户端」工厂 + 逐轮选模型上下文/
        // 委托客户端，取代老 Service ModelRouter 作为 AguiHost 的模型 seam（老 ModelRouter 零改动、本宿主不再注册）。
        //  - IModelChatClientFactory/AguiModelClientFactory：读 Models 节 + ActiveModel 缺省；每模型底层懒建缓存 +
        //    每客户端 OTel 外包（Router 切到任一模型 gen_ai 都在；全局 IChatClient seam 也经本工厂 GetDefaultClient）。
        //  - IActiveModelProvider/ActiveModelProvider：AsyncLocal 单例「当前请求激活模型」上下文（M3 中间件写入、
        //    值按执行流隔离、不跨请求泄漏，仿 ICurrentUserAccessor）。
        //  - RouterChatClient：agent 专属链底层 delegating IChatClient，按本轮 ActiveModel 委托工厂对应模型底层。
        services.AddSingleton<IModelChatClientFactory, AguiModelClientFactory>();
        services.AddSingleton<IActiveModelProvider, ActiveModelProvider>();
        services.AddSingleton<RouterChatClient>();

        // S1（agui-session-prod）压缩阈值单一来源：注册共享的上下文压缩策略单例——阈值定义唯一收敛于
        // AguiCompaction（对齐老 ShoppingAssistantAgent 128000/16384/0.5/0.8）。AGUIShopping Agent 装配
        // （Program keyed factory 经 GetRequiredService 注入）与 S4 的会话快照收敛（SqliteAgentSessionStore）
        // 共用【同一实例】，从结构上消除「阈值两处各写一遍」的配置漂移（见 AguiCompaction 类注释 / spec R1）。
        services.AddSingleton<ContextWindowCompactionStrategy>(_ => AguiCompaction.CreateStrategy());

        // Agent 遥测：绑定 "AgentTelemetry" 配置节，注册 AgentTelemetryOptions 单例（同 Api/Program.cs L62-66）
        var agentTelemetrySection = config.GetSection("AgentTelemetry");
        services.Configure<AgentTelemetryOptions>(agentTelemetrySection);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value);

        // 全局默认 chatClient（模型 seam）：被 AGUIShopping 专属链之下与记忆服务（IMemoryService 内部
        // GetRequiredService<IChatClient>()，见 Infra MemoryDependencyInjection）共用。
        // M4（C5）起 = AguiModelClientFactory.GetDefaultClient()（ActiveModel 底层；OTel 已内置于工厂每客户端，
        // 此处不再 .UseOpenTelemetry 二次外包）。C3 语义延续——本 seam 纯净、【不含】ReplySanitizingChatClient：
        // 记忆提取 / 冲突消解 / 精排若走「面向用户的商品编号清洗」中间件，会把回复文本中的商品编号剥落污染
        // 落库记忆（Mem0 提取的是模型可见的完整内容）；清洗是展示层规则，只应作用于面向用户的 agent 输出，
        // 故隔离到 AGUIShopping 专属 chatClient 路径（Program keyed factory：ReplySanitizingChatClient(RouterChatClient)）。
        // 记忆 / 内部链路一律走本纯净 seam。
        services.AddSingleton<IChatClient>(sp =>
            sp.GetRequiredService<IModelChatClientFactory>().GetDefaultClient());



        // Mem0 记忆（T13，挂独立记忆库）：老 AddMemoryService() 写死 aishop.db 会触碰老库违反数据隔离（spec 验收 5），
        // 经 T13 Infra 参数化后传入本宿主独立记忆库路径（缺省 agui.memory.db）——memories / memory_history 表由
        // SqliteMemoryStore.InitializeAsync 自建（InitializeAsync 预热，失败仅 Warning）。IMemoryService 单例解析会
        // 加载本地 bge ONNX 模型（Models/bge-small-zh-v1.5），模型缺失时在 Program keyed factory 降级不挂记忆 provider
        // （见 Program.cs ResolveMemoryService 注释），启动不因模型缺失而崩。
        memoryDatabasePath ??= DefaultMemoryDatabasePath;
        services.AddMemoryService(memoryDatabasePath);

        return services;
    }

    // 会话持久化 store 的 DI 装配（AddAguiSessionStore）已下沉 AIShop.Service（SessionStoreDependencyInjection：
    // options 绑定 / store 工厂 / keyed AgentSessionStore / SessionCleanupService hosted service 全套）——
    // 分层搬迁重构：store 与 AGUIShoppingAgent 同层后 keyed 注册可整体迁入，宿主不再需要薄包装。
    // 宿主侧仅保留对 Service 扩展的调用（Program.cs）与 InitializeAsync 的建表预热。

    /// <summary>
    /// 启动引导（Program top-level 与宿主级测试共用）：MigrateAsync（勿 EnsureCreated）→ 幂等播种
    /// marla/steve/fzf003 + ProductSeedData 18 商品 → RAG 向量索引预热（EnsureIndexedAsync，失败仅 Warning）→
    /// Mem0 记忆库建表（SqliteMemoryStore.InitializeAsync，失败仅 Warning）。
    /// 若已注册会话 store（AddAguiSessionStore）则预热其 agent_sessions 表（幂等建表，失败仅 Warning）。
    /// 语义对齐 AIShop.Api/Program.cs 的启动逻辑。
    /// </summary>
    /// <param name="sp">已装配 AddAguiBaseServices 的 ServiceProvider。</param>
    public static async Task InitializeAsync(IServiceProvider sp)
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

        // 幂等播种商品：按 Id **补齐缺失的**种子商品（原实现是「空表才写入」）。
        // 为什么改：种子表一旦扩充，既有库（Products 已非空）就永远拿不到新商品 ——
        // 而「给商品表补货」正是这条路径要支持的。按 Id 补齐既能让既有库自动更新，
        // 又幂等（重复启动不产生重复行），且**不动**库里已有的任何一行（含用户写入的数据）。
        //
        // 数据源是 AguiHost **专用**的 AguiProductSeedData（共享的 ProductSeedData + 本宿主追加的 8 条）——
        // 共享那份维持 18 条不变，因为 Api 也在读它且 Api 属禁改范围。
        var seededIds = await db.Products.Select(product => product.Id).ToListAsync();
        var missingProducts = AguiProductSeedData.Products.Where(product => !seededIds.Contains(product.Id)).ToList();
        if (missingProducts.Count > 0)
        {
            db.Products.AddRange(missingProducts);
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
