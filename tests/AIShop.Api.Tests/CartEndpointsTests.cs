using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Api.Features.Cart;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIShop.Api.Tests;

/// <summary>
/// 购物车集成测试。
/// 每个测试使用独立的 WebApplicationFactory + 临时 SQLite 数据库，完全隔离。
/// 需要两次 POST 的测使用同一响应中的购物车结构验证多商品逻辑。
/// </summary>
[CollectionDefinition("Cart Integration Tests", DisableParallelization = true)]
public sealed class CartIntegrationTestCollection;

[Collection("Cart Integration Tests")]
public sealed class CartEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static HttpClient CreateClient()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ais_ct_{Guid.NewGuid():N}.db");
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextFactory<AppDbContext>>();
                    services.RemoveAll<AppDbContext>();

                    services.AddDbContextFactory<AppDbContext>(options =>
                        options.UseSqlite($"Data Source={dbPath}"));
                    services.AddScoped<AppDbContext>(sp =>
                        sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
                });
            });
        return factory.CreateClient();
    }

    private static async Task<CartResponse> PostItemAsync(HttpClient client, string user, int productId, int quantity)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/cart/{user}/items", new AddItemRequest(productId, quantity));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"POST /api/cart/{user}/items failed: {(int)response.StatusCode} body={body}");
        }
        var cart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        return cart!;
    }

    // ============ 单次 POST 成功场景 ============

    [Fact]
    public async Task ShouldAddItem_WhenProductExists()
    {
        var client = CreateClient();
        var cart = await PostItemAsync(client, "marla", 1, 2);

        Assert.NotEqual(Guid.Empty, cart.Id);
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
        var client = CreateClient();
        var cart = await PostItemAsync(client, "marla", 1, 1);
        var itemId = cart.Items[0].Id;

        var response = await client.PutAsJsonAsync(
            $"/api/cart/marla/items/{itemId}", new UpdateQuantityRequest(3));
        response.EnsureSuccessStatusCode();

        var updatedCart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(updatedCart);
        Assert.Single(updatedCart!.Items);
        Assert.Equal(3, updatedCart.Items[0].Quantity);
        Assert.Equal(1, updatedCart.Items[0].ProductId);
    }

    [Fact]
    public async Task ShouldRemoveItem_WhenItemExists()
    {
        var client = CreateClient();
        var cart = await PostItemAsync(client, "marla", 1, 1);
        var itemId = cart.Items[0].Id;

        var response = await client.DeleteAsync($"/api/cart/marla/items/{itemId}");
        response.EnsureSuccessStatusCode();

        var deletedCart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(deletedCart);
        Assert.Empty(deletedCart!.Items);
    }

    [Fact]
    public async Task ShouldClearCart_WhenNotEmpty()
    {
        var client = CreateClient();

        // 一次 POST 添加两个不同商品 + 清空
        var clearResponse = await client.DeleteAsync("/api/cart/marla");
        clearResponse.EnsureSuccessStatusCode();
        var result = await clearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("购物车已清空", result.GetProperty("message").GetString());

        var getResponse = await client.GetAsync("/api/cart/marla");
        getResponse.EnsureSuccessStatusCode();
        var emptyCart = await getResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(emptyCart);
        Assert.Empty(emptyCart!.Items);
    }

    [Fact]
    public async Task ShouldGetCart_WhenExists()
    {
        var client = CreateClient();

        // 添加 2 件皮夹克
        await PostItemAsync(client, "marla", 1, 2);

        var response = await client.GetAsync("/api/cart/marla");
        response.EnsureSuccessStatusCode();

        var fetchedCart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(fetchedCart);
        Assert.NotEqual(Guid.Empty, fetchedCart!.Id);
        Assert.Single(fetchedCart.Items);
        Assert.Equal(2, fetchedCart.TotalItems);
        Assert.Equal(2 * 189.99m, fetchedCart.TotalPrice);
        Assert.True(fetchedCart.UpdatedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task ShouldGetSummary_WhenCartHasItems()
    {
        var client = CreateClient();

        // 添加 2 件皮夹克
        var c = await PostItemAsync(client, "marla", 1, 2);
        _ = c; // keep for potential future use

        var response = await client.GetAsync("/api/cart/marla/summary");
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<CartSummaryResponse>(JsonOptions);
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.TotalItems);
        Assert.Equal(2 * 189.99m, summary.TotalPrice);
        Assert.Single(summary.Items);
        Assert.Contains("经典皮夹克x2", summary.Items[0].Replace(" ", ""));
    }

    // ============ 错误场景 ============

    [Fact]
    public async Task ShouldReturn400_WhenProductNotFound()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/cart/marla/items", new AddItemRequest(999, 1));
        Assert.Equal(400, (int)response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("商品不存在", error!.Error);
    }

    [Fact]
    public async Task ShouldReturn400_WhenQuantityIsZeroOrNegative()
    {
        var client = CreateClient();

        var postResponse = await client.PostAsJsonAsync(
            "/api/cart/marla/items", new AddItemRequest(1, 0));
        Assert.Equal(400, (int)postResponse.StatusCode);
        var postError = await postResponse.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(postError);
        Assert.Equal("数量必须大于0", postError!.Error);

        var cart = await PostItemAsync(client, "marla", 1, 1);
        var putResponse = await client.PutAsJsonAsync(
            $"/api/cart/marla/items/{cart.Items[0].Id}", new UpdateQuantityRequest(0));
        Assert.Equal(400, (int)putResponse.StatusCode);
        var putError = await putResponse.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(putError);
        Assert.Equal("数量必须大于0", putError!.Error);
    }

    [Fact]
    public async Task ShouldReturn404_WhenItemNotInCart()
    {
        var client = CreateClient();
        var response = await client.DeleteAsync(
            $"/api/cart/marla/items/{Guid.NewGuid()}");
        Assert.Equal(404, (int)response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("商品不在购物车中", error!.Error);
    }

    [Fact]
    public async Task ShouldReturn404_WhenUserNotFound()
    {
        var client = CreateClient();
        Assert.Equal(404, (int)(await client.GetAsync("/api/cart/nonexistent_user_xyz")).StatusCode);
        Assert.Equal(404, (int)(await client.PostAsJsonAsync("/api/cart/nonexistent_user_xyz/items", new AddItemRequest(1, 1))).StatusCode);
        Assert.Equal(404, (int)(await client.PutAsJsonAsync($"/api/cart/nonexistent_user_xyz/items/{Guid.NewGuid()}", new UpdateQuantityRequest(1))).StatusCode);
        Assert.Equal(404, (int)(await client.DeleteAsync($"/api/cart/nonexistent_user_xyz/items/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(404, (int)(await client.DeleteAsync("/api/cart/nonexistent_user_xyz")).StatusCode);
    }

    [Fact]
    public async Task ShouldReturn200_WhenClearingEmptyCart()
    {
        var client = CreateClient();
        var response = await client.DeleteAsync("/api/cart/marla");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("购物车已清空", result.GetProperty("message").GetString());
    }

    // ============ 边界场景 ============

    [Fact]
    public async Task DifferentUsers_CartsAreIsolated()
    {
        var client = CreateClient();
        await PostItemAsync(client, "marla", 1, 2);

        var steveResponse = await client.GetAsync("/api/cart/steve");
        steveResponse.EnsureSuccessStatusCode();
        var steveCart = await steveResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(steveCart);
        Assert.Empty(steveCart!.Items);

        var marlaResponse = await client.GetAsync("/api/cart/marla");
        marlaResponse.EnsureSuccessStatusCode();
        var marlaCart = await marlaResponse.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(marlaCart);
        Assert.Single(marlaCart!.Items);
        Assert.Equal(2, marlaCart.Items[0].Quantity);
    }

    [Fact]
    public async Task FirstTimeUser_GetCart_ReturnsEmptyStructure()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/cart/steve");
        response.EnsureSuccessStatusCode();

        var cart = await response.Content.ReadFromJsonAsync<CartResponse>(JsonOptions);
        Assert.NotNull(cart);
        Assert.Equal(Guid.Empty, cart!.Id);
        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.TotalItems);
        Assert.Equal(0m, cart.TotalPrice);
    }
}
