using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;

namespace AIShop.Infrastructure.Repositories;

internal sealed class CartRepository(AppDbContext db) : ICartRepository
{
    public async Task<Cart?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
    }

    public async Task AddItemAsync(
        Guid userId, int productId, string productName, decimal productPrice,
        string productEmoji, int quantity, CancellationToken ct = default)
    {
        try
        {
            var cart = await GetOrCreateCartAsync(userId, ct);
            cart.AddItem(productId, productName, productPrice, productEmoji, quantity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex.GetType().Name != "OperationCanceledException")
        {
            throw new InvalidOperationException(
                $"CartRepository.AddItemAsync failed for userId={userId}, productId={productId}: {ex.Message}", ex);
        }
    }

    public async Task UpdateItemQuantityAsync(
        Guid userId, Guid itemId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        if (cart is not null && cart.UpdateItemQuantity(itemId, quantity))
            await db.SaveChangesAsync(ct);
    }

    public async Task SetQuantityAsync(
        Guid userId, int productId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartAsync(userId, ct);
        if (cart.SetQuantity(productId, quantity))
            await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAllByProductIdAsync(
        Guid userId, int productId, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        if (cart is not null && cart.RemoveAllByProductId(productId) > 0)
            await db.SaveChangesAsync(ct);
    }

    public async Task RemoveItemAsync(
        Guid userId, Guid itemId, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        if (cart is not null && cart.RemoveItem(itemId))
            await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        if (cart is null) return;

        cart.Clear();
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);

    private async Task<Cart> GetOrCreateCartAsync(Guid userId, CancellationToken ct)
    {
        var cart = await db.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (cart is null)
        {
            cart = new Cart { UserId = userId };
            db.Carts.Add(cart);
        }

        return cart;
    }
}
