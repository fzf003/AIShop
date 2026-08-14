using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AIShop.Api.Tests;

/// <summary>
/// ProductCatalog 改走 ProductRepository 缓存链路测试（T10）。
/// 链路：SQLite 内存库 + 种子 → ProductRepository(cache, dbFactory) → ProductCatalog(repository)。
/// 对应 spec「ProductCatalog.All 来自数据库」。
/// </summary>
public sealed class ProductCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache;
    private readonly CountingDbContextFactory _dbFactory;
    private readonly IProductCatalogService _catalog;

    public ProductCatalogTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // 播种：种子入库（与 ProductRepositoryTests 同一模式）
        using (var seedCtx = new AppDbContext(_options))
        {
            seedCtx.Database.EnsureCreated();
            seedCtx.Products.AddRange(ProductSeedData.Products);
            seedCtx.SaveChanges();
        }

        _cache = new MemoryCache(new MemoryCacheOptions());
        _dbFactory = new CountingDbContextFactory(_options);
        _catalog = new ProductCatalog(new ProductRepository(_cache, _dbFactory));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public void SplitProducts_MatchingKeywords_ReturnsSplit()
    {
        var (recommended, others) = _catalog.SplitProducts(["跑步"]);

        Assert.Contains(recommended, p => p.Name == "专业跑鞋");
        Assert.Contains(recommended, p => p.Name == "高级瑜伽垫");
        Assert.DoesNotContain(recommended, p => p.Name == "经典皮夹克");
        Assert.Contains(others, p => p.Name == "经典皮夹克");
    }

    [Fact]
    public void SplitProducts_EmptyKeywords_ReturnsEmptyRecommended()
    {
        var (recommended, others) = _catalog.SplitProducts([]);

        Assert.Empty(recommended);
        Assert.Equal(6, others.Length);
    }

    [Fact]
    public void SplitProducts_AllProductsCoveredByCombinedKeywords()
    {
        var allKeywords = _catalog.KeywordMap.Keys.ToArray();
        var (recommended, _) = _catalog.SplitProducts(allKeywords);

        Assert.Equal(_catalog.All.Count, recommended.Length);
    }

    [Fact]
    public void SplitProducts_NonMatchingKeywords_ReturnsEmptyRecommended()
    {
        var (recommended, others) = _catalog.SplitProducts(["不存在的关键词"]);

        Assert.Empty(recommended);
        Assert.Equal(_catalog.All.Count, others.Length);
    }

    [Fact]
    public void PromoteProduct_KeywordExpansionMapsToTags()
    {
        var (recommended, _) = _catalog.SplitProducts(["运动"]);

        Assert.Contains(recommended, p => p.Name == "专业跑鞋");
        Assert.Contains(recommended, p => p.Name == "高级瑜伽垫");
        Assert.Contains(recommended, p => p.Name == "智能运动手表");
    }

    [Fact]
    public void MatchProducts_EmptyPreferences_ReturnsEmpty()
    {
        var result = _catalog.MatchProducts([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_WithPreferences_ReturnsScored()
    {
        var result = _catalog.MatchProducts(["咖啡"]);

        Assert.Contains(result, p => p.Name == "意式浓缩咖啡机");
        Assert.InRange(result.Length, 1, 6);
    }

    [Fact]
    public void KeywordMap_ContainsAllExpectedKeys()
    {
        var expected = new[]
        {
            "夹克", "鞋子", "靴子", "音乐", "咖啡", "健身", "瑜伽", "烹饪",
            "科技", "阅读", "户外", "时尚", "环保", "巧克力", "跑步", "家居",
            "送礼", "爱好", "耳机", "手表", "运动", "音频", "数码"
        };

        foreach (var key in expected)
        {
            Assert.True(_catalog.KeywordMap.ContainsKey(key),
                $"KeywordMap should contain '{key}'");
        }
    }

    [Fact]
    public void All_ReturnsContentMatchingProductsTable()
    {
        // 表当前内容（全新 context 从 DB materialization，与缓存数据源独立对比）
        using var db = new AppDbContext(_options);
        var table = db.Products.OrderBy(p => p.Id).ToList();

        var all = _catalog.All;

        Assert.Equal(table.Count, all.Count);
        foreach (var row in table)
        {
            var actual = all.Single(p => p.Id == row.Id);
            Assert.Equal(row.Name, actual.Name);
            Assert.Equal(row.Category, actual.Category);
            Assert.Equal(row.Tags, actual.Tags);
            Assert.Equal(row.Price, actual.Price);
            Assert.Equal(row.Emoji, actual.Emoji);
        }
    }

    /// <summary>
    /// 计数用 IDbContextFactory：每次创建上下文递增计数（与 ProductRepositoryTests 同模式）。
    /// </summary>
    private sealed class CountingDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public int CreateCount { get; private set; }

        public AppDbContext CreateDbContext()
        {
            CreateCount++;
            return new AppDbContext(options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return Task.FromResult(new AppDbContext(options));
        }
    }
}
