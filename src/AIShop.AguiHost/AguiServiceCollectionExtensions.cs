using AIShop.AgentTelemetry;
using AIShop.AguiHost.Agents;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Data;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Serilog;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 底座 DI 装配与启动引导（agui-host T3）。
/// 底座注册与 AIShop.Api/Program.cs 同源（AddInfrastructure / AddRagService / ModelRouter / AgentTelemetry 绑定），
/// 差异仅在：① 业务库/向量库换为 AguiHost 独立连接串（<c>agui.db</c>/<c>agui.rag.db</c>，数据隔离 spec 验收 5）；
/// ② 不挂 <c>AddMemoryService()</c>（其 SqliteMemoryStore 写死 aishop.db，见 <see cref="AddAguiBaseServices"/> 内注释）。
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

    /// <summary>
    /// 注册 AguiHost 底座 DI：EF 仓储（独立业务库）+ RAG 语义检索（独立向量库）+ 购物工具工厂/模型路由/全局默认
    /// <see cref="IChatClient"/> + Agent 遥测配置绑定。装配契约（spec §4.2/§4.3）：
    /// <c>AddInfrastructure(dbConnection)</c> / <c>AddRagService(ragConnection)</c>，参数缺省时回退
    /// <see cref="DefaultDbConnection"/>/<see cref="DefaultRagConnection"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">应用配置（读 AgentTelemetry 节绑定遥测选项；Models 节供 ModelRouter 延迟解析）。</param>
    /// <param name="dbConnection">EF 业务库连接串；null 时用 <see cref="DefaultDbConnection"/>。</param>
    /// <param name="ragConnection">RAG 向量库连接串；null 时用 <see cref="DefaultRagConnection"/>。</param>
    internal static IServiceCollection AddAguiBaseServices(
        this IServiceCollection services,
        IConfiguration config,
        string? dbConnection = null,
        string? ragConnection = null)
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


        // Agent 遥测：绑定 "AgentTelemetry" 配置节，注册 AgentTelemetryOptions 单例（同 Api/Program.cs L62-66）
        var agentTelemetrySection = config.GetSection("AgentTelemetry");
        services.Configure<AgentTelemetryOptions>(agentTelemetrySection);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value);

        // 全局默认 chatClient（与 Api/Program.cs 同一创建逻辑；AGUIShoppingAgent 与记忆链路共用）。
        // T11（服务端回复清洗兜底）：外包一层 ReplySanitizingChatClient 中间件——agent 输出文本（含流式增量）
        // 离开 AguiHost 前经 Core ReplySanitizer 清洗商品编号（#5 / 商品Id:4 / 商品ID为4 等），与老 Agent
        // SanitizeReply 同语义。只作用于本宿主 chatClient 单例（AGUIShopping 装配 + 请求复用），
        // 不影响老 Api/Service 经各自 ModelRouter 构建的实例。
        services.AddSingleton<IChatClient>(sp =>
            new ReplySanitizingChatClient(sp.GetRequiredService<ModelRouter>().GetDefaultChatClient())
            .AsBuilder().UseOpenTelemetry(sourceName: sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value.SourceName).Build());



        // 本宿主 MVP 不挂 AddMemoryService()：其 SqliteMemoryStore 写死 aishop.db，挂载会触碰老库违反数据隔离
        // （spec 验收 5），且「AGUI 专属用户画像记忆」属 spec 非目标；如需会话/画像记忆属后续路线（复用 Mem0）。

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
    /// marla/steve/fzf003 + ProductSeedData 18 商品 → RAG 向量索引预热（EnsureIndexedAsync，失败仅 Warning）。
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
