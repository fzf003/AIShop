namespace AIShop.Core.Entities;

public sealed class Cart
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid UserId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<CartItem> Items { get; set; } = [];
}

public sealed class CartItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CartId { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal ProductPrice { get; init; }
    public string ProductEmoji { get; init; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}
