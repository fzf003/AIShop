#pragma warning disable MAAI001 // 解析 SessionCleanupService 会间接解析 ContextWindowCompactionStrategy（MAF [Experimental]）
using System.Reflection;
using AIShop.Service.Agui;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIShop.Service.Tests;

/// <summary>
/// 聊天历史轮级 TTL 清理接入 <see cref="SessionCleanupService"/> 的测试（design-sql-chat-history-provider §5 生产接线）：
/// 启用 Sql provider 时启动即清软删除过期轮（保留未过期轮）；未启用时 cleaner 为 no-op、不触碰 chat 库；
/// 聊天历史清理失败不终止服务、StopAsync 干净退出。
/// </summary>
public sealed class SessionCleanupChatHistoryTests : IDisposable
{
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
                // 文件仍被占用时忽略，交由系统清理（对齐既有测试约定）
            }
        }
    }

    // ---------- 启用 Sql provider：启动即清（整轮软删除） ----------

    [Fact]
    public async Task SqlProviderEnabled_StartupClean_SoftDeletesExpiredRounds_KeepsFresh()
    {
        var sessionConn = NewConnection("sessions");
        var chatConn = NewConnection("chat");
        // 过期阈值用默认 TtlDays=30：写入 100 天前的轮即过期
        var now = DateTimeOffset.Now;

        using var sp = BuildProvider(sessionConn, chatConn, SqlEnabledConfig());
        var provider = sp.GetRequiredService<SqlChatHistoryProvider>();
        await provider.InitializeAsync();

        await InsertChatRowAsync(chatConn, "thread-expired", sequence: 1, roundId: 1, createdAt: now.AddDays(-100), isDeleted: false);
        await InsertChatRowAsync(chatConn, "thread-expired", sequence: 2, roundId: 1, createdAt: now.AddDays(-100), isDeleted: false);
        await InsertChatRowAsync(chatConn, "thread-fresh", sequence: 1, roundId: 1, createdAt: now, isDeleted: false);
        await InsertChatRowAsync(chatConn, "thread-fresh", sequence: 2, roundId: 1, createdAt: now, isDeleted: false);

        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            bool cleaned = await WaitUntilAsync(async () => await CountChatRowsAsync(chatConn, "thread-expired", isDeleted: false) == 0);
            Assert.True(cleaned, "启动后应立即软删除过期轮（不等首个清理周期）");

            // 过期轮整轮两行均软删除；未过期轮原样保留、未被误删
            Assert.Equal(2, await CountChatRowsAsync(chatConn, "thread-expired", isDeleted: true));
            Assert.Equal(0, await CountChatRowsAsync(chatConn, "thread-expired", isDeleted: false));
            Assert.Equal(2, await CountChatRowsAsync(chatConn, "thread-fresh", isDeleted: false));
            Assert.Equal(0, await CountChatRowsAsync(chatConn, "thread-fresh", isDeleted: true));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---------- 未启用：no-op，行为与接线前一致 ----------

    [Fact]
    public async Task SqlProviderDisabled_CleanerIsNoop_ChatDbUntouched_ServiceStopsCleanly()
    {
        var sessionConn = NewConnection("sessions");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var chatDbPath = Path.Combine(Directory.GetCurrentDirectory(), "agui.chat.db");
        var existedBefore = File.Exists(chatDbPath);

        using var sp = BuildProvider(sessionConn, null, config);

        // 未启用 Sql provider → 解析到 no-op 实现（恒可解析、无副作用）
        var cleaner = sp.GetRequiredService<IChatHistoryCleaner>();
        Assert.IsType<NoopChatHistoryCleaner>(cleaner);
        Assert.Equal(0, await cleaner.CleanupExpiredRoundsAsync(CancellationToken.None));

        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());
        await service.StartAsync(CancellationToken.None);
        var executeTask = GetExecuteTask(service);
        try
        {
            await Task.Delay(300);
            Assert.False(executeTask.IsCompleted, "未启用时清理服务仍应正常存活轮转");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(executeTask.IsCompleted);
        Assert.False(executeTask.IsFaulted, $"后台任务不应以异常结束（干净退出）：{executeTask.Exception}");

        // 缺省 chat 库（agui.chat.db）未被创建 / 未被触碰
        Assert.Equal(existedBefore, File.Exists(chatDbPath));
    }

    // ---------- 聊天历史清理失败：不终止服务 + 干净退出 ----------

    [Fact]
    public async Task ChatHistoryCleanupFailure_DoesNotTerminateService_ThenStopsCleanly()
    {
        var sessionConn = NewConnection("sessions");
        // chat 库指向不存在目录 → 每次聊天历史清理必抛异常
        var badChatConn = $"Data Source={Path.Combine(Path.GetTempPath(), $"agui_chat_missing_{Guid.NewGuid():N}", "chat.db")}";

        using var sp = BuildProvider(sessionConn, badChatConn, SqlEnabledConfig());
        var service = Assert.IsAssignableFrom<SessionCleanupService>(sp.GetRequiredService<IHostedService>());

        await service.StartAsync(CancellationToken.None);
        var executeTask = GetExecuteTask(service);
        try
        {
            // 给首次（必失败的）聊天历史清理留执行时间；异常应被独立捕获、服务继续等待周期
            await Task.Delay(500);
            Assert.False(executeTask.IsCompleted, "聊天历史清理异常后服务应仍存活（不退出循环）");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(executeTask.IsCompleted, "StopAsync 后后台任务应已结束（无残留 Task）");
        Assert.False(executeTask.IsFaulted, $"后台任务不应以异常结束（干净退出）：{executeTask.Exception}");
    }

    // ---------- helpers ----------

    private static IConfiguration SqlEnabledConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AguiChatHistoryDependencyInjection.ProviderConfigKey] = AguiChatHistoryDependencyInjection.SqlProviderValue,
            })
            .Build();

    private static ServiceProvider BuildProvider(string sessionConnectionString, string? chatDbConnection, IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAguiSessionStore(config, sessionConnectionString);
        if (chatDbConnection is not null)
            services.AddAguiChatHistoryProvider(config, chatDbConnection);
        return services.BuildServiceProvider();
    }

    private string NewConnection(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_chatclean_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return $"Data Source={path}";
    }

    /// <summary>读 <see cref="BackgroundService"/> 的 <c>ExecuteTask</c>（断言后台循环存活 / 干净结束）。</summary>
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

    private static async Task InsertChatRowAsync(
        string connectionString, string conversationId, int sequence, int roundId, DateTimeOffset createdAt, bool isDeleted)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_messages (conversation_id, sequence, role, message_json, round_id, is_deleted, deleted_at, created_at)
            VALUES ($c, $seq, 'user', '{}', $round, $deleted, NULL, $created)
            """;
        command.Parameters.AddWithValue("$c", conversationId);
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$round", roundId);
        command.Parameters.AddWithValue("$deleted", isDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountChatRowsAsync(string connectionString, string conversationId, bool isDeleted)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chat_messages WHERE conversation_id = $c AND is_deleted = $d";
        command.Parameters.AddWithValue("$c", conversationId);
        command.Parameters.AddWithValue("$d", isDeleted ? 1 : 0);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
