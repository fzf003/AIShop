using System.Net.Http.Json;
using System.Text.Json;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Api.Tests;

public sealed class ChatEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChatEndpointsTests(WebApplicationFactory<Program> factory)
    {
        WebApplicationFactory<Program>? newFactory = null;

        newFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                // Mock IChatClient returns JSON text — the real ShoppingAssistantAgent
                // pipeline (DeepSeekDelegatingChatClient → HarnessAgent →
                // SqliteChatHistoryProvider) runs fully, so history is persisted.
                var mockClient = Substitute.For<Meai.IChatClient>();
                const string jsonReply = "{\"Reply\":\"模拟回复\",\"Keywords\":[\"跑步\"],\"Preferences\":[]}";
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(
                        new Meai.ChatMessage(Meai.ChatRole.Assistant, jsonReply)));

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                // Store reference to the factory so mock router lambdas can
                // resolve services at request time (not during ConfigureServices).
                var capturedFactory = newFactory!;

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    var modelName = callInfo.Arg<string>();
                    if (modelName == "nonexistent")
                        throw new KeyNotFoundException("model not found");

                    // Agent created at request time from the fully-built SP —
                    // avoids Serilog "already frozen" from BuildServiceProvider().
                    var sp = capturedFactory.Services;
                    return new ShoppingAssistantAgent(
                        sp.GetRequiredService<Meai.IChatClient>(),
                        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        sp.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        sp.GetRequiredService<AgentTelemetryOptions>());
                });
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                    new ModelInfo("deepseek", "DeepSeek Chat", false),
                ]);

                services.AddSingleton(mockRouter);
            });
        });

        _factory = newFactory;
    }

    /// <summary>Isolate the DB per test class instance to avoid cross-test pollution.</summary>
    private static void ReplaceWithIsolatedDb(IServiceCollection services, string suffix)
    {
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();

        var connStr = $"Data Source=test_{suffix}.db";
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connStr));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        // 播种 18 商品：隔离库是全新空库，ProductRepository 改查库后 /products 从空表返回 0，
        // 必须在此建表 + 播入 ProductSeedData，否则 GetProducts_ReturnsAll 期望 18 实际 0。
        // 用独立 DbContextOptions 直接构造上下文播种（与 ProductRepositoryTests 同一模式），
        // 避免在 ConfigureServices 阶段 BuildServiceProvider() 触发 Serilog "already frozen"。
        using var seedCtx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options);
        seedCtx.Database.EnsureCreated();
        seedCtx.Products.AddRange(ProductSeedData.Products);
        seedCtx.SaveChanges();
    }

    [Fact]
    public async Task Login_WithExistingUser_ReturnsOk()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.Equal("marla", result!.Username);
        Assert.Equal("Marla", result.DisplayName);
    }

    [Fact]
    public async Task Login_WithNonExistentUser_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest("nonexistent"));
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithValidUser_ReturnsReply()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "Hello"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Contains("模拟回复", result!.Response);
    }

    [Fact]
    public async Task Chat_MessageGetsSavedToDb()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "测试保存"));

        using var loginClient = _factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        Assert.Contains(profile!.History, m => m.Content == "测试保存" && m.Role == "user");
    }

    [Fact]
    public async Task Chat_WithValidKeywords_HasRecommendation()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑步鞋"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.True(result!.HasRecommendation);
        Assert.NotEmpty(result.RecommendedProducts!);
    }

    [Fact]
    public async Task GetProducts_ReturnsAll()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/products");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProductsResponse>();
        Assert.NotNull(result);
        Assert.Equal(18, result!.products.Length);
    }

    /// <summary>
    /// R6 纯偏好驱动 — 无偏好时 /recommendations 走 All.Take(6) 兜底：
    /// BestMatch=null、Message=暂无特定推荐、Other 内容恒为 Id 1..6（shuffle 只变顺序不变内容）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnFallback_WhenNoPreference()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);                              // 无偏好 → 无最佳匹配
        Assert.Equal("暂无特定推荐 — 浏览精选商品", result.Message);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).OrderBy(x => x).ToArray()); // shuffle 只变顺序，内容仍为 All.Take(6)
    }

    [Fact]
    public async Task Login_ReturnsSessionWithExistingHistory()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "第一条"));

        using var loginClient = _factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        var history = profile!.History;
        Assert.Contains(history, m => m.Content == "第一条" && m.Role == "user");
        Assert.Contains(history, m => m.Role == "assistant");
    }

    [Fact]
    public async Task Agent_ShouldPreserveLast3Turns()
    {
        using var client = _factory.CreateClient();
        for (int i = 1; i <= 4; i++)
            await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", $"消息{i}"));

        using var loginClient = _factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);
        var userMsgs = profile!.History.Where(m => m.Role == "user").ToList();
        Assert.Contains(userMsgs, m => m.Content == "消息2");
        Assert.Contains(userMsgs, m => m.Content == "消息3");
        Assert.Contains(userMsgs, m => m.Content == "消息4");
    }

    /// <summary>
    /// R6 去缓存 — 两次 /recommendations 不依赖缓存也不调 LLM：
    /// 每次重新计算但内容一致（纯偏好驱动，无偏好时恒为 All.Take(6)），
    /// mock IChatClient 的 GetResponseAsync 全程未被调用（callCount==0）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnConsistentContent_WhenCalledTwice()
    {
        var callCount = 0;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var mockClient = Substitute.For<Meai.IChatClient>();
                const string json = "{\"Reply\":\"模拟推荐\",\"Keywords\":[\"运动\"],\"Preferences\":[]}";
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, json)); });

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = f!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent(Arg.Any<string>()).Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.ActiveModel.Returns("qwen");
                services.AddSingleton(mockRouter);
            });
        });
        using var client = f.CreateClient();
        var resp1 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp1.EnsureSuccessStatusCode();
        var resp2 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp2.EnsureSuccessStatusCode();
        var r1 = await resp1.Content.ReadFromJsonAsync<RecommendationResponse>();
        var r2 = await resp2.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(r1); Assert.NotNull(r2);
        // 无偏好 → 两次都是兜底，内容一致（shuffle 只变顺序不变内容）
        Assert.Null(r1!.BestMatch);
        Assert.Null(r2!.BestMatch);
        Assert.Equal(
            r1.Other.Select(p => p.Id).OrderBy(x => x).ToArray(),
            r2.Other.Select(p => p.Id).OrderBy(x => x).ToArray());
        Assert.Equal([1, 2, 3, 4, 5, 6], r1.Other.Select(p => p.Id).OrderBy(x => x).ToArray());
        Assert.Equal(r1.Message, r2.Message);
        // /recommendations 纯偏好驱动，全程不调 LLM（无缓存命中/未命中之分）
        Assert.Equal(0, callCount);
    }

    /// <summary>
    /// R6 纯偏好驱动 — 推荐不随消息变化：
    /// 两条不同消息的 /chat 后，/recommendations 内容一致（无偏好时恒为 All.Take(6)），
    /// 且 LLM 调用数仅来自 /chat（callCount==2），/recommendations 不再因新消息触发重算/缓存失效。
    /// </summary>
    [Fact]
    public async Task ShouldNotChangeWithMessage_WhenPreferenceDriven()
    {
        var callCount = 0;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var mockClient = Substitute.For<Meai.IChatClient>();
                const string json = "{\"Reply\":\"推荐\",\"Keywords\":[\"运动\"],\"Preferences\":[]}";
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(_ => { callCount++; return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, json)); });

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = f!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent(Arg.Any<string>()).Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.ActiveModel.Returns("qwen");
                services.AddSingleton(mockRouter);
            });
        });
        using var client = f.CreateClient();
        var chat1 = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐运动鞋"));
        chat1.EnsureSuccessStatusCode();
        var chat2 = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐耳机"));
        chat2.EnsureSuccessStatusCode();
        Assert.Equal(2, callCount);   // 两次 /chat 各调一次 LLM

        var resp1 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp1.EnsureSuccessStatusCode();
        var resp2 = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        resp2.EnsureSuccessStatusCode();
        var r1 = await resp1.Content.ReadFromJsonAsync<RecommendationResponse>();
        var r2 = await resp2.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(r1); Assert.NotNull(r2);
        // 不同消息后 /recommendations 内容一致（纯偏好驱动，不随消息变化）
        Assert.Equal(
            r1!.Other.Select(p => p.Id).OrderBy(x => x).ToArray(),
            r2!.Other.Select(p => p.Id).OrderBy(x => x).ToArray());
        Assert.Equal(r1.Message, r2.Message);
        // /recommendations 不再触发 LLM：callCount 仍为 2（仅 /chat 贡献）
        Assert.Equal(2, callCount);
    }

    // ============ Multi-Model Tests ============

    [Fact]
    public async Task GetModels_ReturnsModelInfoWithCorrectFields()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(models);
        Assert.NotEmpty(models);
        foreach (var model in models!)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id), "Id 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(model.Name), "Name 不应为空");
        }
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            Assert.False(element.TryGetProperty("key", out _), "应不包含 key 字段");
            Assert.False(element.TryGetProperty("Key", out _), "应不包含 Key 字段");
            Assert.False(element.TryGetProperty("endpoint", out _), "应不包含 endpoint 字段");
            Assert.False(element.TryGetProperty("Endpoint", out _), "应不包含 Endpoint 字段");
        }
        var defaultModels = models!.Where(m => m.IsDefault).ToList();
        Assert.Single(defaultModels);
        Assert.Equal("qwen", defaultModels[0].Id);
    }

    [Fact]
    public async Task GetModels_ReturnsAvailableModels()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(models);
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task Chat_WithNonExistentModel_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "Hello", "nonexistent"));
        Assert.Equal(400, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("不支持的模型", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Login_ResponseContainsModels()
    {
        using var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginResult);
        Assert.NotNull(loginResult!.Models);
        Assert.NotEmpty(loginResult.Models);

        var modelsResponse = await client.GetAsync("/api/models");
        modelsResponse.EnsureSuccessStatusCode();
        var modelsList = await modelsResponse.Content.ReadFromJsonAsync<List<ModelInfo>>();
        Assert.NotNull(modelsList);

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
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var mockClient = Substitute.For<Meai.IChatClient>();
                const string json = "{\"Reply\":\"默认模型回复\",\"Keywords\":[\"测试\"],\"Preferences\":[]}";
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, json)));

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = f!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    usedModel = callInfo.Arg<string>();
                    return new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>());
                });
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                ]);
                services.AddSingleton(mockRouter);
            });
        });
        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "测试默认模型路由"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Contains("默认模型回复", result!.Response);
        Assert.Equal("qwen", usedModel);
    }

    [Fact]
    public async Task Chat_WithModelParameter_RoutesToCorrectAgent()
    {
        string? usedModel = null;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var mockClient = Substitute.For<Meai.IChatClient>();
                const string json = "{\"Reply\":\"GPT-4.1 推荐跑鞋\",\"Keywords\":[\"跑步\"],\"Preferences\":[]}";
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, json)));

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = f!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
                    usedModel = callInfo.Arg<string>();
                    return new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>());
                });
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                ]);
                services.AddSingleton(mockRouter);
            });
        });
        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "推荐跑鞋", "gpt-4.1"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(result);
        Assert.Contains("GPT-4.1 推荐跑鞋", result!.Response);
        Assert.Equal("gpt-4.1", usedModel);
    }

    [Fact]
    public async Task History_IsPreserved_WhenSwitchingModels()
    {
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                // Qwen pipeline — mock IChatClient returning "这是 Qwen 回复"
                var qwenClient = Substitute.For<Meai.IChatClient>();
                const string qwenJson = "{\"Reply\":\"这是 Qwen 回复\",\"Keywords\":[\"测试\"],\"Preferences\":[]}";
                qwenClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, qwenJson)));
                var qwenPipe = new DeepSeekDelegatingChatClient(qwenClient, null, "qwen");

                // GPT pipeline — mock IChatClient returning "这是 GPT 回复"
                var gptClient = Substitute.For<Meai.IChatClient>();
                const string gptJson = "{\"Reply\":\"这是 GPT 回复\",\"Keywords\":[\"测试\"],\"Preferences\":[]}";
                gptClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, gptJson)));
                var gptPipe = new DeepSeekDelegatingChatClient(gptClient, null, "gpt-4.1");

                // Register both pipelines as keyed singletons
                services.AddKeyedSingleton<Meai.IChatClient>("qwen", qwenPipe);
                services.AddKeyedSingleton<Meai.IChatClient>("gpt-4.1", gptPipe);

                var capturedFactory = f!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.GetAgent("qwen").Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredKeyedService<Meai.IChatClient>("qwen"),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.GetAgent("gpt-4.1").Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredKeyedService<Meai.IChatClient>("gpt-4.1"),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredKeyedService<Meai.IChatClient>("qwen"),
                        capturedFactory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                        ProductKeywordMap.Entries,
                        capturedFactory.Services.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        capturedFactory.Services.GetRequiredService<AgentTelemetryOptions>()));
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAvailableModels().Returns([
                    new ModelInfo("qwen", "Qwen 3.7", true),
                    new ModelInfo("gpt-4.1", "GPT 4.1", false),
                ]);
                services.AddSingleton(mockRouter);
            });
        });
        using var client = f.CreateClient();

        var resp1 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "Qwen 帮我推荐跑鞋", "qwen"));
        resp1.EnsureSuccessStatusCode();

        var resp2 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "GPT 推荐耳机", "gpt-4.1"));
        resp2.EnsureSuccessStatusCode();

        using var loginClient = f.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        loginResponse.EnsureSuccessStatusCode();
        var profile = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);

        var userMsgs = profile!.History.Where(m => m.Role == "user").ToList();
        Assert.Contains(userMsgs, m => m.Content == "Qwen 帮我推荐跑鞋");
        Assert.Contains(userMsgs, m => m.Content == "GPT 推荐耳机");

        var assistantMsgs = profile.History.Where(m => m.Role == "assistant").ToList();
        Assert.Contains(assistantMsgs, m => m.Content == "这是 Qwen 回复");
        Assert.Contains(assistantMsgs, m => m.Content == "这是 GPT 回复");
    }

    private sealed record ProductsResponse(ProductDto[] products);
}
