using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<User> CreateAsync(string username, string displayName, CancellationToken ct = default);
}

public interface ISessionRepository
{
    Task<string> GetOrCreateSessionIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(Guid sessionId, CancellationToken ct = default);
}

/// <summary>
/// 商品仓储接口，提供商品数据的只读查询能力。
/// 商品数据源自 ProductCatalog（内存数据），无写入操作。
/// 用途：API 端点展示商品列表；Agent 工具按条件搜索匹配商品。
/// </summary>
public interface IProductRepository
{
    /// <summary>
    /// 获取全量商品列表。
    /// </summary>
    IReadOnlyList<Product> GetAll();

    /// <summary>
    /// 按指定条件查询单个商品。
    /// Agent 搜索场景下使用：用户输入商品名/关键词，遍历内存列表找到匹配。
    /// 返回第一个满足条件的商品，无匹配返回 null。
    /// </summary>
    /// <param name="predicate">筛选条件，如 p => p.Name.Contains("咖啡")</param>
    Product? QueryFilter(Func<Product, bool> predicate);
}
