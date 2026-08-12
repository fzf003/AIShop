using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 偏好异步写入后台服务（design 4.2）：单 worker 串行消费偏好写入队列，
/// 每条消息按「读旧值 → 逐词 +1 → Top-20 截断 → upsert 写库」累加到 UserPreferences 表。
/// 使用 IDbContextFactory 创建短生命周期上下文，规避 Scoped 捕获问题；
/// 累加收敛到单一 worker 侧，天然无并发丢失。端点只入队轻量 UserPreferenceUpdate，不阻塞聊天响应。
/// </summary>
public sealed class PreferenceWriteHostedService : BackgroundService
{
    private readonly PreferenceQueue _queue;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PreferenceWriteHostedService(PreferenceQueue queue, IDbContextFactory<AppDbContext> dbFactory)
    {
        _queue = queue;
        _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var update in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                var current = await db.UserPreferences.FindAsync(update.UserId);
                var isNew = current is null;
                current ??= new UserPreferences { UserId = update.UserId };
                var weights = JsonSerializer.Deserialize<Dictionary<string, int>>(current.KeywordsJson ?? "{}") ?? [];

                foreach (var p in update.Preferences)
                {
                    weights[p] = weights.GetValueOrDefault(p) + 1;
                }

                current.KeywordsJson = JsonSerializer.Serialize(weights.Take(20).ToDictionary(kv => kv.Key, kv => kv.Value));
                current.UpdatedAt = DateTime.UtcNow;

                if (isNew)
                {
                    db.UserPreferences.Add(current);
                }

                await db.SaveChangesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // 主机优雅关闭（SIGTERM / dotnet stop）：stoppingToken 被取消，正常退出 ExecuteAsync，不视为错误
        }
    }
}
