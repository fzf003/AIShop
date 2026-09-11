namespace AIShop.Service.Agui;

/// <summary>
/// <see cref="IChatHistoryCleaner"/> 的默认 no-op 实现：未启用 Sql 聊天历史（<c>Agui:ChatHistoryProvider != "Sql"</c>）时
/// 恒注册，供 <see cref="SessionCleanupService"/> 解析——调用恒返回 0、无任何副作用（不建库、不写库、不打日志），
/// 从而未启用时清理服务行为与接线前完全一致。
/// </summary>
internal sealed class NoopChatHistoryCleaner : IChatHistoryCleaner
{
    /// <inheritdoc />
    public Task<int> CleanupExpiredRoundsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
