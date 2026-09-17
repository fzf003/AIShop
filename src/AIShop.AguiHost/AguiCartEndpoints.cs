using AIShop.Core.Entities;
using AIShop.Core.Interfaces;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 的购物车 REST 端点（agui-client-support 第 6 项，design §13.2 / §13.5 / §13.6 / §13.8）。
/// <para>
/// 根级 <c>/cart</c> 组（<c>RequireUsername</c> 缺省 <c>true</c> = 身份必填）：<c>GET /cart</c>（读）、
/// <c>POST /cart/items</c>（加购）；改量 / 移除 / 清空由 T14 在同一文件内追加。
/// </para>
/// <para>
/// <b>复用既有仓储、禁止第二套实现（spec「客户端支撑端点复用既有仓储」）</b>：购物车读写一律经
/// <see cref="ICartRepository"/>，商品经 <see cref="IProductRepository.GetAll()"/>（与 AI 工具
/// <c>add_to_cart</c> / <c>search_product</c>、<c>/products</c> 列表共用同一份 5 分钟缓存）。
/// 合并数量、清空、<c>UpdatedAt</c> 等业务规则只在 <see cref="Cart"/> 实体与 <c>CartRepository</c> 内——
/// 本文件<b>零业务规则</b>，只做「读身份 → 校验入参 → 调仓储 → 读回」。
/// </para>
/// <para>
/// <b>不调用仓储的保存方法</b>：<c>CartRepository</c> 的每个写方法内部各自提交更改，调用方再提交一次既冗余
/// 又会让「静默 no-op」的失败被掩盖。写库落 AguiHost 自己的业务库（<c>agui.db</c>），老库文件零接触。
/// </para>
/// <para>
/// <b>与 AI 工具的边界</b>：与 <c>CartToolProvider</c> 共享仓储与实体，<b>不共享</b>其工具层包装——后者是单例工具
/// 宿主（<c>IServiceScopeFactory</c> + <see cref="ICurrentUserAccessor"/>），返回给 LLM 的是自然语言字符串；
/// 本文件要的是 JSON DTO 与 <c>Guid</c> 级操作，故经 DI 直接注入 Scoped 仓储。
/// </para>
/// <para>
/// <b><c>?username=</c> 不是认证 / 授权</b>（spec「校验不等于认证」）：三个种子账户无密码、无凭证，身份由请求自称
/// ——写谁的用户名就是谁。中间件的存在性校验只解决「用户名拼错导致静默空车 / 孤儿购物车」的数据质量问题。
/// <b>严禁</b>在注释、文档或对外文案中把它表述为「登录校验」「登录」「鉴权」或「认证」。
/// </para>
/// </summary>
internal static class AguiCartEndpoints
{
    /// <summary>数量非正时的错误文案（必须拦在 <see cref="Cart.AddItem"/> 抛 <see cref="ArgumentOutOfRangeException"/> 之前）。</summary>
    private const string QuantityMustBePositiveDetail = "Quantity must be greater than 0";

    /// <summary>加购商品在商品目录中不存在时的错误文案。</summary>
    private const string ProductNotFoundDetail = "Product not found";

    /// <summary>
    /// 映射 AguiHost 的购物车端点（根级路径——AguiHost 无 <c>/api</c> 分组，与 <c>/models</c> / <c>/products</c> 同层）。
    /// <para>
    /// 组级挂 <see cref="AguiClientRestEndpoint"/> 元数据（<c>RequireUsername</c> 缺省 <c>true</c>）是中间件
    /// REST 分支的唯一依据：缺 / 空白 <c>?username=</c> → 中间件 <c>400</c> 短路，<b>不注入、不回落缺省用户</b>；
    /// 带了非空值 → 与 AG-UI 面同一份存在性校验（查无此人 → 404）后注入。该元数据由组继承到组内每个端点，
    /// 路由随分组走，不按 HTTP 方法或路径字符串判定（design §13.3）。
    /// </para>
    /// 路径与 <c>"/"</c>（AG-UI SSE）、<c>/models</c>、<c>/products</c>、<c>/health</c>、<c>/alive</c>、
    /// <c>/devui</c>、<c>/v1/*</c> 均不冲突；映射顺序不影响请求管线。
    /// </summary>
    internal static void MapCartEndpoints(this WebApplication app)
    {
        // 组级元数据用 RequireUsername 缺省值（true）= 身份必填；/products 是唯一的 false（公开可读）。
        var cart = app.MapGroup("/cart").WithMetadata(new AguiClientRestEndpoint());

        // GET /cart（读）与 POST /cart/items（加购）；改量 / 移除 / 清空三个端点由 T14 在本组内追加。
        cart.MapGet("", GetCartAsync);
        cart.MapPost("/items", AddCartItemAsync);
    }

