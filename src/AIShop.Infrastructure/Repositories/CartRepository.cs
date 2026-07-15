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
        var cart = await GetOrCreateCartAsync(userId, ct);

        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem is not null)
        {
            existingItem.Quantity += quantity;
        }
        else
        {
            cart.Items.Add(new CartItem
            {
                CartId = cart.Id,
                ProductId = productId,
                ProductName = productName,
                ProductPrice = productPrice,
                ProductEmoji = productEmoji,
                Quantity = quantity
            });
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateItemQuantityAsync(
        Guid userId, Guid itemId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        var item = cart?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return;

        item.Quantity = quantity;
        cart!.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveItemAsync(
        Guid userId, Guid itemId, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        var item = cart?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return;

        cart!.Items.Remove(item);
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct = default)
    {
        var cart = await GetByUserIdAsync(userId, ct);
        if (cart is null) return;

        cart.Items.Clear();
        cart.UpdatedAt = DateTime.UtcNow;
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
