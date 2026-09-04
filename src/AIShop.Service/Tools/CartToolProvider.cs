using System.ComponentModel;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AIShop.Service.Tools;

/// <summary>
/// 提供购物车操作工具函数，供 AI Agent 调用。
/// 用户名由调用方经 <see cref="ICurrentUserAccessor"/> 注入（按执行流传递），LLM 不需要关心。
/// </summary>
/// <param name="semanticSearch">
/// 商品语义搜索服务（可选注入）。未注册/检索失败时 search_product 返回无结果提示，不崩溃
/// （语义搜索由 AddRag 注册；未启用 RAG 的宿主该依赖为 null，行为退化为提示，不破坏既有流程）。
/// </param>
public sealed class CartToolProvider(
    IServiceScopeFactory scopeFactory,
    ICurrentUserAccessor currentUserAccessor,
    IProductSemanticSearch? semanticSearch = null)
{
    /// <summary>在 Agent 运行前注入当前登录用户名（实例方法，内部走 ICurrentUserAccessor）。</summary>
    public void SetCurrentUser(string username) => currentUserAccessor.SetCurrentUser(username);

    private string? GetCurrentUser() => currentUserAccessor.CurrentUser;

    /// <summary>
    /// 按语义搜索商品，供 AI Agent 调用。
    /// 纯语义检索（bge 向量，DB 端 KNN）：输入自然语言即可召回语义相关商品。
    /// 工具签名与输出格式保持兼容：`#Id Name — ¥Price`、无结果 `未找到包含「{keyword}」的商品`。
    /// </summary>
    [Description("按名称搜索商品，返回商品名称、ID、价格和相关度。当用户提到商品名时先调用此工具搜索。")]
    public async Task<string> SearchProductAsync(
        [Description("商品关键词，如「咖啡」「跑鞋」")] string keyword,
        [Description("可选：用户明确说的商品类别（厨房用品/健身/电子产品/家居/服装/鞋类等，须为商品类别，未明确可省略")] string? category = null)
    {
        if (semanticSearch is not null)
        {
            try
            {
                // category 非空 → 仅在该类别内语义召回（精确维度优先，向量只在该子集排序，避免泛类排前）
                var hits = await semanticSearch.SearchAsync(keyword, category: category);
                return FormatHits(keyword, hits);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 语义检索异常（模型缺失 / 索引未建 / 存储错误）→ 返回无结果提示，工具层不崩溃；
                // OperationCanceledException 正常传播（调用方取消语义）
                Log.Warning(ex, "search_product 语义搜索失败：{Keyword}", keyword);
            }
        }

        // 语义搜索未注册（宿主未启用 RAG）或失败 → 友好提示，不崩溃
        return $"未找到包含「{keyword}」的商品";
    }

    /// <summary>把语义检索结果格式化为工具兼容输出：`#Id Name — ¥Price`；空结果保持无结果文案。</summary>
    private static string FormatHits(string keyword, IReadOnlyList<ProductSearchHit> hits)
    {
        if (hits.Count == 0)
            return $"未找到包含「{keyword}」的商品";

        // 带相关度分 + 类别展示：LLM 可见 [0.94] vs [0.71]，可判断匹配强弱，低分商品不硬推
        return $"找到 {hits.Count} 个商品：\n"
             + string.Join("\n", hits.Select(h => $"[{h.Score:F2}] #{h.ProductId} {h.Name}（{h.Category}） — ¥{h.Price}"));
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

    /// <summary>
    /// 把购物车/商品工具方法注册为 Agent 可调用的 AI 工具（工具名与描述集中定义在工具宿主，
    /// Agent 侧无需逐个注册）。
    /// </summary>
    public IReadOnlyList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(
                (Func<int, int, Task<string>>)((productId, quantity) => AddToCartAsync(productId, quantity)),
                "add_to_cart",
                "追加商品到购物车。参数 productId=商品ID, quantity=追加数量。在现有数量上追加，不是设置最终数量。"),
            AIFunctionFactory.Create(
                (Func<int, int, Task<string>>)((productId, quantity) => UpdateCartItemQuantityAsync(productId, quantity)),
                "update_cart_quantity",
                "设置购物车中某个商品的精确数量。参数 productId=商品ID, quantity=最终数量。用户说'只要X个'时调用。"),
            AIFunctionFactory.Create(
                (Func<Task<string>>)(() => GetCartSummaryAsync()),
                "get_cart_summary",
                "查看当前用户的购物车摘要，无参数。"),
            AIFunctionFactory.Create(
                (Func<Guid, Task<string>>)(itemId => RemoveFromCartAsync(itemId)),
                "remove_from_cart",
                "从购物车中移除指定商品。参数 itemId=购物车中商品项的ID。"),
            // 方法组注册（非 lambda）：保留 SearchProductAsync 的参数默认值，category 才是可选参数。
            // lambda 注册会丢失默认值 → AIFunction schema 把 category 标 required → LLM 不传时调用失败。
            AIFunctionFactory.Create(
                (Func<string, string?, Task<string>>)SearchProductAsync,
                "search_product",
                "搜索商品。keyword=用户要的商品关键词（如咖啡机、耳机）；category=可选商品类别（厨房用品/健身/电子产品等，须为真实类别），"
                + "用户明确说类型时填）。用户明确指定商品类型/类别时必须优先返回该类商品，不得用不相关商品充数；"
                + "未找到足够相关商品时如实告知，不要硬推。"),
        ];
    }
}
