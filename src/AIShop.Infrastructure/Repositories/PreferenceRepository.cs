using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;

namespace AIShop.Infrastructure.Repositories;

/// <summary>
/// 偏好仓储实现（design 4.2）：按 UserId 读取用户偏好；Upsert 按 UserId 更新或插入。
/// 偏好由后台 PreferenceWriteHostedService 异步写入，端点只读，不阻塞聊天响应。
/// </summary>
public sealed class PreferenceRepository(AppDbContext db) : IPreferenceRepository
{
    /// <summary>
    /// 按用户 ID 查询偏好；无记录返回 null。
    /// </summary>
    public async Task<UserPreferences?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
        => await db.UserPreferences.FirstOrDefaultAsync(u => u.UserId == userId, ct);

    /// <summary>
    /// 按 UserId 更新或插入：已存在则覆盖 KeywordsJson 与 UpdatedAt，否则插入新行。
    /// </summary>
    public async Task UpsertAsync(UserPreferences preferences, CancellationToken ct = default)
    {
        var existing = await db.UserPreferences.FirstOrDefaultAsync(u => u.UserId == preferences.UserId, ct);

        if (existing is null)
        {
            db.UserPreferences.Add(preferences);
        }
        else
        {
            existing.KeywordsJson = preferences.KeywordsJson;
            existing.UpdatedAt = preferences.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
