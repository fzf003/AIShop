using AIShop.Core.Interfaces;

namespace AIShop.Api.Features.Cart;

public sealed record AddItemRequest(int ProductId, int Quantity);
public sealed record UpdateQuantityRequest(int Quantity);

public sealed record CartItemDto(
    Guid Id, int ProductId, string ProductName, decimal ProductPrice,
    string ProductEmoji, int Quantity, DateTime AddedAt);

public sealed record CartResponse(
    Guid Id, List<CartItemDto> Items, int TotalItems,
    decimal TotalPrice, DateTime UpdatedAt);

public sealed record CartSummaryResponse(
    int TotalItems, decimal TotalPrice, string[] Items);

public sealed record ErrorResponse(string Error);

public static class CartEndpoints
{
    public static void MapCartEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/cart").WithTags("Cart");

        // 1. 获取购物车
        api.MapGet("/{username}", async (
            string username,
            IUserRepository users,
            ICartRepository cartRepo,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            var cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            if (cart is null)
                return Results.Ok(new CartResponse(
                    Guid.Empty, [], 0, 0m, DateTime.UtcNow));

            return Results.Ok(ToCartResponse(cart));
        });

        // 2. 添加商品到购物车
        api.MapPost("/{username}/items", async (
            string username,
            AddItemRequest req,
            IUserRepository users,
            ICartRepository cartRepo,
            IProductCatalogService catalog,
            CancellationToken ct) =>
        {
            if (req.Quantity <= 0)
                return Results.BadRequest(new ErrorResponse("数量必须大于0"));

            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            var product = catalog.All.FirstOrDefault(p => p.Id == req.ProductId);
            if (product is null)
                return Results.BadRequest(new ErrorResponse("商品不存在"));

            await cartRepo.AddItemAsync(
                user.Id, product.Id, product.Name,
                product.Price, product.Emoji, req.Quantity, ct);

            var cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            return Results.Ok(ToCartResponse(cart!));
        });

        // 3. 更新商品数量
        api.MapPut("/{username}/items/{itemId}", async (
            string username,
            Guid itemId,
            UpdateQuantityRequest req,
            IUserRepository users,
            ICartRepository cartRepo,
            CancellationToken ct) =>
        {
            if (req.Quantity <= 0)
                return Results.BadRequest(new ErrorResponse("数量必须大于0"));

            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            var cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            var item = cart?.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
                return Results.NotFound(new ErrorResponse("商品不在购物车中"));

            await cartRepo.UpdateItemQuantityAsync(user.Id, itemId, req.Quantity, ct);

            cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            return Results.Ok(ToCartResponse(cart!));
        });

        // 4. 删除购物车商品
        api.MapDelete("/{username}/items/{itemId}", async (
            string username,
            Guid itemId,
            IUserRepository users,
            ICartRepository cartRepo,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            var cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            var item = cart?.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
                return Results.NotFound(new ErrorResponse("商品不在购物车中"));

            await cartRepo.RemoveItemAsync(user.Id, itemId, ct);

            cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            return Results.Ok(ToCartResponse(cart!));
        });

        // 5. 清空购物车
        api.MapDelete("/{username}", async (
            string username,
            IUserRepository users,
            ICartRepository cartRepo,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            await cartRepo.ClearAsync(user.Id, ct);
            return Results.Ok(new { message = "购物车已清空" });
        });

        // 6. 获取购物车摘要（Agent 使用）
        api.MapGet("/{username}/summary", async (
            string username,
            IUserRepository users,
            ICartRepository cartRepo,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(username, ct);
            if (user is null)
                return Results.NotFound(new ErrorResponse("用户不存在"));

            var cart = await cartRepo.GetByUserIdAsync(user.Id, ct);
            if (cart is null || cart.Items.Count == 0)
                return Results.Ok(new CartSummaryResponse(0, 0m, []));

            var items = cart.Items
                .Select(i => $"{i.ProductEmoji} {i.ProductName} x{i.Quantity}")
                .ToArray();

            return Results.Ok(new CartSummaryResponse(
                cart.Items.Sum(i => i.Quantity),
                cart.Items.Sum(i => i.ProductPrice * i.Quantity),
                items));
        });
    }

    private static CartResponse ToCartResponse(Core.Entities.Cart cart)
    {
        return new CartResponse(
            cart.Id,
            cart.Items.Select(i => new CartItemDto(
                i.Id, i.ProductId, i.ProductName, i.ProductPrice,
                i.ProductEmoji, i.Quantity, i.AddedAt)).ToList(),
            cart.Items.Sum(i => i.Quantity),
            cart.Items.Sum(i => i.ProductPrice * i.Quantity),
            cart.UpdatedAt);
    }
}
