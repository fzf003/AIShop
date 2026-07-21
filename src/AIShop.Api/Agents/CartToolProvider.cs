using System.ComponentModel;
using AIShop.Core.Interfaces;

namespace AIShop.Api.Agents;

/// <summary>
/// 提供购物车操作工具函数，供 AI Agent 调用。
/// 用户名由 API 层通过 SetCurrentUser 注入，LLM 不需要关心。
/// </summary>
public sealed class CartToolProvider(IServiceScopeFactory scopeFactory)
{
    private static readonly AsyncLocal<string?> _currentUser = new();

    /// <summary>在 API 端点调用 Agent 前注入当前登录用户名。</summary>
    public static void SetCurrentUser(string username) => _currentUser.Value = username;

    private static string? GetCurrentUser() => _currentUser.Value;

    /// <summary>
    /// 按名称关键词搜索商品，供 AI Agent 调用。
    /// </summary>
    [Description("按名称搜索商品，返回商品名称、ID 和价格。当用户提到商品名时先调用此工具搜索。")]
    public async Task<string> SearchProductAsync(
        [Description("商品名称关键词，支持模糊匹配，如「咖啡」「跑鞋」")] string keyword)
    {
        using var scope = scopeFactory.CreateScope();
        var productRepo = scope.ServiceProvider.GetRequiredService<IProductRepository>();

        var all = productRepo.GetAll();
        var matches = all
            .Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || p.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"#{p.Id} {p.Name} — ¥{p.Price}")
            .ToList();

        if (matches.Count == 0)
            return $"未找到包含「{keyword}」的商品";

        return $"找到 {matches.Count} 个商品：\n" + string.Join("\n", matches);
    }

    /// <summary>
    /// 设置购物车中某个商品的精确数量。当用户说"只要X个""改为X个"时调用此工具。
    /// </summary>
    [Description("设置购物车中某个商品的精确数量。用户说'只要X个'改为X个'时调用，不是追加。")]
    public async Task<string> UpdateCartItemQuantityAsync(
        [Description("商品ID")] int productId,
        [Description("最终数量，用户说几个就设几个")] int quantity)
    {
        var username = GetCurrentUser();
        if (username is null) return "用户未登录";
        if (quantity < 0) return "数量不能为负";

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var userRepo = services.GetRequiredService<IUserRepository>();
        var cartRepo = services.GetRequiredService<ICartRepository>();
        var catalog = services.GetRequiredService<IProductCatalogService>();

        var user = await userRepo.GetByUsernameAsync(username);
        if (user is null) return $"用户 {username} 不存在";

        var product = catalog.All.FirstOrDefault(p => p.Id == productId);
        var productName = product?.Name ?? productId.ToString();

        if (quantity == 0)
        {
            await cartRepo.RemoveAllByProductIdAsync(user.Id, productId);
            return $"已移除 {productName}";
        }

        await cartRepo.SetQuantityAsync(user.Id, productId, quantity);
        return $"已设置 {productName} 数量为 {quantity}";
    }

    /// <summary>
    /// 向购物车添加商品。用户名由系统自动注入，无需传参。
    /// 单次请求内已调过此工具的请勿重复调用。
    /// </summary>
    [Description("向购物车添加商品。当用户表达购买某商品的意愿时调用此工具。已在购物车的商品不会重复添加。")]
    public async Task<string> AddToCartAsync(
        [Description("商品ID，可在商品列表中查看")] int productId,
        [Description("购买数量，默认为1")] int quantity = 1)
    {
        var username = GetCurrentUser();
        if (username is null) return "用户未登录，请先登录";

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

        // 幂等检查：如果商品已在购物车，返回当前状态，不重复添加
        var cart = await cartRepo.GetByUserIdAsync(user.Id);
        var existing = cart?.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
            return $"注意：{product.Name} 已在购物车中（当前 {existing.Quantity} 件）。如需增加数量请用 update_cart_quantity({productId}, 新数量) 设置最终数量。";

        await cartRepo.AddItemAsync(
            user.Id, product.Id, product.Name, product.Price, product.Emoji, quantity);

        return $"已添加 {product.Name} x{quantity} 到购物车";
    }

    /// <summary>
    /// 获取当前用户的购物车摘要。用户名由系统自动注入。
    /// </summary>
    [Description("获取当前用户的购物车摘要，包含商品列表和总价。当用户询问购物车内容时调用此工具。")]
    public async Task<string> GetCartSummaryAsync()
    {
        var username = GetCurrentUser();
        if (username is null) return "用户未登录";

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
            .Select(i => $"{i.ProductName} x{i.Quantity} = ¥{i.ProductPrice * i.Quantity:F2}")
            .ToList();

        return $"您的购物车共 {totalItems} 件商品，总计 ¥{totalPrice:F2}\n"
             + string.Join("\n", lines.Select((line, idx) => $"{idx + 1}. {line}"));
    }

    /// <summary>
    /// 从购物车中移除指定商品。用户名由系统自动注入。
    /// </summary>
    [Description("从购物车中移除指定商品。当用户表达移除某商品的意愿时调用此工具。")]
    public async Task<string> RemoveFromCartAsync(
        [Description("购物车中商品项的ID，可通过 get_cart_summary 获取")] Guid itemId)
    {
        var username = GetCurrentUser();
        if (username is null) return "用户未登录";

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
        return $"已移除 {item.ProductName}";
    }
}
