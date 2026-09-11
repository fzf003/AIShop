using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIShop.Service.Agui;

/// <summary>
/// <see cref="SqlChatHistoryProvider"/> 的 DI 装配（design-sql-chat-history-provider §7 迁移路径第 1 条）。
/// 按配置 <c>Agui:ChatHistoryProvider</c> 选择聊天历史 provider：值为 <c>Sql</c>（不区分大小写）时注册持久化
/// <see cref="SqlChatHistoryProvider"/>；其他 / 未配置时不注册，<c>AGUIShoppingAgent</c> 维持 MAF 默认
/// <c>InMemoryChatHistoryProvider</c>（既有行为零变化）。
/// </summary>
/// <remarks>
/// 注册为单例：provider 本身无每会话状态（会话标识存 <see cref="AgentSession.StateBag"/>），可安全跨会话共享
/// （同官方 <c>ValkeyChatHistoryProvider</c> 用法）。连接串默认 <see cref="SqlChatHistoryOptions.DefaultConnectionString"/>
/// （独立聊天历史库，不得为老 aishop.db）；<c>Agui:ChatConnection</c> 为其可选覆盖键（仿 T16 会话库 <c>Agui:SessionConnection</c> seam）。
/// </remarks>
public static class AguiChatHistoryDependencyInjection
{
    /// <summary>provider 选择配置键（<c>Agui:ChatHistoryProvider</c>）。</summary>
    public const string ProviderConfigKey = "Agui:ChatHistoryProvider";

    /// <summary>选择持久化 <see cref="SqlChatHistoryProvider"/> 的配置值。</summary>
    public const string SqlProviderValue = "Sql";

    /// <summary>
    /// 按配置注册聊天历史 provider。未配置 / 非 <c>Sql</c> 时不注册（保持 MAF 默认 InMemory 行为）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">应用配置（读 <c>Agui:ChatHistoryProvider</c> 选择键）。null 时不注册。</param>
    /// <param name="chatDbConnection">聊天历史库连接串；null 时用 <see cref="SqlChatHistoryOptions.DefaultConnectionString"/>。</param>
    public static IServiceCollection AddAguiChatHistoryProvider(
        this IServiceCollection services,
        IConfiguration? config = null,
        string? chatDbConnection = null)
    {
        // 未配置 / 非 Sql → 不注册（AGUIShoppingAgent.Create 收 null → ChatClientAgent 用默认 InMemoryChatHistoryProvider）。
        if (!string.Equals(config?[ProviderConfigKey], SqlProviderValue, StringComparison.OrdinalIgnoreCase))
            return services;

        services.AddSingleton(new SqlChatHistoryOptions
        {
            ConnectionString = chatDbConnection ?? SqlChatHistoryOptions.DefaultConnectionString
        });
        services.AddSingleton<SqlChatHistoryProvider>();
        // 对外以抽象 ChatHistoryProvider 暴露，供 host 经 GetService<ChatHistoryProvider>() 注入 AGUIShoppingAgent.Create。
        services.AddSingleton<ChatHistoryProvider>(sp => sp.GetRequiredService<SqlChatHistoryProvider>());
        // 聊天历史维护：把默认 no-op（AddAguiSessionStore 中 TryAdd）替换为真实实现（provider 同一单例），
        // 使 SessionCleanupService 的周期清理在启用 Sql 聊天历史时真正执行轮级 TTL 软删除（design §5 接线）。
        services.Replace(ServiceDescriptor.Singleton<IChatHistoryCleaner>(
            sp => sp.GetRequiredService<SqlChatHistoryProvider>()));
        return services;
    }
}
