using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Service;
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
    /// R7 — 无对话历史且无偏好时，/recommendations 走 All.Take(6) 精选兜底：
    /// BestMatch=null、Message=为您精选商品（非「暂无特定推荐」）、Other 内容恒为 Id 1..6（固定顺序，无 shuffle）。
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
        Assert.Null(result!.BestMatch);                              // 无对话历史 + 无偏好 → 无最佳匹配
        Assert.Equal("为您精选商品", result.Message);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).ToArray()); // 固定顺序（去 shuffle），可精确断言
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
    /// R7 无随机 — 两次 /recommendations 结果完全一致（确定性，无 shuffle）：
    /// 无对话历史 + 无偏好 → 每次都是「为您精选商品」兜底，Other 顺序逐次相同（Id 1..6），
    /// mock IChatClient 的 GetResponseAsync 全程未被调用（callCount==0，不调 LLM）。
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
        // 无对话历史 + 无偏好 → 两次都是「为您精选商品」兜底，内容与顺序一致（去 shuffle 确定性）
        Assert.Null(r1!.BestMatch);
        Assert.Null(r2!.BestMatch);
        Assert.Equal(
            r1.Other.Select(p => p.Id).ToArray(),
            r2.Other.Select(p => p.Id).ToArray());
        Assert.Equal([1, 2, 3, 4, 5, 6], r1.Other.Select(p => p.Id).ToArray());
        Assert.Equal(r1.Message, r2.Message);
        // /recommendations 全程不调 LLM（无缓存命中/未命中之分）
        Assert.Equal(0, callCount);
    }

    /// <summary>
    /// R8 随聊天变化 — /recommendations 推荐随最新聊天产物实时变化（/chat 每次重写快照缓存，不调 LLM）：
    /// /chat「推荐咖啡机」→ 快照=咖啡机 → reco1 BestMatch 意式浓缩咖啡机（Id 5）；
    /// 再 /chat「你好」（mock Agent 语义回退 Keywords=["运动"]）→ /chat 重写快照=运动推荐 →
    /// reco2 BestMatch 专业跑鞋（Id 3）。两次 /recommendations 均命中各自快照缓存、
    /// 结果随聊天变化（5 → 3）；LLM 调用数仅来自 /chat（callCount==2），/recommendations 不触发。
    /// </summary>
    [Fact]
    public async Task ShouldFollowLatestMessage_WhenMessageChanges()
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

        // 消息 1：「推荐咖啡机」→ /chat 字面命中「咖啡」→ 快照 BestMatch=意式浓缩咖啡机 Id 5
        var chat1 = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐咖啡机"));
        chat1.EnsureSuccessStatusCode();
        var reco1Resp = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        reco1Resp.EnsureSuccessStatusCode();
        var reco1 = await reco1Resp.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(reco1);
        Assert.NotNull(reco1!.BestMatch);
        Assert.Equal(5, reco1.BestMatch!.Id); // 意式浓缩咖啡机（消息「咖啡」命中）
        Assert.Equal("根据您的兴趣，为您推荐：", reco1.Message);

        // 消息 2：「你好」→ /chat 字面无命中、Agent 语义回退 Keywords=["运动"] → /chat 重写快照为运动推荐
        var chat2 = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chat2.EnsureSuccessStatusCode();
        var reco2Resp = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        reco2Resp.EnsureSuccessStatusCode();
        var reco2 = await reco2Resp.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(reco2);
        Assert.NotNull(reco2!.BestMatch);
        Assert.Equal(3, reco2.BestMatch!.Id); // 专业跑鞋（「运动」命中，运动推荐首项）
        Assert.Equal("根据您的兴趣，为您推荐：", reco2.Message);

        // 推荐随最新聊天变化（R8 核心语义）：/chat 每次重写快照，推荐栏镜像最新聊天产物（5 → 3）
        Assert.NotEqual(reco1.BestMatch!.Id, reco2.BestMatch!.Id);
        // /recommendations 不调 LLM：callCount 仍为 2（仅两次 /chat 贡献）
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

    /// <summary>
    /// R11 — /api/chat Agent 抛异常：catch 块把被吞异常写进 OTel span——
    /// 请求 Activity（AddAspNetCoreInstrumentation 创建）置 Error 状态、产生 "exception" 事件，
    /// 响应仍为兜底「抱歉，暂时无法处理您的请求，请重试。」。
    /// </summary>
    [Fact]
    public async Task Chat_WhenAgentThrows_SetsActivityErrorAndReturnsFallback()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var mockAgent = Substitute.For<IShoppingAssistantAgent>();
                mockAgent.RunChatAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<(AgentChatResult, Microsoft.Agents.AI.AgentSession)>>(
                        _ => throw new InvalidOperationException("agent boom"));

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(mockAgent);
                mockRouter.GetDefaultAgent().Returns(mockAgent);
                services.AddSingleton(mockRouter);
            }));

        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("抱歉，暂时无法处理您的请求", reply!.Response);

        // 请求 span 被 catch 置 Error + exception 事件（被吞异常进 OTel）。
        // 宿主 Activity 在响应返回后毫秒级 Stop，轮询等待避免时序竞态。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        Activity? errorSpan = null;
        while (errorSpan is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            errorSpan = captured.FirstOrDefault(a => a.Status == ActivityStatusCode.Error);
        }
        Assert.NotNull(errorSpan);
        Assert.Contains("agent boom", errorSpan!.StatusDescription);
        Assert.Contains(errorSpan.Events, e => e.Name == "exception");
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

    // ============ T12 重试分类器测试 ============

    /// <summary>
    /// T12 分类器基础判定：网络/超时/429/5xx → true；确定性失败 → false。
    /// 用无参构造各异常类型（均有公共无参构造），ct 取未取消令牌。
    /// </summary>
    [Theory]
    [InlineData(typeof(HttpRequestException), true)]      // 网络抖动 → 可重试
    [InlineData(typeof(TimeoutException), true)]          // 超时 → 可重试
    [InlineData(typeof(InvalidOperationException), false)] // 确定性失败 → 不重试
    [InlineData(typeof(KeyNotFoundException), false)]     // 模型不存在 → 不落入分类器（T13 独立 catch 优先）
    public void IsRetryableAgentFailure_ClassifiesByExceptionType(Type exceptionType, bool expected)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Equal(expected, ChatEndpoints.IsRetryableAgentFailure(ex, CancellationToken.None));
    }

    /// <summary>
    /// T12 TaskCanceledException 语义：调用方 ct 未取消时视为 HttpClient 超时（内部超时触发）→ 可重试。
    /// </summary>
    [Fact]
    public void IsRetryableAgentFailure_TaskCanceledWithoutCallerCancellation_IsTrue()
    {
        var ex = new TaskCanceledException();
        Assert.True(ChatEndpoints.IsRetryableAgentFailure(ex, CancellationToken.None));
    }

    /// <summary>
    /// T12 TaskCanceledException 语义：调用方 ct 已取消时是调用方主动取消，非可重试失败 → false。
    /// </summary>
    [Fact]
    public void IsRetryableAgentFailure_TaskCanceledByCallerToken_IsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new TaskCanceledException();
        Assert.False(ChatEndpoints.IsRetryableAgentFailure(ex, cts.Token));
    }

    /// <summary>T12 OpenAI/Qwen 路径 ClientResultException：429（限流）→ 可重试。</summary>
    [Fact]
    public void IsRetryableAgentFailure_ClientResult429_IsTrue()
    {
        var ex = new ClientResultException(new StubPipelineResponse(429), null);
        Assert.True(ChatEndpoints.IsRetryableAgentFailure(ex, CancellationToken.None));
    }

    /// <summary>T12 OpenAI/Qwen 路径 ClientResultException：5xx（服务端错误）→ 可重试。</summary>
    [Fact]
    public void IsRetryableAgentFailure_ClientResult503_IsTrue()
    {
        var ex = new ClientResultException(new StubPipelineResponse(503), null);
        Assert.True(ChatEndpoints.IsRetryableAgentFailure(ex, CancellationToken.None));
    }

    /// <summary>T12 OpenAI/Qwen 路径 ClientResultException：4xx 非 429（如 400 请求错误）→ 不重试。</summary>
    [Fact]
    public void IsRetryableAgentFailure_ClientResult400_IsFalse()
    {
        var ex = new ClientResultException(new StubPipelineResponse(400), null);
        Assert.False(ChatEndpoints.IsRetryableAgentFailure(ex, CancellationToken.None));
    }

    /// <summary>
    /// T12 测试辅助：构造 <see cref="ClientResultException"/> 所需的最小 <see cref="PipelineResponse"/> 桩。
    /// 该版本的 ClientResultException 通过 PipelineResponse.Status 暴露状态码（无 (int, Response) 构造），
    /// 用显式子类而非 NSubstitute（Headers 为 protected 抽象，mock 设置繁琐）。
    /// </summary>
    private sealed class StubPipelineResponse(int status) : PipelineResponse
    {
        public override int Status => status;
        public override string ReasonPhrase => "";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.Empty;
        public override bool IsError => status >= 400;
        public override void Dispose() { }
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(BinaryData.Empty);
        protected override PipelineResponseHeaders HeadersCore => new StubHeaders();
        protected override bool IsErrorCore => status >= 400;

        private sealed class StubHeaders : PipelineResponseHeaders
        {
            public override bool TryGetValue(string name, out string value) { value = ""; return false; }
            public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() { yield break; }
        }
    }

    // ============ T13T1 重试路径测试（方案 A：mock HttpRequestException） ============

    /// <summary>
    /// T13T1 — mock RunChatAsync 首次抛 HttpRequestException（网络抖动，IsRetryableAgentFailure→true）、
    /// 第二次返回正常回复 → /api/chat 响应为重试后的正常回复、RunChatAsync 恰被调用 2 次
    /// （重试成功路径：首次失败仅 Warning，重试成功用重试结果，不污染 span）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnRetriedReply_WhenFirstRunChatCallThrowsHttpRequestException()
    {
        IShoppingAssistantAgent? mockAgent = null;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var agent = Substitute.For<IShoppingAssistantAgent>();
                // NSubstitute 回调队列：第 1 次抛 HttpRequestException，第 2 次返回正常回复
                agent.RunChatAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<(AgentChatResult, Microsoft.Agents.AI.AgentSession)>>(
                        _ => throw new HttpRequestException("网络抖动"),
                        _ => Task.FromResult(
                            (new AgentChatResult("重试成功回复", [], null),
                             Substitute.For<Microsoft.Agents.AI.AgentSession>())));
                mockAgent = agent;

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(agent);
                mockRouter.GetDefaultAgent().Returns(agent);
                services.AddSingleton(mockRouter);
            }));

        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("重试成功回复", reply!.Response);

        // 重试成功路径：RunChatAsync 恰好被调用 2 次（首次失败 + 一次重试）
        await mockAgent!.Received(2).RunChatAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// T13T1 — mock RunChatAsync 两次都抛 HttpRequestException → 重试也失败才兜底：
    /// /api/chat 响应为「抱歉，暂时无法处理您的请求，请重试。」、RunChatAsync 恰被调用 2 次
    /// （首次失败 + 重试失败，重试失败才 OTel Error + 兜底返回）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnFallback_WhenRetryAlsoThrowsHttpRequestException()
    {
        IShoppingAssistantAgent? mockAgent = null;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var agent = Substitute.For<IShoppingAssistantAgent>();
                // 第 1 次与第 2 次（重试）均抛 HttpRequestException
                agent.RunChatAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<(AgentChatResult, Microsoft.Agents.AI.AgentSession)>>(
                        _ => throw new HttpRequestException("网络抖动 1"),
                        _ => throw new HttpRequestException("网络抖动 2"));
                mockAgent = agent;

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(agent);
                mockRouter.GetDefaultAgent().Returns(agent);
                services.AddSingleton(mockRouter);
            }));

        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("抱歉，暂时无法处理您的请求", reply!.Response);

        // 兜底路径：RunChatAsync 恰好被调用 2 次（首次失败 + 一次重试，重试仍失败才兜底）
        await mockAgent!.Received(2).RunChatAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ============ T13T2 非重试异常不重试测试（方案 A：确定性失败） ============

    /// <summary>
    /// T13T2 — mock RunChatAsync 抛 InvalidOperationException（非重试类型，IsRetryableAgentFailure→false）：
    /// /api/chat 响应为兜底「抱歉，暂时无法处理您的请求，请重试。」、RunChatAsync 只被调用 1 次
    /// （确定性失败不重试，保持既有 R11 行为，不进入 IsRetryableAgentFailure 重试分支）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnFallback_WhenRunChatThrowsInvalidOperationException()
    {
        IShoppingAssistantAgent? mockAgent = null;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var agent = Substitute.For<IShoppingAssistantAgent>();
                agent.RunChatAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<(AgentChatResult, Microsoft.Agents.AI.AgentSession)>>(
                        _ => throw new InvalidOperationException("agent boom"));
                mockAgent = agent;

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(agent);
                mockRouter.GetDefaultAgent().Returns(agent);
                services.AddSingleton(mockRouter);
            }));

        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("抱歉，暂时无法处理您的请求", reply!.Response);

        // 非重试异常：RunChatAsync 只被调用 1 次（不重试）
        await mockAgent!.Received(1).RunChatAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// T13T2 — mock RunChatAsync 抛 KeyNotFoundException（模型不存在）：
    /// /api/chat 响应为 400「不支持的模型」、RunChatAsync 只被调用 1 次
    /// （T13 独立 catch 优先，不被重试逻辑覆盖，对应方案 A「KeyNotFoundException 不重试」）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequest_WhenRunChatThrowsKeyNotFoundException()
    {
        IShoppingAssistantAgent? mockAgent = null;
        WebApplicationFactory<Program>? f = null;
        f = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, Guid.NewGuid().ToString("N"));
                services.RemoveAll<ModelRouter>();

                var agent = Substitute.For<IShoppingAssistantAgent>();
                agent.RunChatAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<(AgentChatResult, Microsoft.Agents.AI.AgentSession)>>(
                        _ => throw new KeyNotFoundException("model not found"));
                mockAgent = agent;

                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(agent);
                mockRouter.GetDefaultAgent().Returns(agent);
                services.AddSingleton(mockRouter);
            }));

        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        Assert.Equal(400, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("不支持的模型", body.GetProperty("detail").GetString());

        // 独立 catch 优先：RunChatAsync 只被调用 1 次（不重试）
        await mockAgent!.Received(1).RunChatAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private sealed record ProductsResponse(ProductDto[] products);
}
