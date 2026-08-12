using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using AIShop.McpServer.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AIShop.McpServer.Tests;

/// <summary>
/// ProductTools 走 ProductCatalog(repository) 链路测试适配（T22）。
/// 链路：SQLite 内存库 + 种子 → ProductRepository(cache, dbFactory) → ProductCatalog(repository) → ProductTools。
/// 对应 design 6.2「ProductCatalog 改查库后 MatchProducts 行为等价」。
/// </summary>
public sealed class ProductToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache;
    private readonly CountingDbContextFactory _dbFactory;
    private readonly ProductTools _tools;

    public ProductToolsTests()
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
        _tools = new ProductTools(new ProductCatalog(new ProductRepository(_cache, _dbFactory)));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public void MatchProducts_ValidKeywords_ReturnsResults()
    {
        var result = _tools.MatchProducts(["运动", "户外"]);

        Assert.NotEmpty(result);
        Assert.True(result.Length <= 6);
        // "专业跑鞋" tags: ["跑步", "运动", "健身", "体育", "鞋子"] — matches "运动"
        Assert.Contains(result, p => p.Name == "专业跑鞋");
        // "户外徒步靴" tags: ["徒步", "户外", "冒险", "自然", "靴子"] — matches "户外"
        Assert.Contains(result, p => p.Name == "户外徒步靴");
    }

    [Fact]
    public void MatchProducts_EmptyKeywords_ReturnsEmpty()
    {
        var result = _tools.MatchProducts([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_UnknownKeyword_ReturnsEmpty()
    {
        var result = _tools.MatchProducts(["不存在的关键词xyz"]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_ResultLimit_Six()
    {
        // 用多个关键词触发足够多的匹配，验证上限为 6
        var result = _tools.MatchProducts(["运动", "户外", "音乐", "科技", "咖啡", "健身", "阅读"]);

        Assert.True(result.Length <= 6);
    }

    [Fact]
    public void MatchProducts_SingleKeyword_ReturnsMatches()
    {
        // "咖啡" → 意式浓缩咖啡机
        var result = _tools.MatchProducts(["咖啡"]);

        Assert.NotEmpty(result);
        Assert.Contains(result, p => p.Name == "意式浓缩咖啡机");
    }

    [Fact]
    public void MatchProducts_ReturnedDto_HasAllFields()
    {
        var result = _tools.MatchProducts(["运动"]);

        Assert.NotEmpty(result);
        var product = result[0];
        Assert.True(product.Id > 0);
        Assert.False(string.IsNullOrEmpty(product.Name));
        Assert.False(string.IsNullOrEmpty(product.Category));
        Assert.NotEmpty(product.Tags);
        Assert.True(product.Price > 0);
        Assert.False(string.IsNullOrEmpty(product.Emoji));
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
