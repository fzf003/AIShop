using AIShop.Infrastructure;
using AIShop.Infrastructure.MemoryService;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Service.Tests;

/// <summary>
/// AddMemoryService 可选 databasePath 参数化（agui-host T13）：
/// 传入独立库路径 → memories / memory_history 表在指定路径生成（AguiHost 独立 agui.memory.db 前置）；默认常量保留；
/// 老 AddMemoryService() 无参调用仍指向默认 aishop.db（回归护航）。
/// 只解析 <see cref="SqliteMemoryStore"/>（不解析 IMemoryService——后者会加载本地 bge ONNX 模型），验证路径参数生效。
/// </summary>
public sealed class AddMemoryServiceParameterizationTests
{
    [Fact]
    public async Task AddMemoryService_WithCustomDatabasePath_CreatesMemoriesTablesAtGivenPath()
    {
        // 传入独立库路径 → SqliteMemoryStore 在 temp 路径建 memories/memory_history 表；
        // 若参数被忽略回退默认 aishop.db，temp 路径不会出现文件
        var dbPath = Path.Combine(Path.GetTempPath(), $"memparam_{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryService(dbPath);
            // SqliteMemoryStore 实现 IAsyncDisposable，容器须 await using 释放（同步 Dispose 会抛 InvalidOperationException）
            await using var sp = services.BuildServiceProvider();

            var store = sp.GetRequiredService<SqliteMemoryStore>();
            await store.InitializeAsync();

            Assert.True(File.Exists(dbPath), $"传入路径应使记忆库在 {dbPath} 生成");

            // 断言 memories / memory_history 两表均在指定库创建（Mem0 记忆存储契约表）
            var tableCount = await CountTablesAsync(dbPath, ["memories", "memory_history"]);
            Assert.Equal(2, tableCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch (IOException) { /* 连接未完全释放时忽略，系统清理 */ }
        }
    }

    [Fact]
    public void DefaultMemoryDatabasePath_RemainsAishopDb()
    {
        // 默认常量保留且不变：缺省路径回退点 = aishop.db（spec「AddMemoryService 默认 aishop.db 不变」）
        Assert.Equal("aishop.db", MemoryDependencyInjection.DefaultMemoryDatabasePath);
    }

    [Fact]
    public async Task AddMemoryService_WithoutDatabasePath_UsesDefaultStore()
    {
        // 回归护航：老 AddMemoryService() 无参调用行为零变化——记忆库仍指向默认路径（cwd 下 aishop.db），
        // 与老宿主（AIShop.Api Program）使用方式一致，建表链路不抛异常
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "aishop.db");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryService();
            await using var sp = services.BuildServiceProvider();

            var store = sp.GetRequiredService<SqliteMemoryStore>();
            await store.InitializeAsync();

            Assert.True(File.Exists(dbPath), "无参 AddMemoryService() 应沿用默认路径在 cwd 生成 aishop.db");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch (IOException) { /* 连接未完全释放时忽略，系统清理 */ }
        }
    }

    /// <summary>统计指定 SQLite 文件中匹配 <paramref name="tableNames"/> 的表数量。</summary>
    private static async Task<int> CountTablesAsync(string dbPath, string[] tableNames)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ($t1, $t2)";
        command.Parameters.AddWithValue("$t1", tableNames[0]);
        command.Parameters.AddWithValue("$t2", tableNames[1]);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
