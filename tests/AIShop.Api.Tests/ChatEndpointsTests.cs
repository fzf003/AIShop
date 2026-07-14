using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

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
                services.RemoveAll<IShoppingAssistantAgent>();

                var mock = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("模拟回复", ["跑步"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());

                mock.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns((fakeResult, fakeSession));

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

    [Fact]
    public async Task Agent_ShouldPreserveLast3Turns()
    {
        var client = _factory.CreateClient();
        for (int i = 1; i <= 4; i++)
            await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", $"消息{i}"));

        var login = await client.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        var userMsgs = profile!.History.Where(m => m.Role == "user").ToList();
        Assert.Contains(userMsgs, m => m.Content == "消息2");
        Assert.Contains(userMsgs, m => m.Content == "消息3");
        Assert.Contains(userMsgs, m => m.Content == "消息4");
    }

    [Fact]
    public async Task Recommendations_SecondRequest_ReturnsCachedResult()
    {
        var callCount = 0;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IShoppingAssistantAgent>();
                var mock = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("模拟推荐", ["运动"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());
                mock.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return (fakeResult, fakeSession); });
                services.AddSingleton(mock);
            });
        });
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐运动"));
        var resp1 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp1.EnsureSuccessStatusCode();
        var resp2 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp2.EnsureSuccessStatusCode();
        var r1 = await resp1.Content.ReadFromJsonAsync<RecommendationResponse>();
        var r2 = await resp2.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(r1); Assert.NotNull(r2);
        Assert.Equal(r1!.BestMatch?.Id, r2!.BestMatch?.Id);
        Assert.Equal(r1.Message, r2.Message);
    }

    [Fact]
    public async Task Recommendations_NewMessage_InvalidatesCache()
    {
        var callCount = 0;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IShoppingAssistantAgent>();
                var mock = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("推荐", ["运动"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());
                mock.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return (fakeResult, fakeSession); });
                services.AddSingleton(mock);
            });
        });
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐运动鞋"));
        await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        var firstCalls = callCount;
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐耳机"));
        await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        Assert.True(callCount > firstCalls,
            "新消息应使缓存失效，导致 Agent 重新被调用");
    }

    private sealed record ProductsResponse(ProductDto[] products);

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
