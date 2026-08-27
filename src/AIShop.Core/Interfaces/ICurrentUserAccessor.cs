namespace AIShop.Core.Interfaces;

/// <summary>
/// 当前登录用户访问器 — 封装执行流级（AsyncLocal）的用户传递。
/// Agent 工具调用无会话参数、跨 async/await 执行，需按执行流传递当前用户；
/// 实现为单例可安全被长生命周期组件（agent/工具提供者）持有。
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>在 Agent 运行前注入当前登录用户名。</summary>
    void SetCurrentUser(string username);

    /// <summary>当前执行流绑定的用户名（未设置返回 null）。</summary>
    string? CurrentUser { get; }
}
