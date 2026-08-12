using System.Globalization;
using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace AIShop.Api.Tests;

/// <summary>
/// T14 PreferenceWriteHostedService 实现测试（design 4.2，对应 spec「偏好权重累加」
/// 「偏好异步写入不阻塞响应」）：worker 侧读-改-写累加、同用户多次入队合并单行、Top-20 截断。
/// </summary>
public sealed class PreferenceWriteHostedServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly PreferenceQueue _queue;
    private PreferenceWriteHostedService? _service;

    public PreferenceWriteHostedServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new AppDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new TestDbContextFactory(options);
        _queue = PreferenceQueue.Create();
    }

    public Task InitializeAsync()
    {
        _service = new PreferenceWriteHostedService(_queue, _dbFactory);
        return _service.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// 预置旧偏好 {"咖啡":2}，入队 [咖啡, 健身]：worker 读旧值逐词 +1，
    /// 最终落库 {"咖啡":3,"健身":1}（咖啡在原有基础 +1，健身新建权重 1）（spec「偏好权重累加」）。
    /// </summary>
    [Fact]
    public async Task ShouldAccumulateWeights_WhenWorkerConsumesUpdate()
    {
        var userId = Guid.NewGuid();
        using (var seedCtx = _dbFactory.CreateDbContext())
        {
            seedCtx.UserPreferences.Add(new UserPreferences { UserId = userId, KeywordsJson = "{\"咖啡\":2}" });
            await seedCtx.SaveChangesAsync();
        }

        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(userId, ["咖啡", "健身"])));

        var weights = await WaitForWeightsAsync(userId, w => w.Count == 2);
        Assert.Equal(3, weights["咖啡"]);
        Assert.Equal(1, weights["健身"]);
    }

    /// <summary>
    /// 同一用户连续入队两次：worker 串行读-改-写，第二次更新既有行而非插入，
    /// 落库仍只有一行，且权重累加（spec「偏好异步写入不阻塞响应」的后台消费）。
    /// </summary>
    [Fact]
    public async Task ShouldMergeMultipleEnqueues_IntoSingleRow_ForSameUser()
    {
        var userId = Guid.NewGuid();

        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(userId, ["咖啡"])));
        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(userId, ["健身"])));

        var weights = await WaitForWeightsAsync(userId, w => w.Count == 2);
        Assert.Equal(1, weights["咖啡"]);
        Assert.Equal(1, weights["健身"]);

        // 两次入队合并：该用户落库仍恰一行（第二次是覆盖更新而非插入新行）
        await using var db = _dbFactory.CreateDbContext();
        var rows = await db.UserPreferences.AsNoTracking().Where(u => u.UserId == userId).ToListAsync();
        Assert.Single(rows);
    }

    /// <summary>
    /// 一次入队 21 个偏好词：worker 逐词 +1 后按 Top-20 截断，落库仅保留 20 个词
    /// （spec「偏好权重累加」的 Top-20 约束）。
    /// </summary>
    [Fact]
    public async Task ShouldTruncateToTop20_WhenMoreThan20PreferenceWords()
    {
        var userId = Guid.NewGuid();
        var words = Enumerable.Range(0, 21)
            .Select(i => "词" + i.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(userId, words)));

        var weights = await WaitForWeightsAsync(userId, w => w.Count == 20);
        Assert.Equal(20, weights.Count);                                          // 21 词只保留 20
        Assert.Equal(20, words.Count(w => weights.ContainsKey(w)));               // 21 个输入词中恰 20 个被保留
        Assert.All(weights, kv => Assert.Equal(1, kv.Value));                     // 保留的每个词权重均为 1（累加都计入过）
    }

    /// <summary>
    /// 轮询读取落库的权重表，直到 matches 谓词成立；超时抛异常（避免测试因异步写时序不稳而 flaky）。
    /// </summary>
    private async Task<Dictionary<string, int>> WaitForWeightsAsync(
        Guid userId, Func<Dictionary<string, int>, bool> matches, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = _dbFactory.CreateDbContext();
            var row = await db.UserPreferences.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (row is not null)
            {
                var weights = JsonSerializer.Deserialize<Dictionary<string, int>>(row.KeywordsJson) ?? [];
                if (matches(weights))
                {
                    return weights;
                }
            }
            await Task.Delay(50);
        }
        throw new XunitException($"Timed out after {timeoutMs} ms waiting for UserPreferences({userId}) to be persisted");
    }

    /// <summary>
    /// 短生命周期上下文工厂：所有上下文共享同一 SqliteConnection（in-memory 库按连接隔离），
    /// 供 hosted service 与轮询读取使用（模拟 IDbContextFactory 短生命周期语义）。
    /// </summary>
    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
