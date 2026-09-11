namespace AIShop.Service.Agui;

/// <summary>
/// 聊天历史维护抽象：周期清理过期轮（轮级 TTL 软删除，design-sql-chat-history-provider §5）。由
/// <see cref="SessionCleanupService"/> 的后台循环周期调用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为何需要该抽象</b>（而非让 <see cref="SessionCleanupService"/> 直接依赖 <see cref="SqlChatHistoryProvider"/>）：
/// MS DI <b>不支持可选构造参数</b>，而聊天历史 provider 仅在配置 <c>Agui:ChatHistoryProvider=Sql</c> 时才注册——
/// 服务若直接依赖具体 provider，则未启用时无法解析 <see cref="SessionCleanupService"/>（破坏零回归）。故引入本抽象并
/// <b>恒注册</b>默认 no-op 实现（<see cref="NoopChatHistoryCleaner"/>），启用时再由
/// <see cref="AguiChatHistoryDependencyInjection.AddAguiChatHistoryProvider"/> 替换为真实实现——
/// 服务依赖永远可解析、永远非 null（方案 a：无 service-locator 味道、易测）。
/// </para>
/// </remarks>
public interface IChatHistoryCleaner
{
    /// <summary>软删除过期轮（整轮软删除，不拆断 FCC↔FRC 配对）。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>软删除的轮数；0 表示无过期轮，或聊天历史未启用（no-op）。</returns>
    Task<int> CleanupExpiredRoundsAsync(CancellationToken cancellationToken = default);
}
