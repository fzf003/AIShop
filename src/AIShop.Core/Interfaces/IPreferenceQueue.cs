namespace AIShop.Core.Interfaces;

/// <summary>
/// 偏好写入队列的轻量消息（design 4.2）：仅携带 UserId 与该轮返回的偏好关键词列表，
/// 权重累加由 PreferenceWriteHostedService 在 worker 侧串行执行，端点不构造完整实体。
/// </summary>
public sealed record UserPreferenceUpdate(Guid UserId, IReadOnlyList<string> Preferences);

/// <summary>
/// 偏好异步写入队列接口：非阻塞入队，失败（队列满）即丢弃，不阻塞聊天响应。
/// </summary>
public interface IPreferenceQueue
{
    /// <summary>
    /// 尝试入队一条偏好更新消息。队列满（容量 64，DropOldest）时返回 false，调用方应记录丢弃。
    /// </summary>
    bool TryEnqueue(UserPreferenceUpdate update);
}
