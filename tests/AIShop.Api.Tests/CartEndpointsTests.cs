using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Api.Features.Cart;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Api.Tests;

/// <summary>
/// 购物车集成测试。
/// 每个测试使用不同的用户名，通过工厂 DI 直接预创建测试用户。
/// </summary>
public sealed class CartEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    // 每个测试取一个唯一用户名，避免数据冲突
    private static int _counter;

    private static string NextUser() => $"cartuser_{Interlocked.Increment(ref _counter)}";

    public CartEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 确保指定用户存在于数据库，然后返回 HTTP 客户端。
    /// </summary>
    private HttpClient CreateClientFor(string username)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.Users.Any(u => u.Username == username))
            {
                db.Users.Add(new User { Username = username, DisplayName = username });
                db.SaveChanges();
            }
        }
        return _factory.CreateClient();
    }

    // ============ 成功场景 ============

    [Fact]
    public async Task ShouldAddItem_WhenProductExists()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        var response = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 2));
        response.EnsureSuccessStatusCode();

        var cart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.NotEqual(Guid.Empty, cart!.Id);
        Assert.Single(cart.Items);
        var item = cart.Items[0];
        Assert.Equal(1, item.ProductId);
        Assert.Equal("经典皮夹克", item.ProductName);
        Assert.Equal(189.99m, item.ProductPrice);
        Assert.Equal(2, item.Quantity);
        Assert.Equal("🧥", item.ProductEmoji);
    }

    [Fact]
    public async Task ShouldUpdateQuantity_WhenItemExists()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // 先添加商品
        var addResponse = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 1));
        addResponse.EnsureSuccessStatusCode();
        var addCart = await addResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(addCart);
        var itemId = addCart!.Items[0].Id;

        // 修改数量
        var updateResponse = await client.PutAsJsonAsync(
            $"/api/cart/{user}/items/{itemId}", new UpdateQuantityRequest(3));
        updateResponse.EnsureSuccessStatusCode();

        var cart = await updateResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.Single(cart!.Items);
        Assert.Equal(3, cart.Items[0].Quantity);
        Assert.Equal(1, cart.Items[0].ProductId);
    }

    [Fact]
    public async Task ShouldRemoveItem_WhenItemExists()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // 先添加商品
        var addResponse = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 1));
        addResponse.EnsureSuccessStatusCode();
        var addCart = await addResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(addCart);
        var itemId = addCart!.Items[0].Id;

        // 删除商品
        var deleteResponse = await client.DeleteAsync(
            $"/api/cart/{user}/items/{itemId}");
        deleteResponse.EnsureSuccessStatusCode();

        var cart = await deleteResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.Empty(cart!.Items);
    }

    [Fact]
    public async Task ShouldClearCart_WhenNotEmpty()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // 添加两个商品
        var r1 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 1));
        r1.EnsureSuccessStatusCode();
        var r2 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(2, 2));
        r2.EnsureSuccessStatusCode();

        // 清空购物车
        var clearResponse = await client.DeleteAsync($"/api/cart/{user}");
        clearResponse.EnsureSuccessStatusCode();

        var clearResult = await clearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("购物车已清空", clearResult.GetProperty("message").GetString());

        // 验证购物车已空
        var getResponse = await client.GetAsync($"/api/cart/{user}");
        getResponse.EnsureSuccessStatusCode();
        var cart = await getResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.Empty(cart!.Items);
    }

    [Fact]
    public async Task ShouldGetCart_WhenExists()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // 添加商品：2 件皮夹克 + 1 双跑鞋
        var p1 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 2));
        p1.EnsureSuccessStatusCode();

        var p2 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(3, 1));
        p2.EnsureSuccessStatusCode();

        // 获取购物车
        var response = await client.GetAsync($"/api/cart/{user}");
        response.EnsureSuccessStatusCode();

        var cart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.NotEqual(Guid.Empty, cart!.Id);
        Assert.Equal(2, cart.Items.Count);
        Assert.Equal(3, cart.TotalItems); // 2 + 1
        var expectedTotal = 2 * 189.99m + 129.99m;
        Assert.Equal(expectedTotal, cart.TotalPrice);
        Assert.True(cart.UpdatedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task ShouldGetSummary_WhenCartHasItems()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // 添加商品
        var p1 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 2));
        p1.EnsureSuccessStatusCode();
        var p2 = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(2, 1));
        p2.EnsureSuccessStatusCode();

        // 获取摘要
        var response = await client.GetAsync($"/api/cart/{user}/summary");
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<CartSummaryResponse>(JsonOptions);
        Assert.NotNull(summary);
        Assert.Equal(3, summary!.TotalItems); // 2 + 1
        var expectedTotal = 2 * 189.99m + 29.99m;
        Assert.Equal(expectedTotal, summary.TotalPrice);
        Assert.Equal(2, summary.Items.Length);
        Assert.Contains(summary.Items, s => s.Contains("经典皮夹克") && s.Contains("x2"));
        Assert.Contains(summary.Items, s => s.Contains("有机棉T恤") && s.Contains("x1"));
    }

    // ============ 错误场景 ============

    [Fact]
    public async Task ShouldReturn400_WhenProductNotFound()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        var response = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(999, 1));

        Assert.Equal(400, (int)response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("商品不存在", error!.Error);
    }

    [Fact]
    public async Task ShouldReturn400_WhenQuantityIsZeroOrNegative()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        // POST 数量为 0
        var postResponse = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 0));
        Assert.Equal(400, (int)postResponse.StatusCode);
        var postError = await postResponse.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(postError);
        Assert.Equal("数量必须大于0", postError!.Error);

        // 先加一个有效商品
        var addResponse = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(1, 1));
        addResponse.EnsureSuccessStatusCode();
        var addCart = await addResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        var itemId = addCart!.Items[0].Id;

        // PUT 数量为 0
        var putResponse = await client.PutAsJsonAsync(
            $"/api/cart/{user}/items/{itemId}", new UpdateQuantityRequest(0));
        Assert.Equal(400, (int)putResponse.StatusCode);
        var putError = await putResponse.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(putError);
        Assert.Equal("数量必须大于0", putError!.Error);
    }

    [Fact]
    public async Task ShouldReturn404_WhenItemNotInCart()
    {
        var user = NextUser();
        var client = CreateClientFor(user);
        var nonExistentItemId = Guid.NewGuid();

        var response = await client.DeleteAsync(
            $"/api/cart/{user}/items/{nonExistentItemId}");

        Assert.Equal(404, (int)response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("商品不在购物车中", error!.Error);
    }

    [Fact]
    public async Task ShouldReturn404_WhenUserNotFound()
    {
        var client = _factory.CreateClient();

        var getResponse = await client.GetAsync("/api/cart/nonexistent_user_xyz");
        Assert.Equal(404, (int)getResponse.StatusCode);

        var postResponse = await client.PostAsJsonAsync(
            "/api/cart/nonexistent_user_xyz/items", new AddItemRequest(1, 1));
        Assert.Equal(404, (int)postResponse.StatusCode);

        var putResponse = await client.PutAsJsonAsync(
            $"/api/cart/nonexistent_user_xyz/items/{Guid.NewGuid()}", new UpdateQuantityRequest(1));
        Assert.Equal(404, (int)putResponse.StatusCode);

        var delItemResponse = await client.DeleteAsync(
            $"/api/cart/nonexistent_user_xyz/items/{Guid.NewGuid()}");
        Assert.Equal(404, (int)delItemResponse.StatusCode);

        var delCartResponse = await client.DeleteAsync("/api/cart/nonexistent_user_xyz");
        Assert.Equal(404, (int)delCartResponse.StatusCode);
    }

    [Fact]
    public async Task ShouldReturn200_WhenClearingEmptyCart()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        var response = await client.DeleteAsync($"/api/cart/{user}");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("购物车已清空", result.GetProperty("message").GetString());
    }

    // ============ 边界场景 ============

    [Fact]
    public async Task DifferentUsers_CartsAreIsolated()
    {
        var userA = NextUser();
        var userB = NextUser();
        var client = CreateClientFor(userA);
        // userB 也会被自动创建（当需要时），但先确保它存在以测试间隔
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.Users.Any(u => u.Username == userB))
            {
                db.Users.Add(new User { Username = userB, DisplayName = userB });
                await db.SaveChangesAsync();
            }
        }

        // userA 添加商品
        var addResponse = await client.PostAsJsonAsync(
            $"/api/cart/{userA}/items", new AddItemRequest(1, 2));
        addResponse.EnsureSuccessStatusCode();

        // userB 的购物车应为空
        var userBCartResponse = await client.GetAsync($"/api/cart/{userB}");
        userBCartResponse.EnsureSuccessStatusCode();
        var userBCart = await userBCartResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(userBCart);
        Assert.Empty(userBCart!.Items);

        // userA 的购物车应有商品
        var userACartResponse = await client.GetAsync($"/api/cart/{userA}");
        userACartResponse.EnsureSuccessStatusCode();
        var userACart = await userACartResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(userACart);
        Assert.Single(userACart!.Items);
        Assert.Equal(2, userACart.Items[0].Quantity);
    }

    [Fact]
    public async Task FirstTimeUser_GetCart_ReturnsEmptyStructure()
    {
        var user = NextUser();
        var client = CreateClientFor(user);

        var response = await client.GetAsync($"/api/cart/{user}");
        response.EnsureSuccessStatusCode();

        var cart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.Equal(Guid.Empty, cart!.Id);
        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.TotalItems);
        Assert.Equal(0m, cart.TotalPrice);
    }
}
