using System.Reflection;
using AIShop.AguiHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S6（agui-session-prod）后台周期清理测试：<see cref="SessionCleanupService"/> 启动即清（spec R5 场景 1，验收 4）、
/// 单次异常不退出循环（R5 场景 2）、<c>stoppingToken</c> 取消干净退出（R5），以及周期 <c>&lt;= 0</c> 回退 12h
/// 不忙循环（R7）。与 <see cref="AguiSessionStoreTests"/> 同串行集合，避免 SQLite 文件并发写入冲突；
/// Dispose 时 <c>SqliteConnection.ClearAllPools</c> + 删临时库。
/// </summary>
[Collection(nameof(AguiSessionStoreTests))]
public sealed class SessionCleanupServiceTests : IDisposable
{
    /// <summary>过期阈值测试用 TTL 天数（与默认 30 一致）。</summary>
    private const int TtlDays = AguiSessionOptions.DefaultSessionTtlDays;

    private readonly List<string> _createdDbPaths = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _createdDbPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 文件仍被其他进程占用时忽略，交由系统清理
            }
        }
    }

    private string NewDbPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_s6_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    // ---------- 启动即清（R5 场景 1 / 验收 4）----------

    [Fact]
    public async Task StartAsync_CleansExpiredRowsImmediately_KeepsFreshRows()
    {
        // R5 场景 1：启动后立即清一次（无需等默认 12h 周期）——超期行消失、新鲜行保留
        var sessionDbPath = NewDbPath("sessions");
        var connectionString = $"Data Source={sessionDbPath}";
        var initStore = new SqliteAgentSessionStore(connectionString);
        await initStore.InitializeAsync();

        var now = DateTimeOffset.Now;
        await InsertRowAsync(connectionString, "CleanAgent:expired-1", now.AddDays(-(TtlDays + 1)));
        await InsertRowAsync(connectionString, "CleanAgent:expired-2", now.AddDays(-(TtlDays * 2)));
        await InsertRowAsync(connectionString, "CleanAgent:fresh-1", now);
        Assert.Equal(3, await CountRowsAsync(connectionString));

        using var sp = BuildProvider(connectionString);
        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            bool cleaned = await WaitUntilAsync(async () => await CountRowsAsync(connectionString) == 1);
            Assert.True(cleaned, "启动后应立即清理过期行（不等首个 12h 周期）");
            Assert.Equal(0, await CountRowAsync(connectionString, "CleanAgent:expired-1"));
            Assert.Equal(1, await CountRowAsync(connectionString, "CleanAgent:fresh-1"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---------- 单次异常不终止 + 干净退出（R5 场景 2 / 干净退出）----------

    [Fact]
    public async Task SingleFailure_DoesNotTerminateService_ThenStopsCleanly()
    {
        // R5 场景 2：store 指向不存在目录 → 清理抛异常 → 后台任务【不退出循环】（仍存活）→ StopAsync 干净退出无残留
        var badPath = Path.Combine(Path.GetTempPath(), $"agui_s6_missing_{Guid.NewGuid():N}", "sessions.db");
        using var sp = BuildProvider($"Data Source={badPath}");
        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());

        await service.StartAsync(CancellationToken.None);
        var executeTask = GetExecuteTask(service);
        try
        {
            // 给首次（必失败的）清理留执行时间；异常应被吞掉、服务继续等待周期
            await Task.Delay(500);
            Assert.False(executeTask.IsCompleted, "单次清理异常后后台任务应仍存活（不退出循环）");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // StopAsync 后后台任务以取消正常结束（未抛出异常、无残留）
        await executeTask;
        Assert.True(executeTask.IsCompletedSuccessfully, "StopAsync 后后台任务应干净退出（无异常）");
    }

    [Fact]
    public async Task StopAsync_CancelsCleanly_NoResidualTask()
    {
        // R5「干净退出」：stoppingToken 生效 → 后台任务以取消正常结束、不抛异常
        var sessionDbPath = NewDbPath("sessions");
        var connectionString = $"Data Source={sessionDbPath}";
        var initStore = new SqliteAgentSessionStore(connectionString);
        await initStore.InitializeAsync();

        using var sp = BuildProvider(connectionString);
        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());

        await service.StartAsync(CancellationToken.None);
        var executeTask = GetExecuteTask(service);

        await service.StopAsync(CancellationToken.None);

        await executeTask;
        Assert.True(executeTask.IsCompletedSuccessfully, "StopAsync 后后台任务应干净退出（无异常、无残留）");
    }

    // ---------- 周期 <=0 回退 12h 不忙循环（R7）----------

    [Fact]
    public async Task CleanupIntervalNonPositive_FallsBackToTwelveHours_NoBusyLoop()
    {
        // R7：SessionCleanupIntervalHours <= 0 时实际周期回退默认 12h（选项类归一），服务不得误判为 0 忙循环。
        // 观测：不可写路径使每次清理都失败并记 Warning——12h 回退下窗口内应【恰好 1 次】清理尝试；
        // 若周期为 0（Task.Delay(0) 立即返回）则忙循环产生大量重试、告警数暴增。
        var badPath = Path.Combine(Path.GetTempPath(), $"agui_s6_missing_{Guid.NewGuid():N}", "sessions.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agui:SessionCleanupIntervalHours"] = "0",
            })
            .Build();
        var logger = new CountingLogger();

        using var sp = BuildProvider($"Data Source={badPath}", config, logger);
        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            bool firstAttempt = await WaitUntilAsync(() => Task.FromResult(logger.Warnings >= 1), TimeSpan.FromSeconds(5));
            Assert.True(firstAttempt, "启动后应立即执行首次清理（并因不可写路径失败告警）");

            await Task.Delay(400);
            Assert.Equal(1, logger.Warnings);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---------- helpers ----------

    /// <summary>经 <see cref="AguiServiceCollectionExtensions.AddAguiSessionStore"/> 装配（含 hosted service 注册）。</summary>
    private static ServiceProvider BuildProvider(
        string sessionConnectionString,
        IConfiguration? config = null,
        ILogger<SessionCleanupService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (logger is not null)
            services.AddSingleton(logger);
        services.AddAguiSessionStore(config, sessionConnectionString);
        return services.BuildServiceProvider();
    }

    /// <summary>读 <see cref="BackgroundService"/> 的 <c>ExecuteTask</c>（断言后台循环是否存活/干净结束）。</summary>
    private static Task GetExecuteTask(BackgroundService service)
        => (Task)typeof(BackgroundService)
            .GetProperty("ExecuteTask", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

    /// <summary>轮询条件直至为真或超时；返回最终判定。</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(50);
        }

        return await condition();
    }

    /// <summary>直接写入一行（session_json 占位 "{}"；仅用于清理/计数类断言）。</summary>
    private static async Task InsertRowAsync(string connectionString, string storeId, DateTimeOffset updatedAt)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_sessions (store_id, session_json, updated_at) VALUES ($id, '{}', $ts)";
        command.Parameters.AddWithValue("$id", storeId);
        command.Parameters.AddWithValue("$ts", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> CountRowAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>计数 Warning 及以上日志（观测清理尝试次数；用于忙循环反证）。</summary>
    private sealed class CountingLogger : ILogger<SessionCleanupService>
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }
}
