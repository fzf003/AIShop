using AIShop.Core.Entities;
using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 商品仓储实现，基于 ProductCatalog（静态内存数据）提供商品查询。
/// GetAll 返回完整商品目录，QueryFilter 按条件筛选。
/// 注：商品数据不持久化到数据库，硬编码在 ProductCatalog 类中。
/// </summary>
internal sealed class ProductRepository(IProductCatalogService catalog) : IProductRepository
{
    /// <summary>
    /// 获取全量商品列表，直接委托给 IProductCatalogService.All。
    /// </summary>
    public IReadOnlyList<Product> GetAll() => catalog.All;

    /// <summary>
    /// 按指定条件查询第一个匹配的商品。
    /// 遍历 ProductCatalog 中的商品列表，返回首个满足 predicate 条件的商品。
    /// 用于 Agent 搜索场景：用户说"帮我加咖啡机"→QueryFilter(name.Contains("咖啡"))→找到 productId=5。
    /// </summary>
    /// <param name="predicate">筛选条件委托，如 p => p.Name.Contains("咖啡机", StringComparison.OrdinalIgnoreCase)</param>
    /// <returns>匹配的商品，无匹配返回 null</returns>
    public Product? QueryFilter(Func<Product, bool> predicate)
        => catalog.All.FirstOrDefault(predicate);
}
