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
                services.RemoveAll<ModelRouter>();

                var mockRouter = Substitute.For<ModelRouter>();
                var mockAgent = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("模拟回复", ["跑步"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());

                mockAgent.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns((fakeResult, fakeSession));

                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    var modelName = callInfo.Arg<string>();
                    if (modelName == "nonexistent")
                        throw new KeyNotFoundException("model not found");
                    return mockAgent;
                });
                mockRouter.GetDefaultAgent().Returns(mockAgent);
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                    new ModelInfo("deepseek", "DeepSeek Chat", false),
                ]);

                services.AddSingleton(mockRouter);
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
                services.RemoveAll<ModelRouter>();
                var mockRouter = Substitute.For<ModelRouter>();
                var mock = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("模拟推荐", ["运动"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());
                mock.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return (fakeResult, fakeSession); });
                mockRouter.GetAgent(Arg.Any<string>()).Returns(mock);
                mockRouter.GetDefaultAgent().Returns(mock);
                mockRouter.ActiveModel.Returns("qwen");
                services.AddSingleton(mockRouter);
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
                services.RemoveAll<ModelRouter>();
                var mockRouter = Substitute.For<ModelRouter>();
                var mock = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("推荐", ["运动"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());
                mock.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return (fakeResult, fakeSession); });
                mockRouter.GetAgent(Arg.Any<string>()).Returns(mock);
                mockRouter.GetDefaultAgent().Returns(mock);
                mockRouter.ActiveModel.Returns("qwen");
                services.AddSingleton(mockRouter);
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

    // ============ Multi-Model Tests ============

    [Fact]
    public async Task GetModels_ReturnsModelInfoWithCorrectFields()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");

        // 验证响应 200
        response.EnsureSuccessStatusCode();

        // 验证可反序列化为 List<ModelInfo>
        var models = await response.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(models);
        Assert.NotEmpty(models);

        // 验证每个元素有 id/name/isDefault 字段
        foreach (var model in models!)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id), "Id 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(model.Name), "Name 不应为空");
        }

        // 验证 JSON 不包含 Key/Endpoint 等敏感字段
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            Assert.False(element.TryGetProperty("key", out _), "应不包含 key 字段");
            Assert.False(element.TryGetProperty("Key", out _), "应不包含 Key 字段");
            Assert.False(element.TryGetProperty("endpoint", out _), "应不包含 endpoint 字段");
            Assert.False(element.TryGetProperty("Endpoint", out _), "应不包含 Endpoint 字段");
        }

        // 验证 isDefault: true 的模型与 ActiveModel 配置一致
        var defaultModels = models!.Where(m => m.IsDefault).ToList();
        Assert.Single(defaultModels);
        Assert.Equal("qwen", defaultModels[0].Id);
    }

    [Fact]
    public async Task GetModels_ReturnsAvailableModels()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(models);
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task Chat_WithNonExistentModel_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "Hello", "nonexistent"));
        Assert.Equal(400, (int)response.StatusCode);

        // 验证错误信息包含"不支持的模型"
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("不支持的模型", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Login_ResponseContainsModels()
    {
        var client = _factory.CreateClient();

        // Act: POST /api/login
        var loginResponse = await client.PostAsJsonAsync("/api/login",
            new LoginRequest("marla"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginResult);
        Assert.NotNull(loginResult!.Models);
        Assert.NotEmpty(loginResult.Models);

        // Act: GET /api/models
        var modelsResponse = await client.GetAsync("/api/models");
        modelsResponse.EnsureSuccessStatusCode();
        var modelsList = await modelsResponse.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(modelsList);

        // Assert: login response models match GET /api/models (count, id, name)
        Assert.Equal(modelsList!.Count, loginResult.Models.Count);
        foreach (var expected in modelsList)
        {
            var actual = loginResult.Models.FirstOrDefault(m => m.Id == expected.Id);
            Assert.NotNull(actual);
            Assert.Equal(expected.Name, actual!.Name);
        }
    }

    [Fact]
    public async Task Chat_WithoutModel_UsesDefaultModel()
    {
        string? usedModel = null;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelRouter>();

                var mockRouter = Substitute.For<ModelRouter>();
                var mockAgent = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("默认模型回复", ["测试"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());

                mockAgent.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns((fakeResult, fakeSession));

                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    usedModel = callInfo.Arg<string>();
                    return mockAgent;
                });
                mockRouter.GetDefaultAgent().Returns(mockAgent);
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                ]);

                services.AddSingleton(mockRouter);
            });
        });
        var client = factory.CreateClient();

        // Act: send chat request without model parameter
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "测试默认模型路由"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Equal("默认模型回复", result!.Response);

        // Assert: routed through ActiveModel = "qwen"
        Assert.Equal("qwen", usedModel);
    }

    [Fact]
    public async Task Chat_WithModelParameter_RoutesToCorrectAgent()
    {
        string? usedModel = null;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelRouter>();

                var mockRouter = Substitute.For<ModelRouter>();
                var mockAgent = Substitute.For<IShoppingAssistantAgent>();
                var fakeResult = new AgentChatResult("GPT-4.1 推荐跑鞋", ["跑步"], null);
                var fakeSession = new TestSession();
                fakeSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());

                mockAgent.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns((fakeResult, fakeSession));

                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    usedModel = callInfo.Arg<string>();
                    return mockAgent;
                });
                mockRouter.GetDefaultAgent().Returns(mockAgent);
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                ]);

                services.AddSingleton(mockRouter);
            });
        });
        var client = factory.CreateClient();

        // Act: send chat request with model="gpt-4.1"
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "推荐跑鞋", "gpt-4.1"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Equal("GPT-4.1 推荐跑鞋", result!.Response);

        // Assert: routed to gpt-4.1 agent
        Assert.Equal("gpt-4.1", usedModel);
    }

    [Fact]
    public async Task History_IsPreserved_WhenSwitchingModels()
    {
        // Arrange: create mock router with two separate agents
        var qwenResult = new AgentChatResult("这是 Qwen 回复", ["测试"], null);
        var gptResult = new AgentChatResult("这是 GPT 回复", ["测试"], null);
        var qwenSession = new TestSession();
        var gptSession = new TestSession();
        qwenSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());
        gptSession.StateBag.SetValue("SessionId", Guid.NewGuid().ToString());


        var mockQwenAgent = Substitute.For<IShoppingAssistantAgent>();
        mockQwenAgent.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((qwenResult, qwenSession));

        var mockGptAgent = Substitute.For<IShoppingAssistantAgent>();
        mockGptAgent.RunChatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((gptResult, gptSession));

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelRouter>();

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent("qwen").Returns(mockQwenAgent);
                mockRouter.GetAgent("gpt-4.1").Returns(mockGptAgent);
                mockRouter.GetDefaultAgent().Returns(mockQwenAgent);
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                ]);

                services.AddSingleton(mockRouter);
            });
        });
        var client = factory.CreateClient();

        // Act 1: send chat with model="qwen"
        var resp1 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "Qwen 帮我推荐跑鞋", "qwen"));
        resp1.EnsureSuccessStatusCode();

        // Act 2: send chat with model="gpt-4.1" (same user → same sessionId)
        var resp2 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "GPT 推荐耳机", "gpt-4.1"));
        resp2.EnsureSuccessStatusCode();

        // Act 3: login to get full history
        var loginResponse = await client.PostAsJsonAsync("/api/login",
            new LoginRequest("marla"));
        loginResponse.EnsureSuccessStatusCode();
        var profile = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);

        // Assert: history contains user messages from both conversations
        var userMsgs = profile!.History.Where(m => m.Role == "user").ToList();
        Assert.Contains(userMsgs, m => m.Content == "Qwen 帮我推荐跑鞋");
        Assert.Contains(userMsgs, m => m.Content == "GPT 推荐耳机");

        // Assert: history contains assistant responses from both conversations
        var assistantMsgs = profile.History.Where(m => m.Role == "assistant").ToList();
        Assert.Contains(assistantMsgs, m => m.Content == "这是 Qwen 回复");
        Assert.Contains(assistantMsgs, m => m.Content == "这是 GPT 回复");
    }

    private sealed record ProductsResponse(ProductDto[] products);

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
