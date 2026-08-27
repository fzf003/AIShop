using AIShop.Core.Entities;

namespace AIShop.Api.Tests;

/// <summary>
/// Cart 聚合行为单元测试：验证领域方法自管理不变量
/// （数量校验、同商品合并、总额计算、条目增删改清）。
/// </summary>
public sealed class CartEntitiesTests
{
    // ============ AddItem ============

    [Fact]
    public void ShouldMergeQuantity_WhenAddingExistingProduct()
    {
        var cart = new Cart();

        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 2);
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 3);

        Assert.Single(cart.Items);
        Assert.Equal(5, cart.Items[0].Quantity);
    }

    [Fact]
    public void ShouldAddNewItem_WhenProductNotInCart()
    {
        var cart = new Cart();

        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 2);

        Assert.Single(cart.Items);
        Assert.Equal(1, cart.Items[0].ProductId);
        Assert.Equal("经典皮夹克", cart.Items[0].ProductName);
        Assert.Equal(2, cart.Items[0].Quantity);
    }

    [Fact]
    public void ShouldThrow_WhenAddQuantityNotPositive()
    {
        var cart = new Cart();

        Assert.Throws<ArgumentOutOfRangeException>(() => cart.AddItem(1, "x", 1m, "x", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cart.AddItem(1, "x", 1m, "x", -1));
    }

    // ============ 总额计算 ============

    [Fact]
    public void ShouldComputeTotalItemsAndPrice()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 2);
        cart.AddItem(2, "咖啡豆", 45.50m, "☕", 3);

        Assert.Equal(5, cart.TotalItems);
        Assert.Equal(2 * 189.99m + 3 * 45.50m, cart.TotalPrice);
        Assert.False(cart.IsEmpty);
    }

    [Fact]
    public void ShouldBeEmpty_WhenNoItems()
    {
        var cart = new Cart();

        Assert.True(cart.IsEmpty);
        Assert.Equal(0, cart.TotalItems);
        Assert.Equal(0m, cart.TotalPrice);
    }

    // ============ UpdateItemQuantity ============

    [Fact]
    public void ShouldUpdateQuantity_WhenItemExists()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);
        var itemId = cart.Items[0].Id;

        Assert.True(cart.UpdateItemQuantity(itemId, 4));
        Assert.Equal(4, cart.Items[0].Quantity);
    }

    [Fact]
    public void ShouldReturnFalse_WhenUpdatingMissingItem()
    {
        var cart = new Cart();

        Assert.False(cart.UpdateItemQuantity(Guid.NewGuid(), 1));
    }

    [Fact]
    public void ShouldThrow_WhenUpdateQuantityNotPositive()
    {
        var cart = new Cart();
        cart.AddItem(1, "x", 1m, "x", 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => cart.UpdateItemQuantity(cart.Items[0].Id, 0));
    }

    // ============ SetQuantity ============

    [Fact]
    public void ShouldSetQuantity_WhenProductExists()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);

        Assert.True(cart.SetQuantity(1, 5));
        Assert.Equal(5, cart.Items[0].Quantity);
    }

    [Fact]
    public void ShouldReturnFalse_WhenSettingQuantityForMissingProduct()
    {
        var cart = new Cart();

        Assert.False(cart.SetQuantity(999, 3));
    }

    // ============ RemoveItem / RemoveAllByProductId ============

    [Fact]
    public void ShouldRemoveItem_WhenItemExists()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);
        var itemId = cart.Items[0].Id;

        Assert.True(cart.RemoveItem(itemId));
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void ShouldReturnFalse_WhenRemovingMissingItem()
    {
        var cart = new Cart();

        Assert.False(cart.RemoveItem(Guid.NewGuid()));
    }

    [Fact]
    public void ShouldRemoveAllByProduct_WhenEntryExists()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);
        cart.AddItem(2, "咖啡豆", 45.50m, "☕", 1);

        Assert.Equal(1, cart.RemoveAllByProductId(1));
        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items[0].ProductId);
    }

    [Fact]
    public void ShouldRemoveZero_WhenProductNotInCart()
    {
        var cart = new Cart();

        Assert.Equal(0, cart.RemoveAllByProductId(1));
    }

    // ============ Clear ============

    [Fact]
    public void ShouldClear_WhenNotEmpty()
    {
        var cart = new Cart();
        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);

        cart.Clear();

        Assert.Empty(cart.Items);
        Assert.True(cart.IsEmpty);
        Assert.Equal(0, cart.TotalItems);
        Assert.Equal(0m, cart.TotalPrice);
    }

    // ============ UpdatedAt 变更标记 ============

    [Fact]
    public void ShouldTouchUpdatedAt_OnMutation()
    {
        var cart = new Cart();
        var original = cart.UpdatedAt;

        cart.AddItem(1, "经典皮夹克", 189.99m, "🧥", 1);

        Assert.True(cart.UpdatedAt >= original);
    }
}
