using AIShop.Core.Entities;
using AIShop.Core.ValueObjects;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIShop.Api.Tests;

/// <summary>
/// T13 PreferenceRepository 实现测试（design 4.2，对应 spec「UserPreferences 表持久化」）：
/// GetByUserIdAsync 无记录返回 null / 有记录返回该行；UpsertAsync 首次插入新行、
/// 再次写入按同 UserId 覆盖更新且 UpdatedAt 刷新（不产生重复行）。
/// </summary>
public sealed class PreferenceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PreferenceRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetByUserIdAsync_NoRecord_ReturnsNull()
    {
        using var db = new AppDbContext(_options);
        var repo = new PreferenceRepository(db);

        var result = await repo.GetByUserIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByUserIdAsync_ExistingRecord_ReturnsStoredRow()
    {
        var userId = Guid.NewGuid();
        using (var seedCtx = new AppDbContext(_options))
        {
            seedCtx.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = "{\"咖啡\":2}",
            });
            await seedCtx.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var repo = new PreferenceRepository(db);

        var result = await repo.GetByUserIdAsync(userId);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal(2, result.KeywordWeights["咖啡"]);
    }

    [Fact]
    public async Task UpsertAsync_FirstInsert_CreatesSingleRow()
    {
        var userId = Guid.NewGuid();
        using var db = new AppDbContext(_options);
        var repo = new PreferenceRepository(db);

        await repo.UpsertAsync(PreferenceProfile.FromKeywordsJson(userId, """{"咖啡":2}""", DateTime.UtcNow));

        // 全新 context 读回，强制从 DB materialization，验证插入已落库且仅一行
        using var readCtx = new AppDbContext(_options);
        var rows = await readCtx.UserPreferences.Where(u => u.UserId == userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("{\"咖啡\":2}", rows[0].KeywordsJson);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_OverwritesBySameUserId_AndRefreshesUpdatedAt()
    {
        var userId = Guid.NewGuid();
        var originalUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 预置旧偏好：权重 {"咖啡":2}，旧 UpdatedAt
        using (var seedCtx = new AppDbContext(_options))
        {
            seedCtx.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = "{\"咖啡\":2}",
                UpdatedAt = originalUpdatedAt,
            });
            await seedCtx.SaveChangesAsync();
        }

        var newUpdatedAt = DateTime.UtcNow;
        using var db = new AppDbContext(_options);
        var repo = new PreferenceRepository(db);
        await repo.UpsertAsync(PreferenceProfile.FromKeywordsJson(userId, """{"咖啡":3,"健身":1}""", newUpdatedAt));

        // 全新 context 读回：仍只有一行（未插入重复行）、内容被覆盖、UpdatedAt 刷新为新值
        using var readCtx = new AppDbContext(_options);
        var rows = await readCtx.UserPreferences.Where(u => u.UserId == userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("{\"咖啡\":3,\"健身\":1}", rows[0].KeywordsJson);
        Assert.True(rows[0].UpdatedAt > originalUpdatedAt, "UpdatedAt 应被刷新为新值，而非保留旧值");
        Assert.InRange(rows[0].UpdatedAt, newUpdatedAt.AddSeconds(-10), newUpdatedAt.AddSeconds(10));
    }
}
