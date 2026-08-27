using AIShop.Core.ValueObjects;

namespace AIShop.Core.Interfaces;

/// <summary>
/// 偏好仓储接口，提供用户偏好的读取与按 UserId 更新或插入（upsert）能力。
/// 偏好由后台 PreferenceWriteHostedService 异步写入，端点只读不阻塞响应。
/// 读写统一以强类型 <see cref="PreferenceProfile"/> 承载，序列化收敛到实现边界。
/// </summary>
public interface IPreferenceRepository
{
    /// <summary>
    /// 按用户 ID 查询偏好；无记录返回 null。
    /// </summary>
    Task<PreferenceProfile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// 按 UserId 更新或插入：已存在则刷新偏好与 UpdatedAt，否则插入新行。
    /// </summary>
    Task UpsertAsync(PreferenceProfile profile, CancellationToken ct = default);
}
