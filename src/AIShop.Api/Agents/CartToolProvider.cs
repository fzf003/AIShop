using System.ComponentModel;
using AIShop.Core.Interfaces;

namespace AIShop.Api.Agents;

/// <summary>
/// 提供购物车操作工具函数，供 AI Agent 调用。
/// 每个方法对应一个 Agent 工具，使用 IServiceScopeFactory 创建作用域来解析 Scoped 服务。
/// </summary>
public sealed class CartToolProvider(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// 向购物车添加商品。
    /// </summary>
    [Description("向购物车添加商品。当用户表达购买某商品的意愿时调用此工具。")]
    public async Task<string> AddToCartAsync(
        [Description("用户名")] string username,
        [Description("商品ID，可在商品列表中查看")] int productId,
        [Description("购买数量")] int quantity)
    {
        if (quantity <= 0)
            return "数量必须大于0";

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var userRepo = services.GetRequiredService<IUserRepository>();
        var cartRepo = services.GetRequiredService<ICartRepository>();
        var catalog = services.GetRequiredService<IProductCatalogService>();

        var user = await userRepo.GetByUsernameAsync(username);
        if (user is null)
            return $"用户 {username} 不存在";

        var product = catalog.All.FirstOrDefault(p => p.Id == productId);
        if (product is null)
            return $"商品 ID {productId} 不存在";

        await cartRepo.AddItemAsync(
            user.Id, product.Id, product.Name, product.Price, product.Emoji, quantity);

        return $"已将 {product.Emoji} {product.Name} x{quantity} 添加到购物车";
    }

    /// <summary>
    /// 获取用户的购物车摘要。
    /// </summary>
    [Description("获取当前用户的购物车摘要，包含商品列表和总价。当用户询问购物车内容时调用此工具。")]
    public async Task<string> GetCartSummaryAsync(
        [Description("用户名")] string username)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var userRepo = services.GetRequiredService<IUserRepository>();
        var cartRepo = services.GetRequiredService<ICartRepository>();

        var user = await userRepo.GetByUsernameAsync(username);
        if (user is null)
            return $"用户 {username} 不存在";

        var cart = await cartRepo.GetByUserIdAsync(user.Id);
        if (cart is null || cart.Items.Count == 0)
            return "您的购物车是空的";

        var totalItems = cart.Items.Sum(i => i.Quantity);
        var totalPrice = cart.Items.Sum(i => i.ProductPrice * i.Quantity);

        var lines = cart.Items
            .Select(i => $"{i.ProductEmoji} {i.ProductName} x{i.Quantity} = ¥{i.ProductPrice * i.Quantity:F2}")
            .ToList();

        return $"您的购物车共 {totalItems} 件商品，总计 ¥{totalPrice:F2}\n"
             + string.Join("\n", lines.Select((line, idx) => $"{idx + 1}. {line}"));
    }

    /// <summary>
    /// 从购物车中移除指定商品。
    /// </summary>
    [Description("从购物车中移除指定商品。当用户表达移除某商品的意愿时调用此工具。")]
    public async Task<string> RemoveFromCartAsync(
        [Description("用户名")] string username,
        [Description("购物车中商品项的ID")] Guid itemId)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var userRepo = services.GetRequiredService<IUserRepository>();
        var cartRepo = services.GetRequiredService<ICartRepository>();

        var user = await userRepo.GetByUsernameAsync(username);
        if (user is null)
            return $"用户 {username} 不存在";

        var cart = await cartRepo.GetByUserIdAsync(user.Id);
        var item = cart?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return "该商品不在您的购物车中";

        await cartRepo.RemoveItemAsync(user.Id, itemId);
        return $"已将 {item.ProductEmoji} {item.ProductName} 从购物车移除";
    }
}
