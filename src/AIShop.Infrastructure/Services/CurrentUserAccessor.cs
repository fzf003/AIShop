using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 当前登录用户访问器 — 基于 AsyncLocal（ExecutionContext）的实例级实现。
/// 每个执行流（async/await 调用链）持有独立值，供长生命周期 Agent 的工具调用按执行流隔离当前用户
/// （与 HttpContextAccessor 相同的机制）。
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly AsyncLocal<string?> _currentUser = new();

    public void SetCurrentUser(string username) => _currentUser.Value = username;

    public string? CurrentUser => _currentUser.Value;
}
