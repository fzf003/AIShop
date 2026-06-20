using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Api.Features.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Tests;

public sealed class ChatEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChatEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IChatClient>();

                var mock = Substitute.For<IChatClient>();
                var fakeResponse = new ChatResponse(
                    new ChatMessage(ChatRole.Assistant,
                        JsonSerializer.Serialize(new { Reply = "模拟回复", Keywords = new[] { "跑步" } })))
                {
                    ResponseId = "test",
                    FinishReason = ChatFinishReason.Stop
                };

                // IChatClient.GetResponseAsync has IEnumerable<ChatMessage> + ChatOptions? overload
                mock.GetResponseAsync(Arg.Any<IList<ChatMessage>>(),
                        Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                    .Returns(fakeResponse);

                services.AddSingleton(mock);
            });
        });
    }

    [Fact]
    public async Task Login_WithExistingUser_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/login",
            new LoginRequest("marla"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.Equal("marla", result!.Username);
        Assert.Equal("Marla", result.DisplayName);
    }

    [Fact]
    public async Task Login_WithNonExistentUser_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/login",
            new LoginRequest("nonexistent"));

        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithValidUser_ReturnsReply()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "Hello"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Equal("模拟回复", result!.Response);
    }

    [Fact]
    public async Task Chat_MessageGetsSavedToDb()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "测试保存"));

        var login = await client.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        Assert.Contains(profile!.History, m => m.Content == "测试保存" && m.Role == "user");
    }

    [Fact]
    public async Task Chat_WithValidKeywords_HasRecommendation()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "推荐跑步鞋"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.True(result!.HasRecommendation);
        Assert.NotEmpty(result.RecommendedProducts!);
    }

    [Fact]
    public async Task GetProducts_ReturnsAll()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProductsResponse>();
        Assert.NotNull(result);
        Assert.Equal(18, result!.products.Length);
    }

    [Fact]
    public async Task Recommendations_ReturnsResults()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑步"));

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.NotEmpty(result.Other);
    }

    [Fact]
    public async Task Login_ReturnsSessionWithExistingHistory()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "第一条"));

        var login = await client.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        var history = profile!.History;
        Assert.Contains(history, m => m.Content == "第一条" && m.Role == "user");
        Assert.Contains(history, m => m.Role == "assistant");
    }

    private sealed record ProductsResponse(ProductDto[] products);
}
