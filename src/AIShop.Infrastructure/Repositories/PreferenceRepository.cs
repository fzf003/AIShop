using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.ValueObjects;
using AIShop.Infrastructure.Data;

namespace AIShop.Infrastructure.Repositories;

/// <summary>
/// 偏好仓储实现：在 UserPreferences 实体（KeywordsJson 字符串）与 PreferenceProfile（强类型）边界转换。
/// 偏好由后台 PreferenceWriteHostedService 异步写入，端点只读，不阻塞聊天响应。
/// </summary>
public sealed class PreferenceRepository(AppDbContext db) : IPreferenceRepository
{
    /// <summary>
    /// 按用户 ID 查询偏好；无记录返回 null。非法 KeywordsJson 容错为空集。
    /// </summary>
    public async Task<PreferenceProfile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await db.UserPreferences.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (row is null) return null;

        try
        {
            return PreferenceProfile.FromKeywordsJson(row.UserId, row.KeywordsJson, row.UpdatedAt);
        }
        catch (JsonException)
        {
            // 存量数据被手工改坏时降级为空偏好，不崩溃（读取侧容错）
            return new PreferenceProfile(row.UserId, new Dictionary<string, int>(), row.UpdatedAt);
        }
    }

    /// <summary>
    /// 按 UserId 更新或插入：已存在则覆盖偏好与 UpdatedAt，否则插入新行。
    /// </summary>
    public async Task UpsertAsync(PreferenceProfile profile, CancellationToken ct = default)
    {
        var existing = await db.UserPreferences.FirstOrDefaultAsync(u => u.UserId == profile.UserId, ct);
        var keywordsJson = profile.ToKeywordsJson();

        if (existing is null)
        {
            db.UserPreferences.Add(new UserPreferences
            {
                UserId = profile.UserId,
                KeywordsJson = keywordsJson,
                UpdatedAt = profile.UpdatedAt,
            });
        }
        else
        {
            existing.KeywordsJson = keywordsJson;
            existing.UpdatedAt = profile.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
