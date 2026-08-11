using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AIShop.Api.Tests;

/// <summary>
/// ProductRepository 改查库 + IMemoryCache 缓存测试（T11）。
/// 对应 spec「ProductRepository 从数据库读取并缓存」。
/// </summary>
public sealed class ProductRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache;
    private readonly CountingDbContextFactory _dbFactory;

    public ProductRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // 播种：种子入库（与 T6 同一模式）
        using (var seedCtx = new AppDbContext(_options))
        {
            seedCtx.Database.EnsureCreated();
            seedCtx.Products.AddRange(ProductSeedData.Products);
            seedCtx.SaveChanges();
        }

        _cache = new MemoryCache(new MemoryCacheOptions());
        _dbFactory = new CountingDbContextFactory(_options);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public void GetAll_ReturnsAllProductsFromDatabase_MatchingSeed()
    {
        var repo = new ProductRepository(_cache, _dbFactory);

        var result = repo.GetAll();

        Assert.Equal(ProductSeedData.Products.Count, result.Count);
        Assert.Equal(1, _dbFactory.CreateCount); // 首次调用缓存未命中，查库一次
        foreach (var seed in ProductSeedData.Products)
        {
            var actual = result.Single(p => p.Id == seed.Id);
            Assert.Equal(seed.Name, actual.Name);
            Assert.Equal(seed.Category, actual.Category);
            Assert.Equal(seed.Tags, actual.Tags);   // 从 DB materialization，非默认值 []
            Assert.Equal(seed.Price, actual.Price);
            Assert.Equal(seed.Emoji, actual.Emoji);
        }
    }

    [Fact]
    public void GetAll_RepeatedCallWithinFiveMinutes_HitsCache_DoesNotQueryDbAgain()
    {
        var repo = new ProductRepository(_cache, _dbFactory);

        var first = repo.GetAll();
        var createCountAfterFirst = _dbFactory.CreateCount;

        Assert.Equal(ProductSeedData.Products.Count, first.Count);
        Assert.Equal(1, createCountAfterFirst);

        var second = repo.GetAll();

        Assert.Equal(ProductSeedData.Products.Count, second.Count);
        Assert.Same(first, second);                       // 缓存命中，返回同一 List 实例
        Assert.Equal(createCountAfterFirst, _dbFactory.CreateCount); // 第二次不再查库
    }

    [Fact]
    public void QueryFilter_MultipleMatches_ReturnsFirstInListOrder()
    {
        var repo = new ProductRepository(_cache, _dbFactory);

        // "电子产品" 分类有多个商品（Id 4/7/10/15），断言返回列表顺序中的首个
        var result = repo.QueryFilter(p => p.Category == "电子产品");

        Assert.NotNull(result);
        Assert.Equal(4, result.Id);
        Assert.Equal("无线降噪耳机", result.Name);
    }

    [Fact]
    public void QueryFilter_NoMatch_ReturnsNull()
    {
        var repo = new ProductRepository(_cache, _dbFactory);

        var result = repo.QueryFilter(p => p.Name.Contains("不存在的商品"));

        Assert.Null(result);
    }

    /// <summary>
    /// 计数用 IDbContextFactory：每次创建上下文递增计数，用于断言「缓存命中后不再查库」。
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
