using System.Globalization;
using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Sdk;

namespace AIShop.Api.Tests;

/// <summary>
/// T14 测试集合定义：本类用 in-memory SQLite 共享连接直测 hosted service（worker 写 + 轮询读同一连接），
/// 置于 DisableParallelization 串行集合，避免全量并行时与其它测试类的连接/宿主竞争触发
/// SqliteConnection 清理竞态（DisposeAsync 中 Close 抛 NRE）。
/// </summary>
[CollectionDefinition(nameof(PreferenceWriteHostedServiceTests), DisableParallelization = true)]
public sealed class PreferenceWriteHostedServiceTestsCollection;

/// <summary>
/// T14 PreferenceWriteHostedService 实现测试（design 4.2，对应 spec「偏好权重累加」
/// 「偏好异步写入不阻塞响应」）：worker 侧读-改-写累加、同用户多次入队合并单行、Top-20 截断、
/// 单条异常容错。
/// </summary>
[Collection(nameof(PreferenceWriteHostedServiceTests))]
public sealed class PreferenceWriteHostedServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly PreferenceQueue _queue;
    private readonly ILogger<PreferenceWriteHostedService> _logger;
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
        _logger = Substitute.For<ILogger<PreferenceWriteHostedService>>();
    }

    public Task InitializeAsync()
    {
        _service = new PreferenceWriteHostedService(_queue, _dbFactory, _logger);
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
    /// 预置 21 个词（词0..词19 权重 1，外加权重 5 的「词X」且其字典插入顺序最后），
    /// 再一次性入队 21 个词（词0..词20）：worker 逐词 +1 后按 Top-20 截断。
    /// 共 22 个词只保留 20 个，且保留的是**权重最高**的词（词X）而非字典枚举顺序前 20 个
    /// （spec「偏好权重累加」的 Top-20 约束）。
    /// 旧实现 `weights.Take(20)` 按 Dictionary 枚举顺序会丢弃插入最晚的「词X」（第 21 位）；
    /// 新实现按权重降序必保留它，以此断言区分。
    /// </summary>
    [Fact]
    public async Task ShouldTruncateToTop20_WhenMoreThan20PreferenceWords()
    {
        var userId = Guid.NewGuid();

        // 预置 21 个词：词0..词19（权重 1）+ 词X（权重 5，位于 JSON 最后 = 反序列化插入顺序最后）
        var pre = new Dictionary<string, int>();
        for (var i = 0; i < 20; i++)
        {
            pre["词" + i] = 1;
        }
        pre["词X"] = 5;

        using (var seedCtx = _dbFactory.CreateDbContext())
        {
            seedCtx.UserPreferences.Add(new UserPreferences { UserId = userId, KeywordsJson = JsonSerializer.Serialize(pre) });
            await seedCtx.SaveChangesAsync();
        }

        var words = Enumerable.Range(0, 21)
            .Select(i => "词" + i.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(userId, words)));

        var weights = await WaitForWeightsAsync(userId, w => w.Count == 20);
        Assert.Equal(20, weights.Count);                            // 22 词只保留 20
        Assert.True(weights.ContainsKey("词X"));                    // 权重最高词被保留（旧枚举顺序实现会丢弃它）
        Assert.Equal(5, weights["词X"]);                            // 词X 权重保持预置值（21 个入队词不含词X）
        Assert.Equal(19, weights.Count(kv => kv.Value == 2));       // 其余 19 个为权重 2 的词（预置 1 + 入队 +1）
    }

    /// <summary>
    /// 单条消息处理抛异常（如 KeywordsJson 被手工改坏 → JsonException）时 worker 不整体退出：
    /// 记录 Warning 后跳过该消息，继续消费后续消息（spec「偏好异步写入不阻塞响应」的后台消费容错）。
    /// </summary>
    [Fact]
    public async Task ShouldContinue_WhenSingleMessageProcessingThrows()
    {
        var brokenUserId = Guid.NewGuid();
        var healthyUserId = Guid.NewGuid();

        // 预置一条坏 JSON 的偏好记录，worker 处理该用户消息时反序列化会抛 JsonException
        using (var seedCtx = _dbFactory.CreateDbContext())
        {
            seedCtx.UserPreferences.Add(new UserPreferences { UserId = brokenUserId, KeywordsJson = "not-valid-json" });
            await seedCtx.SaveChangesAsync();
        }

        // 首条消息命中坏 JSON，将被 worker 跳过
        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(brokenUserId, ["咖啡"])));

        // 稍等，让 worker 先消费（并失败）坏消息
        await Task.Delay(200);

        // 再入队正常用户的消息：若 worker 未因上一条异常永久死亡，应能正常落库
        Assert.True(_queue.TryEnqueue(new UserPreferenceUpdate(healthyUserId, ["健身"])));

        var weights = await WaitForWeightsAsync(healthyUserId, w => w.Count == 1);
        Assert.Equal(1, weights["健身"]);

        // 坏消息被跳过：A 行仍保持原非法 JSON，未被 worker 覆盖（worker 在反序列化处抛异常，未走到 SaveChanges）
        await using var verifyCtx = _dbFactory.CreateDbContext();
        var brokenRow = await verifyCtx.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == brokenUserId);
        Assert.NotNull(brokenRow);
        Assert.Equal("not-valid-json", brokenRow!.KeywordsJson);

        // 坏消息应被记录为 Warning（异常为 JsonException），而非静默或让 worker 退出
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<dynamic>(),
            Arg.Is<Exception>(ex => ex is JsonException),
            Arg.Any<Func<dynamic, Exception?, string>>());
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
