#pragma warning disable MAAI001 // ContextWindowCompactionStrategy 为 MAF [Experimental]（上下文压缩 API）
using AIShop.Core.Interfaces;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AIShop.Service.Agui;

/// <summary>
/// AG-UI 会话持久化 store 的 DI 装配（分层搬迁重构：自 AguiHost 迁入 AIShop.Service，与 <see cref="SqliteAgentSessionStore"/>
/// 同层）。以 <c>SqliteAgentSessionStore</c> 具体单例 + 按 <see cref="AGUIShoppingAgent.AgentName"/> keyed 的
/// <see cref="AgentSessionStore"/> 注册——使 <c>MapAGUIServer(agentName, pattern)</c> 命中 keyed store
/// （镜像 <c>AGUIEndpointRouteBuilderExtensions.cs</c> <c>GetKeyedService&lt;AgentSessionStore&gt;(aiAgent.Name)</c>），
/// 取代默认 ephemeral 的 Noop 存储：SSE 流结束 <c>SaveSessionAsync</c> 落库、同 ThreadId 下次 <c>GetSessionAsync</c>
/// 还原（重启不丢上下文）。数据隔离：独立会话库（缺省 <see cref="DefaultSessionDbConnection"/>，不得为老 aishop.db）。
/// </summary>
/// <remarks>
/// <para>
/// AguiHost 迁入说明（分层搬迁重构）：keyed 注册依赖 <see cref="AGUIShoppingAgent"/>（现亦在本层），故可整体迁入 Service，
/// 宿主不再需要薄包装。
/// </para>
/// <para>
/// S6（agui-session-prod）：一并注册 <see cref="SessionCleanupService"/> 后台周期清理（<c>AddHostedService</c>）——
/// 与 store/options 同生，宿主 <c>StartAsync</c> 时启动清理循环（启动即清一次 → 默认每 12h 再清）。
/// 裸 <c>ServiceCollection</c> 未启动 host 时仅完成注册、无副作用（不触发任何 DB 访问）。
/// </para>
/// </remarks>
public static class SessionStoreDependencyInjection
{
    /// <summary>AguiHost 独立会话库连接串（缺省回退点）。老 aishop.db 零接触。</summary>
    public const string DefaultSessionDbConnection = "Data Source=agui.sessions.db";

    /// <summary>
    /// 注册 AGUIShopping 的持久化会话 store（T12：会话历史持久化）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">应用配置；非 null 时绑定 "Agui" 节的 <see cref="AguiSessionOptions"/>（S3）。多余键被
    /// binder 忽略，<c>Agui:DbConnection</c> / <c>Agui:SessionConnection</c> 等不受影响；null 时不绑定，store 用类默认。</param>
    /// <param name="sessionDbConnection">会话库连接串；null 时用 <see cref="DefaultSessionDbConnection"/>。</param>
    public static IServiceCollection AddAguiSessionStore(
        this IServiceCollection services,
        IConfiguration? config = null,
        string? sessionDbConnection = null)
    {
        // S3（agui-session-prod）：绑定 "Agui" 节的会话配置（SessionTtlDays / SessionCleanupIntervalHours /
        // SessionMaxRounds）。config 为 null（纯底座测试 / 未接线宿主）时跳过绑定，IOptions 仍解析为类默认 30/12/12。
        if (config is not null)
            services.Configure<AguiSessionOptions>(config.GetSection("Agui"));
        else
            services.AddOptions<AguiSessionOptions>(); // 无配置也注册选项基础设施，保证工厂所需 IOptions<AguiSessionOptions> 可解析（回退类默认）

        sessionDbConnection ??= DefaultSessionDbConnection;
        // S4（agui-session-prod）：确保压缩策略可解析——AddAguiBaseServices（S1）已注册时为 no-op，store 经
        // GetRequiredService 取到与 AGUIShopping Agent 侧【同一单例】（spec R1「复用装配同一实例」）；仅经本扩展
        // 装配的裸容器（如 AguiSessionOptionsTests 的选项绑定场景）补注册，使工厂恒可解析。
        services.TryAddSingleton<ContextWindowCompactionStrategy>(_ => AguiCompaction.CreateStrategy());
        // S3：store 注册为工厂 lambda，经 IOptions 取绑定后的 AguiSessionOptions 注入构造
        // （null 配置 → 类默认；选项绑定发生在容器构建后，工厂解析期读取正是绑定完成时点）。
        // S4：第三参注入共享压缩策略单例，供 SaveSessionAsync 落库前收敛快照使用。
        // 会话归属：第四参注入 ICurrentUserAccessor（store_id = "{agent.Name}:{用户名}"）。用 GetService（非
        // GetRequiredService）——裸容器/纯底座测试未注册它时解析为 null，store 回退 threadId 归属而非炸容器。
        services.AddSingleton(sp => new SqliteAgentSessionStore(
            sessionDbConnection,
            sp.GetRequiredService<IOptions<AguiSessionOptions>>().Value,
            sp.GetRequiredService<ContextWindowCompactionStrategy>(),
            sp.GetService<ICurrentUserAccessor>()));
        // keyed AgentSessionStore 解析具体单例：MapAGUIServer 按 agent.Name 解析命中（keyed 注册是命中前提）
        services.AddKeyedSingleton<AgentSessionStore>(AGUIShoppingAgent.AgentName,
            static (sp, _) => sp.GetRequiredService<SqliteAgentSessionStore>());
        // S6（agui-session-prod）：后台周期清理 hosted service（与 store 同生）——宿主启动即清一次过期会话行，
        // 之后按 AguiSessionOptions.EffectiveCleanupInterval（默认 12h）周期再清（spec R5）。
        // 裸 ServiceCollection 未启动 host 时仅注册、无副作用；依赖 SqliteAgentSessionStore/IOptions/ILogger 均可解析。
        // 聊天历史轮级清理依赖抽象 IChatHistoryCleaner：此处 TryAdd 默认 no-op（未启用 Sql 聊天历史时无副作用，
        // 保证服务恒可解析）；启用时 AddAguiChatHistoryProvider 会 Replace 为真实实现（见该扩展）。
        services.TryAddSingleton<IChatHistoryCleaner, NoopChatHistoryCleaner>();
        services.AddHostedService<SessionCleanupService>();
        return services;
    }
}
