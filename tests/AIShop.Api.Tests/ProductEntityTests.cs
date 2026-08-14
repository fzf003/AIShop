using System.Runtime.CompilerServices;
using AIShop.Core.Entities;
using AIShop.Core.StaticData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIShop.Api.Tests;

/// <summary>
/// Product 实体 T3 契约测试：字段/类型保持原契约（design 3.1），
/// P1 修复（init→set）的类型级 + EF 往返级验证。
/// </summary>
public sealed class ProductEntityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TestProductDbContext> _options;

    public ProductEntityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestProductDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new TestProductDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    /// <summary>
    /// 测试专用上下文：仅映射 Product，映射与 T6 的 AppDbContext 配置一致（主键 Id + 种子显式 Id 不自动生成）。
    /// 用于在 AppDbContext 尚未新增 DbSet 前独立验证 Product 的 EF materialization 能力。
    /// </summary>
    private sealed class TestProductDbContext : DbContext
    {
        public TestProductDbContext(DbContextOptions<TestProductDbContext> options) : base(options) { }

        public DbSet<Product> Products => Set<Product>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(e =>
            {
                e.HasKey(p => p.Id);
                e.Property(p => p.Id).ValueGeneratedNever();
            });
        }
    }

    [Fact]
    public void Product_FieldNamesAndTypes_KeepOriginalContract()
    {
        var type = typeof(Product);

        Assert.Equal(typeof(int), type.GetProperty("Id")!.PropertyType);
        Assert.Equal(typeof(string), type.GetProperty("Name")!.PropertyType);
        Assert.Equal(typeof(string), type.GetProperty("Category")!.PropertyType);
        Assert.Equal(typeof(string[]), type.GetProperty("Tags")!.PropertyType);
        Assert.Equal(typeof(decimal), type.GetProperty("Price")!.PropertyType);
        Assert.Equal(typeof(string), type.GetProperty("Emoji")!.PropertyType);
    }

    [Fact]
    public void Product_AllProperties_HaveSettableAccessors_ForEFMaterialization()
    {
        // P1 修复：EF Core 无法给 init-only 属性赋值，setter 不能带 IsExternalInit 修饰符
        foreach (var property in typeof(Product).GetProperties())
        {
            var setter = property.GetSetMethod();
            Assert.NotNull(setter);
            Assert.False(
                setter!.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(IsExternalInit)),
                $"{property.Name} 不应为 init-only，必须可被 EF Core 从 DB 赋值");
        }
    }

    [Fact]
    public async Task Product_EfRoundTrip_FromSeed_RestoresTagsAndPrice()
    {
        // 写入：种子入库
        using (var writeCtx = new TestProductDbContext(_options))
        {
            writeCtx.Products.AddRange(ProductSeedData.Products);
            await writeCtx.SaveChangesAsync();
        }

        // 读回：用全新 context，强制 EF 从 DB materialization（而非返回已跟踪实例）
        using var readCtx = new TestProductDbContext(_options);
        var readBack = await readCtx.Products.OrderBy(p => p.Id).ToListAsync();

        Assert.Equal(ProductSeedData.Products.Count, readBack.Count);
        foreach (var seed in ProductSeedData.Products)
        {
            var actual = readBack.Single(p => p.Id == seed.Id);

            Assert.Equal(seed.Name, actual.Name);
            Assert.Equal(seed.Category, actual.Category);
            Assert.Equal(seed.Tags, actual.Tags);      // 非默认值 []，验证 set 生效
            Assert.Equal(seed.Price, actual.Price);    // 非默认值 0m，验证 set 生效
            Assert.Equal(seed.Emoji, actual.Emoji);
        }
    }
}
