using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 的购物车 REST 端点（agui-client-support 第 6 项，design §13.2 / §13.5 / §13.6 / §13.8）。
/// <para>
/// 根级 <c>/cart</c> 组（<c>RequireUsername</c> 缺省 <c>true</c> = 身份必填）：<c>GET /cart</c>（读）、
/// <c>POST /cart/items</c>（加购）、<c>PUT /cart/items/{itemId:guid}</c>（改量为<b>绝对数量</b>）、
/// <c>DELETE /cart/items/{itemId:guid}</c>（移除条目）、<c>DELETE /cart</c>（清空，幂等）。
/// </para>
/// <para>
/// <b>静默 no-op 陷阱（改 / 删必读）</b>：<c>CartRepository.UpdateItemQuantityAsync</c> 与
/// <c>RemoveItemAsync</c> 在条目不存在时<b>静默 no-op（返回 <c>void</c>、不报错）</b> → 若端点在调用前不预检，
/// 「改 / 删别人或不存在的条目」会返回 <b>200 假成功</b>。预检（<c>GetByUserIdAsync</c> + <see cref="Cart.FindItem"/>）
/// 是防 200 假成功的唯一手段，也是「条目不属于本人 → 404」这条契约的唯一实现点。
/// </para>
/// <para>
/// <b>路由约束 <c>{itemId:guid}</c></b>：非 GUID 段不匹配路由 → 框架直接 404（<b>不进入 handler</b>、不暴露数据）；
/// 该 404 由路由层产生、响应体为空，<b>不是</b> <c>{"detail":"Cart item not found"}</c>。
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

    /// <summary>
    /// 幂等键的请求头名（L11）。
    /// <para>
    /// 加购是**累加**语义（<c>existing.Quantity += quantity</c>），而网络层重试 / 代理重发会让**同一个**
    /// 请求到达两次 —— 没有幂等约定时数量会翻倍，且返回 200 看起来一切正常。约定：调用方为**每一次用户操作**
    /// 生成一个键随请求带上；服务端按它去重，重复到达的请求**回放首次结果、不再累加**。
    /// </para>
    /// <para>缺失 / 空白该头时走既有非幂等路径（向后兼容，不改既有调用方的行为）。</para>
    /// </summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>幂等记录的保留时长（覆盖重试 / 双击的时间尺度，不必更长）。</summary>
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(10);

    /// <summary>加购商品在商品目录中不存在时的错误文案。</summary>
    private const string ProductNotFoundDetail = "Product not found";

    /// <summary>
    /// 改 / 删的条目不在<b>本人</b>购物车时的错误文案（预检失败，防 <c>CartRepository</c> 静默 no-op 的 200 假成功）。
    /// </summary>
    private const string CartItemNotFoundDetail = "Cart item not found";

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

        // GET /cart（读）/ POST /cart/items（加购）/ PUT /cart/items/{itemId:guid}（改量）
        // / DELETE /cart/items/{itemId:guid}（移除条目）/ DELETE /cart（清空）；5 端点共享同一组级标记与身份前置。
        // 「改 / 删」两端点的预检顺序（预检在写之前）见各自 handler 注释。
        cart.MapGet("", GetCartAsync);
        cart.MapPost("/items", AddCartItemAsync);
        cart.MapPut("/items/{itemId:guid}", UpdateCartItemQuantityAsync);
        cart.MapDelete("/items/{itemId:guid}", RemoveCartItemAsync);
        cart.MapDelete("", ClearCartAsync);
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
        HttpContext http,
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        IProductRepository products,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        // 幂等（L11）：同一 Idempotency-Key 的重复请求**回放首次结果**，不再累加。
        // 放在用户校验之后 —— 不把「身份都还没确认」的请求写进幂等缓存。
        var idempotencyKey = ReadIdempotencyKey(http, username);
        if (idempotencyKey is not null
            && cache.TryGetValue<CartResponse>(idempotencyKey, out var replayed)
            && replayed is not null)
        {
            return Results.Ok(replayed);
        }

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
        var response = ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct));

        // 只有**成功**的加购才记幂等结果 —— 上面的 400 / 404 各自提前 return，不会被回放成成功。
        if (idempotencyKey is not null)
            cache.Set(idempotencyKey, response, IdempotencyWindow);

        return Results.Ok(response);
    }

    /// <summary>
    /// 读并规范化幂等键（L11）：头缺失 / 空白 → <c>null</c>（走既有非幂等路径）；
    /// 有值时返回**按用户名隔离**的缓存键 —— 两个账户偶然用了同一个键，也不会互相回放对方的购物车。
    /// </summary>
    private static string? ReadIdempotencyKey(HttpContext http, string username)
    {
        if (!http.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values)) return null;

        var raw = values.ToString().Trim();
        return raw.Length == 0 ? null : $"cart_idem_{username}_{raw}";
    }

    /// <summary>
    /// <c>PUT /cart/items/{itemId:guid}</c>：把该条目数量设为请求体给出的<b>绝对数量</b>（非累加），返回操作后的购物车。
    /// <para>
    /// <b>预检是本节的关键</b>：<c>CartRepository.UpdateItemQuantityAsync</c> 条目不存在时静默 no-op（返回 <c>void</c>），
    /// 故必须先 <c>GetByUserIdAsync</c> + <see cref="Cart.FindItem"/> 确认条目<b>在本人的车里</b>——
    /// 条目属于他人 / 不存在 → 404 <c>Cart item not found</c>；否则会返回 200 假成功。
    /// 数量非正同样拦在 <see cref="Cart.UpdateItemQuantity"/> 抛 <see cref="ArgumentOutOfRangeException"/> 之前。
    /// </para>
    /// </summary>
    private static async Task<IResult> UpdateCartItemQuantityAsync(
        Guid itemId,
        UpdateCartItemQuantityRequest request,
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        if (request.Quantity <= 0)
            return Results.BadRequest(new { detail = QuantityMustBePositiveDetail });

        // 预检：条目必须存在于【本人】购物车，否则仓储会静默 no-op 造出 200 假成功。
        var cart = await carts.GetByUserIdAsync(user.Id, ct);
        if (cart?.FindItem(itemId) is null)
            return Results.NotFound(new { detail = CartItemNotFoundDetail });

        await carts.UpdateItemQuantityAsync(user.Id, itemId, request.Quantity, ct);

        return Results.Ok(ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct)));
    }

    /// <summary>
    /// <c>DELETE /cart/items/{itemId:guid}</c>：移除该条目后返回操作后的购物车。
    /// 与改量同构——<b>先预检条目属于本人购物车</b>（<c>CartRepository.RemoveItemAsync</c> 同样静默 no-op），
    /// 不在 → 404 <c>Cart item not found</c>。
    /// </summary>
    private static async Task<IResult> RemoveCartItemAsync(
        Guid itemId,
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        // 预检：条目不在本人购物车 → 404（防 RemoveItemAsync 静默 no-op 的 200 假成功）。
        var cart = await carts.GetByUserIdAsync(user.Id, ct);
        if (cart?.FindItem(itemId) is null)
            return Results.NotFound(new { detail = CartItemNotFoundDetail });

        await carts.RemoveItemAsync(user.Id, itemId, ct);

        return Results.Ok(ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct)));
    }

    /// <summary>
    /// <c>DELETE /cart</c>：清空该用户购物车的全部条目，返回空车响应（复用 <see cref="ToCartResponse"/>）。
    /// <b>幂等</b>：本就无购物车行时 <c>CartRepository.ClearAsync</c> 内部直接返回（无副作用），端点仍返回 200 空车；
    /// 清空只作用于该 <c>userId</c> 的行，<b>不影响其他用户</b>。
    /// </summary>
    private static async Task<IResult> ClearCartAsync(
        ICurrentUserAccessor accessor,
        IUserRepository users,
        ICartRepository carts,
        CancellationToken ct)
    {
        var username = accessor.CurrentUser;
        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new { detail = AguiClientIdentity.UsernameRequiredDetail });

        var user = await users.GetByUsernameAsync(username, ct);
        if (user is null)
            return Results.NotFound(new { detail = AguiClientIdentity.UserNotFoundDetail });

        // 无购物车行时仓储内部 no-op（不抛、不建行）——端点无需为此分支，回读即是空车结构。
        await carts.ClearAsync(user.Id, ct);

        return Results.Ok(ToCartResponse(await carts.GetByUserIdAsync(user.Id, ct)));
    }

    /// <summary>
    /// 购物车 → <see cref="CartResponse"/> 映射（camelCase 由 Web 默认序列化保证）。<paramref name="cart"/> 为
    /// <c>null</c>（无购物车行）时返回空车结构——<b>200 而非 404</b>，与老链同义；清空端点复用同一映射。
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

/// <summary>改量请求体：<c>{quantity}</c>（绝对数量，非累加），由 <c>PUT /cart/items/{itemId:guid}</c> 消费。</summary>
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
