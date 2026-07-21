using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface ICartRepository
{
    Task<Cart?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task AddItemAsync(Guid userId, int productId, string productName, decimal productPrice, string productEmoji, int quantity, CancellationToken ct = default);
    Task UpdateItemQuantityAsync(Guid userId, Guid itemId, int quantity, CancellationToken ct = default);
    Task SetQuantityAsync(Guid userId, int productId, int quantity, CancellationToken ct = default);
    Task RemoveAllByProductIdAsync(Guid userId, int productId, CancellationToken ct = default);
    Task RemoveItemAsync(Guid userId, Guid itemId, CancellationToken ct = default);
    Task ClearAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
