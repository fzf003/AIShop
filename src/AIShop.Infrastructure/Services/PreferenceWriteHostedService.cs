using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<PreferenceWriteHostedService> _logger;

    public PreferenceWriteHostedService(
        PreferenceQueue queue,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<PreferenceWriteHostedService> logger)
    {
        _queue = queue;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var update in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
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

                    // Top-20 截断（spec「偏好权重累加」）：按权重降序保留前 20，并列权重按 Key 序数序二次排序，
                    // 保证 Top-20 结果确定可复现。不再依赖 Dictionary 枚举顺序（旧实现 Take(20) 保留的是最早见过的 20 个词）。
                    current.KeywordsJson = JsonSerializer.Serialize(
                        weights.OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                            .Take(20)
                            .ToDictionary(kv => kv.Key, kv => kv.Value));
                    current.UpdatedAt = DateTime.UtcNow;

                    if (isNew)
                    {
                        db.UserPreferences.Add(current);
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单条消息处理失败（如 KeywordsJson 被手工改坏 → JsonException、SQLite 写锁超时 → SqliteException）
                    // 不让 worker 永久死亡：记录 Warning 后跳过该消息，继续消费后续消息（否则 BackgroundService 不自动重启，
                    // 此后所有偏好写入会静默永久失效）。OperationCanceledException 由外层捕获，保持正常关闭语义。
                    _logger.LogWarning(ex, "Failed to process preference update for {UserId}, skipped", update.UserId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // 主机优雅关闭（SIGTERM / dotnet stop）：stoppingToken 被取消，正常退出 ExecuteAsync，不视为错误
        }
    }
}