    /// <summary>
    /// <c>GET /cart</c>：返回该用户「操作后的购物车」。无购物车行时返回 <b>200 + 空车结构</b>（不是 404，对齐老链语义）。
    /// </summary>
    private static async Task<IResult> GetCartAsync(
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;

        // 端点侧防御分支：仅当中间件未注入身份时命中（正常路径下中间件已按标记 400 / 404 短路）。
        // 字面量与中间件同源（AguiClientIdentity），保证同一失败语义只有一个文案来源。
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        // 取 Guid userId 是本端点对 ICartRepository 的固有前置（其 8 个方法全部以 Guid 为用户参数），
        // 不是「第二套校验」——复用的就是中间件同一份 IUserRepository.GetByUsernameAsync。
        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        return Results.Ok(ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct)));
    }

    /// <summary>
    /// <c>POST /cart/items</c>：加购后返回「操作后的购物车」。同商品已在车内由既有
    /// <see cref="Cart.AddItem"/> 合并数量（不产生重复条目）——本端点<b>不复制</b>该规则。
    /// </summary>
    private static async Task<IResult> AddCartItemAsync(
        AddCartItemRequest request,
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        IProductRepository products,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        // 入参校验全部发生在写入之前：数量非正会让 Cart.AddItem 抛异常，必须在此拦截为 400。
        if (request.Quantity <= 0)
            return Results.BadRequest(new { detail = QuantityMustBePositiveDetail });

        // 商品校验与取名 / 价 / emoji 走同一个单一数据源（与 /products、AI 工具共用 5 分钟缓存），不另建查询。
        var product = products.GetAll().FirstOrDefault(p => p.Id == request.ProductId);
        if (product is null)
            return Results.BadRequest(new { detail = ProductNotFoundDetail });

        await carts.AddItemAsync(
            user.Id, product.Id, product.Name, product.Price, product.Emoji, request.Quantity, ct);

        // 回读「操作后的购物车」：写端点与读端点返回同一形状，前端每个操作后可直接 setState，不必再发一次 GET。
        return Results.Ok(ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct)));
    }

    /// <summary>
    /// 购物车 → <see cref="CartResponse"/> 映射（camelCase 由 Web 默认序列化保证）。<paramref name="cart"/> 为
    /// <c>null</c>（无购物车行）时返回空车结构——<b>200 而非 404</b>，与老链同义；T14 的清空端点复用同一映射。
    /// </summary>
    private static CartResponse ToCartResponse(Cart? cart)
    {
        if (cart is null)
            return new CartResponse(Guid.Empty, [], 0, 0m, DateTime.UtcNow);

        return new CartResponse(
            cart.Id,
            [.. cart.Items.Select(ToCartItemDto)],
            cart.TotalItems,
            cart.TotalPrice,
            cart.UpdatedAt);
    }

    private static CartItemDto ToCartItemDto(CartItem item)
        => new(item.Id, item.ProductId, item.ProductName, item.ProductPrice, item.ProductEmoji, item.Quantity, item.AddedAt);
}

/// <summary>加购请求体：<c>{productId, quantity}</c>。</summary>
internal sealed record AddCartItemRequest(int ProductId, int Quantity);

/// <summary>改量请求体：<c>{quantity}</c>（绝对数量，非累加）。由 T14 的 PUT 端点消费。</summary>
internal sealed record UpdateCartItemQuantityRequest(int Quantity);

/// <summary>
/// 购物车条目 DTO（camelCase：<c>id</c>/<c>productId</c>/<c>productName</c>/<c>productPrice</c>/
/// <c>productEmoji</c>/<c>quantity</c>/<c>addedAt</c>）——字段与老链 <c>CartItemDto</c> 同名同义。
/// <c>id</c> 即 PUT / DELETE 用的条目 ID。
/// </summary>
internal sealed record CartItemDto(
    Guid Id, int ProductId, string ProductName, decimal ProductPrice,
    string ProductEmoji, int Quantity, DateTime AddedAt);

/// <summary>
/// 购物车响应 DTO（camelCase：<c>id</c>/<c>items</c>/<c>totalItems</c>/<c>totalPrice</c>/<c>updatedAt</c>）——
/// 字段与老链 <c>CartResponse</c> 同名同义，值取 <see cref="Cart"/> 实体的计算属性（<c>TotalItems</c> / <c>TotalPrice</c>）。
/// </summary>
internal sealed record CartResponse(
    Guid Id, List<CartItemDto> Items, int TotalItems,
    decimal TotalPrice, DateTime UpdatedAt);
