using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 商品仓储实现，从数据库读取商品并缓存 5 分钟。
/// GetAll 返回完整商品目录（首次查库后写入 IMemoryCache 缓存），QueryFilter 按条件筛选。
/// </summary>
public sealed class ProductRepository(
    IMemoryCache cache,
    IDbContextFactory<AppDbContext> dbFactory) : IProductRepository
{
    private const string AllProductsCacheKey = "products_all";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 获取全量商品列表：优先命中 5 分钟缓存，未命中则查库并写入缓存。
    /// </summary>
    public IReadOnlyList<Product> GetAll()
        // GetOrCreate 返回可空类型，但工厂 db.Products.ToList() 永不返回 null（无行时返回空列表）
        => cache.GetOrCreate(AllProductsCacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
            using var db = dbFactory.CreateDbContext();
            return db.Products.ToList();
        })!;

    /// <summary>
    /// 按指定条件查询第一个匹配的商品。
    /// 在缓存的商品列表上返回首个满足 predicate 条件的商品，无匹配返回 null。
    /// </summary>
    /// <param name="predicate">筛选条件委托，如 p => p.Name.Contains("咖啡机", StringComparison.OrdinalIgnoreCase)</param>
    /// <returns>匹配的商品，无匹配返回 null</returns>
    public Product? QueryFilter(Func<Product, bool> predicate)
        => GetAll().FirstOrDefault(predicate);
}
