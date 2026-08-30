using System.ComponentModel;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AIShop.Service.Tools;

/// <summary>
/// 提供购物车操作工具函数，供 AI Agent 调用。
/// 用户名由调用方经 <see cref="ICurrentUserAccessor"/> 注入（按执行流传递），LLM 不需要关心。
/// </summary>
/// <param name="ragSearchService">
/// 混合检索服务（Task 10 引入）。可选参数而非必填：RAG 底座由 AddRag 注册（Task 9），
/// 在 AddRag 落地前 / 未启用 RAG 的宿主中该依赖缺失——此时回退纯关键词匹配，保持既有行为不破坏
/// （AI-3 降级语义；MS.DI 对带默认值的可选参数按 null 注入）。
/// </param>
public sealed class CartToolProvider(
    IServiceScopeFactory scopeFactory,
    ICurrentUserAccessor currentUserAccessor,
    IRagSearchService? ragSearchService = null)
{
    /// <summary>在 Agent 运行前注入当前登录用户名（实例方法，内部走 ICurrentUserAccessor）。</summary>
    public void SetCurrentUser(string username) => currentUserAccessor.SetCurrentUser(username);

    private string? GetCurrentUser() => currentUserAccessor.CurrentUser;

    /// <summary>
    /// 按关键词搜索商品，供 AI Agent 调用。
    /// Task 10 起升级为混合检索：委托 <see cref="IRagSearchService.SearchProductsAsync"/>（关键词命中 ∪ 向量召回，RRF 融合，AR-1）。
    /// 工具签名与输出格式保持兼容（非 breaking，§9）：`#Id Name — ¥Price`、无结果 `未找到包含「{keyword}」的商品`。
    /// </summary>
    [Description("按名称搜索商品，返回商品名称、ID 和价格。当用户提到商品名时先调用此工具搜索。")]
    public async Task<string> SearchProductAsync(
        [Description("商品名称关键词，支持模糊匹配，如「咖啡」「跑鞋」")] string keyword)
    {
        if (ragSearchService is not null)
        {
            try
            {
                // 混合检索路径：Top-N 由 RagSearchService 按 RRF 融合得分降序返回（AI-2 确定性）
                var hits = await ragSearchService.SearchProductsAsync(keyword);
                return FormatHits(keyword, hits);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // AI-3 降级：混合检索异常（模型缺失 / 索引未建 / 存储错误）→ 回退纯关键词路兜底，工具层不崩溃；
                // OperationCanceledException 正常传播（调用方取消语义，与 RagSearchService 同约定）
                Log.Warning(ex, "search_product 混合检索失败，降级为关键词路：{Keyword}", keyword);
            }
        }

        // 兜底路径：RAG 服务未注册（AddRag 未落地 / 宿主未启用 RAG）或检索失败 → 纯关键词匹配。
        // 谓词与 RagSearchService.KeywordRoute 保持一致（AR-3 回归不劣化），改动需两边同步。
        return KeywordSearch(keyword);
    }

    /// <summary>纯关键词路兜底：Name.Contains || Tags.Any（与 RagSearchService.KeywordRoute 同谓词，AR-3）。</summary>
    private string KeywordSearch(string keyword)
    {
        using var scope = scopeFactory.CreateScope();
        var productRepo = scope.ServiceProvider.GetRequiredService<IProductRepository>();

        var matches = productRepo.GetAll()
            .Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || p.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"#{p.Id} {p.Name} — ¥{p.Price}")
            .ToList();

        if (matches.Count == 0)
            return $"未找到包含「{keyword}」的商品";

        return $"找到 {matches.Count} 个商品：\n" + string.Join("\n", matches);
    }

    /// <summary>把混合检索结果格式化为工具兼容输出（§9：`#Id Name — ¥Price`；空结果保持无结果文案）。</summary>
    private static string FormatHits(string keyword, IReadOnlyList<ProductSearchHit> hits)
    {
        if (hits.Count == 0)
            return $"未找到包含「{keyword}」的商品";

        return $"找到 {hits.Count} 个商品：\n"
             + string.Join("\n", hits.Select(h => $"#{h.ProductId} {h.Name} — ¥{h.Price}"));
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
        var existing = cart?.FindItemByProductId(productId);
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
        if (cart is null || cart.IsEmpty)
            return "您的购物车是空的";

        var totalItems = cart.TotalItems;
        var totalPrice = cart.TotalPrice;

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
        var item = cart?.FindItem(itemId);
        if (item is null)
            return "该商品不在您的购物车中";

        await cartRepo.RemoveItemAsync(user.Id, itemId);
        return $"已移除 {item.ProductName}";
    }
}
