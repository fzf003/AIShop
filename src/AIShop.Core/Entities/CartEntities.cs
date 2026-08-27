namespace AIShop.Core.Entities;

public sealed class Cart
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid UserId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<CartItem> Items { get; set; } = [];

    /// <summary>总件数：各条目数量之和。</summary>
    public int TotalItems => Items.Sum(i => i.Quantity);

    /// <summary>总价：各条目单价 × 数量之和。</summary>
    public decimal TotalPrice => Items.Sum(i => i.ProductPrice * i.Quantity);

    /// <summary>购物车是否为空。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>按商品 ID 查找条目（幂等/去重检查用）。</summary>
    public CartItem? FindItemByProductId(int productId)
        => Items.FirstOrDefault(i => i.ProductId == productId);

    /// <summary>按条目 ID 查找条目。</summary>
    public CartItem? FindItem(Guid itemId)
        => Items.FirstOrDefault(i => i.Id == itemId);

    /// <summary>
    /// 加入商品：已存在同商品则合并数量，否则新增条目。数量必须大于 0。
    /// </summary>
    public void AddItem(int productId, string productName, decimal productPrice, string productEmoji, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var existing = FindItemByProductId(productId);
        if (existing is not null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            Items.Add(new CartItem
            {
                CartId = Id,
                ProductId = productId,
                ProductName = productName,
                ProductPrice = productPrice,
                ProductEmoji = productEmoji,
                Quantity = quantity
            });
        }

        Touch();
    }

    /// <summary>
    /// 更新指定条目数量。数量必须大于 0；条目不存在返回 false。
    /// </summary>
    public bool UpdateItemQuantity(Guid itemId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var item = FindItem(itemId);
        if (item is null) return false;

        item.Quantity = quantity;
        Touch();
        return true;
    }

    /// <summary>
    /// 设置指定商品的精确数量（用于「只要 X 个」）。数量必须大于 0；商品不在购物车返回 false。
    /// </summary>
    public bool SetQuantity(int productId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var item = FindItemByProductId(productId);
        if (item is null) return false;

        item.Quantity = quantity;
        Touch();
        return true;
    }

    /// <summary>移除指定条目。条目不存在返回 false。</summary>
    public bool RemoveItem(Guid itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return false;

        Items.Remove(item);
        Touch();
        return true;
    }

    /// <summary>移除指定商品的所有条目，返回移除条数。</summary>
    public int RemoveAllByProductId(int productId)
    {
        var targets = Items.Where(i => i.ProductId == productId).ToList();
        if (targets.Count == 0) return 0;

        foreach (var item in targets)
            Items.Remove(item);

        Touch();
        return targets.Count;
    }

    /// <summary>清空购物车。</summary>
    public void Clear()
    {
        if (Items.Count == 0) return;

        Items.Clear();
        Touch();
    }

    /// <summary>记录最后修改时间。</summary>
    private void Touch() => UpdatedAt = DateTime.UtcNow;
}

public sealed class CartItem
{
    public Guid Id { get; init; }
    public Guid CartId { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal ProductPrice { get; init; }
    public string ProductEmoji { get; init; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}
