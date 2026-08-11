using AIShop.Core.Entities;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AIShop.Api.Tests;

/// <summary>
/// AppDbContext 表映射契约测试（T6）：
/// Product 表（主键 Id + ValueGeneratedNever + Tags JSON 列往返）与 UserPreferences 表（主键 UserId + KeywordsJson TEXT）。
/// 对应 spec「Product 表幂等播种」「UserPreferences 表持久化」的表结构。
/// </summary>
public sealed class AppDbContextMappingsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextMappingsTests()
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
    public async Task Products_SaveSeedData_TagsRoundTrip_FromDatabase()
    {
        // 写入：种子入库
        using (var writeCtx = new AppDbContext(_options))
        {
            writeCtx.Products.AddRange(ProductSeedData.Products);
            await writeCtx.SaveChangesAsync();
        }

        // 读回：用全新 context，强制 EF 从 DB materialization（而非返回已跟踪实例）
        using var readCtx = new AppDbContext(_options);
        var readBack = await readCtx.Products.OrderBy(p => p.Id).ToListAsync();

        Assert.Equal(ProductSeedData.Products.Count, readBack.Count);
        foreach (var seed in ProductSeedData.Products)
        {
            var actual = readBack.Single(p => p.Id == seed.Id);

            Assert.Equal(seed.Id, actual.Id);          // 显式 Id 1..18 原样保留（ValueGeneratedNever 生效）
            Assert.Equal(seed.Name, actual.Name);
            Assert.Equal(seed.Category, actual.Category);
            Assert.Equal(seed.Tags, actual.Tags);      // Tags 经 JSON 列往返一致（非默认值 []）
            Assert.Equal(seed.Price, actual.Price);
            Assert.Equal(seed.Emoji, actual.Emoji);
        }
    }

    [Fact]
    public void Product_Model_Id_IsPrimaryKey_AndValueGeneratedNever()
    {
        using var ctx = new AppDbContext(_options);

        var entity = ctx.Model.FindEntityType(typeof(Product))!;
        Assert.Equal(nameof(Product.Id), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal(ValueGenerated.Never, entity.FindProperty(nameof(Product.Id))!.ValueGenerated);
    }

    [Fact]
    public async Task UserPreferences_InsertAndUpdate_Persist()
    {
        var userId = Guid.NewGuid();
        var firstUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var secondUpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        // 插入新行
        using (var writeCtx = new AppDbContext(_options))
        {
            writeCtx.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = "{\"咖啡\":1}",
                UpdatedAt = firstUpdatedAt
            });
            await writeCtx.SaveChangesAsync();
        }

        // 全新 context 读回，验证插入生效
        using (var readCtx = new AppDbContext(_options))
        {
            var inserted = await readCtx.UserPreferences.FindAsync(userId);
            Assert.NotNull(inserted);
            Assert.Equal("{\"咖啡\":1}", inserted!.KeywordsJson);
            Assert.Equal(firstUpdatedAt, inserted.UpdatedAt);
        }

        // 更新：改 KeywordsJson + UpdatedAt
        using (var updateCtx = new AppDbContext(_options))
        {
            var existing = await updateCtx.UserPreferences.FindAsync(userId);
            Assert.NotNull(existing);
            existing!.KeywordsJson = "{\"咖啡\":2,\"健身\":1}";
            existing.UpdatedAt = secondUpdatedAt;
            await updateCtx.SaveChangesAsync();
        }

        // 全新 context 读回，验证更新生效
        using var finalCtx = new AppDbContext(_options);
        var updated = await finalCtx.UserPreferences.FindAsync(userId);
        Assert.NotNull(updated);
        Assert.Equal("{\"咖啡\":2,\"健身\":1}", updated!.KeywordsJson);
        Assert.Equal(secondUpdatedAt, updated.UpdatedAt);
    }

    [Fact]
    public void UserPreferences_Model_UserIdPrimaryKey_AndKeywordsJsonText()
    {
        using var ctx = new AppDbContext(_options);

        var entity = ctx.Model.FindEntityType(typeof(UserPreferences))!;
        Assert.Equal(nameof(UserPreferences.UserId), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("TEXT", entity.FindProperty(nameof(UserPreferences.KeywordsJson))!.GetColumnType());
    }
}
