using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit.Sdk;

namespace AIShop.Api.Tests;

/// <summary>
/// T15 测试集合定义：本类启动 WebApplicationFactory + 临时 SQLite 文件库，
/// 置于 DisableParallelization 串行集合，避免与 ServiceDefaultsDebugTests 等并行宿主竞争
/// （learnings 先例：共享工作树并行 WebApplicationFactory 会触发 flaky 失败）。
/// </summary>
[CollectionDefinition(nameof(PreferenceServiceRegistrationTests), DisableParallelization = true)]
public sealed class PreferenceServiceRegistrationTestsCollection;

/// <summary>
/// T15 DI 注册偏好服务测试（design 4.2/4.4 修正版，对应 spec「偏好异步写入不阻塞响应」的注册）：
///  - IPreferenceRepository 每请求独立实例（Scoped：不同 scope 不同实例，同一 scope 内同一实例）
///  - IPreferenceQueue 为单例（两次解析 Assert.Same），且与具体 PreferenceQueue 共享同一实例
///  - PreferenceWriteHostedService 已注册为 hosted service 并真实启动消费队列（入队 → 轮询落库）
/// </summary>
[Collection(nameof(PreferenceServiceRegistrationTests))]
public sealed class PreferenceServiceRegistrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connStr;
    private readonly List<string> _createdDbPaths = [];

    public PreferenceServiceRegistrationTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"t15_{Guid.NewGuid():N}.db");
        _createdDbPaths.Add(dbPath);
        _connStr = $"Data Source={dbPath}";

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(_connStr));
                services.AddScoped<AppDbContext>(sp =>
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
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

    /// <summary>
    /// IPreferenceRepository 为 Scoped：不同请求（scope）解析出不同实例，同一请求内同一实例
    /// （spec「偏好异步写入不阻塞响应」的注册）。
    /// </summary>
    [Fact]
    public void PreferenceRepository_IsScoped_DifferentScopes_DistinctInstances_SameScope_SameInstance()
    {
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var repo1a = scope1.ServiceProvider.GetRequiredService<IPreferenceRepository>();
        var repo1b = scope1.ServiceProvider.GetRequiredService<IPreferenceRepository>();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IPreferenceRepository>();

        Assert.Same(repo1a, repo1b);   // 同一 scope（请求）内同一实例
        Assert.NotSame(repo1a, repo2); // 不同 scope（请求）不同实例
    }

    /// <summary>
    /// IPreferenceQueue 为单例：两次解析 Assert.Same，且与具体 PreferenceQueue 共享同一实例
    /// （design 4.4 修正：AddSingleton 具体类型 + 工厂映射接口，保证 hosted service 构造依赖可解析）。
    /// </summary>
    [Fact]
    public void PreferenceQueue_IsSingleton_SameInstance_AcrossResolutions()
    {
        var queue1 = _factory.Services.GetRequiredService<IPreferenceQueue>();
        var queue2 = _factory.Services.GetRequiredService<IPreferenceQueue>();
        var concrete = _factory.Services.GetRequiredService<PreferenceQueue>();

        Assert.Same(queue1, queue2);   // 接口单例：两次解析同一实例
        Assert.Same(queue1, concrete); // 接口与具体类型同一实例（worker 消费同一队列）
    }

    /// <summary>
    /// PreferenceWriteHostedService 已注册为 hosted service 且真实启动消费队列：
    /// 入队一条 UserPreferenceUpdate 后轮询临时库，worker 完成读-改-写落库
    /// （spec「偏好异步写入不阻塞响应」的注册，端到端证明 worker 已启动）。
    /// </summary>
    [Fact]
    public async Task PreferenceWriteHostedService_IsRegistered_AndConsumesQueueToPersist()
    {
        // hosted service 已注册（通过 AddHostedService 注册到 DI）
        var workers = _factory.Services.GetServices<IHostedService>()
            .OfType<PreferenceWriteHostedService>()
            .ToList();
        var worker = Assert.Single(workers);
        Assert.NotNull(worker);

        // worker 已启动消费：入队 → 轮询临时库断言落库
        var queue = _factory.Services.GetRequiredService<IPreferenceQueue>();
        var userId = Guid.NewGuid();
        Assert.True(queue.TryEnqueue(new UserPreferenceUpdate(userId, ["咖啡", "健身"])));

        var weights = await WaitForWeightsAsync(userId, w => w.Count == 2);
        Assert.Equal(1, weights["咖啡"]);
        Assert.Equal(1, weights["健身"]);
    }

    /// <summary>
    /// 轮询临时库读取落库的权重表，直到 matches 谓词成立；超时抛异常（避免因异步写时序不稳而 flaky）。
    /// </summary>
    private async Task<Dictionary<string, int>> WaitForWeightsAsync(
        Guid userId, Func<Dictionary<string, int>, bool> matches, int timeoutMs = 5000)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connStr).Options;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new AppDbContext(options);
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
}
