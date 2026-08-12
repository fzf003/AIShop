#pragma warning disable MAAI001
using System.Net.Http.Json;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Api.Tests;

/// <summary>
/// T21 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争。
/// </summary>
[CollectionDefinition(nameof(ChatRecommendationsMergeTests), DisableParallelization = true)]
public sealed class ChatRecommendationsMergeTestsCollection;

/// <summary>
/// T21 — /recommendations 复用推荐合并逻辑（design 4.3 / P2-8）。
/// 偏好注入在端点层：/recommendations 的缓存命中路径不调 RunChatAsync，
/// Agent 层 StateBag 偏好回填仅 /chat 生效，因此端点加载偏好后用
/// RecommendationMerger.MergeKeywords 合并出推荐，与 /chat 口径一致。
/// </summary>
[Collection(nameof(ChatRecommendationsMergeTests))]
public sealed class ChatRecommendationsMergeTests : IDisposable
{
    private readonly string _connStr;
    private readonly string _dbPath;

    public ChatRecommendationsMergeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"t21_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 文件仍被其他进程占用时忽略，交由系统清理
        }
    }

    /// <summary>
    /// R7 — 消息无关键词但有偏好时，用偏好关键词推荐：
    /// /chat「你好」（无关键词）+ 预置偏好 {"咖啡":3,"健身":2} →
    /// merged = [咖啡, 健身] → BestMatch 恒为意式浓缩咖啡机（Id 5，权重最高的「咖啡」优先）。
    /// 去 shuffle 后顺序确定，可精确断言 BestMatch.Id。
    /// </summary>
    [Fact]
    public async Task ShouldRecommendPreferenceProducts_WhenUserHasPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先产生对话消息（无关键词「你好」），使「无消息关键词但有偏好」场景成立
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chatResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 偏好「咖啡」权重最高 → BestMatch 为意式浓缩咖啡机（Id 5）；固定顺序可精确断言
        Assert.Equal(5, result.BestMatch!.Id);
        Assert.Equal("根据您的对话，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品"); // 偏好「咖啡」的咖啡机分类在列
    }

    /// <summary>
    /// R7 消息关键词驱动 — /recommendations 使用最新用户消息的关键词，忽略 Agent 结构化 Keywords 输出：
    /// mock Agent 返回 Keywords=["数码"]，但消息「推荐跑鞋」命中 鞋子/跑步 → BestMatch 恒为专业跑鞋（Id 3），
    /// 而非 Agent 关键词「数码」命中的数码产品。证明推荐随对话内容实时匹配，不依赖 LLM 结构化输出。
    /// </summary>
    [Fact]
    public async Task ShouldUseMessageKeywords_NotAgentKeywords()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["数码"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先跑一次 /chat（mock Agent 返回 Keywords=["数码"]），产生对话历史供 /recommendations 读最新消息
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑鞋"));
        chatResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 最新消息「推荐跑鞋」→ 鞋子/跑步 → BestMatch 跑鞋 Id 3（Agent Keywords=["数码"] 不参与）
        Assert.Equal(3, result.BestMatch!.Id);
        Assert.Equal("根据您的对话，为您推荐：", result.Message);
    }

    /// <summary>
    /// R7 — 无对话历史且无偏好时，兜底 All.Take(6) 且提示语「为您精选商品」：
    /// 只要商品库非空就显示精选兜底（不再显示「暂无特定推荐」造成提示语与列表矛盾）。
    /// BestMatch=null、Other 内容恒为 Id 1..6（固定顺序，无 shuffle）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnFallback_WhenNoPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 不先调 /chat：无对话历史 + 无偏好 → 商品库非空 → 精选兜底
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);
        Assert.Equal("为您精选商品", result.Message);          // 非「暂无特定推荐」
        Assert.Null(result.MatchedCategories);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).ToArray()); // 固定顺序（去 shuffle），可精确断言
    }

    /// <summary>
    /// R7 随对话内容 — 最新消息含关键词「咖啡」→ /recommendations 推荐含意式浓缩咖啡机（Id 5）：
    /// 读最新用户消息实时匹配关键词（不调 LLM），BestMatch 恒为咖啡机、提示语为「根据您的对话，为您推荐：」。
    /// </summary>
    [Fact]
    public async Task ShouldRecommendCoffeeMachine_WhenLatestMessageContainsCoffee()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先产生对话消息（含关键词「咖啡」）
        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐咖啡机"));
        chat.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.Equal(5, result.BestMatch!.Id);                 // 意式浓缩咖啡机（消息「咖啡」命中）
        Assert.Equal("根据您的对话，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品");
    }

    /// <summary>
    /// R7 提示语修正 — 有对话但消息无关键词且无偏好 → 兜底 All.Take(6) 且提示语「为您精选商品」：
    /// 不再显示「暂无特定推荐」（与列表有商品矛盾）；固定顺序，无 shuffle。
    /// </summary>
    [Fact]
    public async Task ShouldReturnCuratedFallback_WhenMessageHasNoKeywordAndNoPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先产生对话消息（无关键词「你好」），无偏好 → 非「完全无内容」，走精选兜底
        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chat.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);
        Assert.Equal("为您精选商品", result.Message);          // 非「暂无特定推荐」
        Assert.Null(result.MatchedCategories);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).ToArray());         // 固定顺序（去 shuffle），可精确断言
    }

    /// <summary>
    /// R7 无随机 — 去掉 shuffle 后多次调用推荐顺序完全一致（确定性）：
    /// 预置偏好 + 消息含关键词「咖啡」，连续 3 次 POST /api/recommendations，
    /// 每次 Other 的 Id 序列逐次相同、BestMatch 稳定为咖啡机（Id 5）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnDeterministicOrder_WhenCalledMultipleTimes()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先产生对话消息（含关键词「咖啡」）
        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐咖啡机"));
        chat.EnsureSuccessStatusCode();

        int[]? firstOtherIds = null;
        int? firstBestMatchId = null;
        for (var i = 0; i < 3; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/recommendations",
                new RecommendationRequest("marla", "keymatch"));
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<RecommendationResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result!.BestMatch);
            Assert.Equal("根据您的对话，为您推荐：", result.Message);

            var otherIds = result.Other.Select(p => p.Id).ToArray();
            if (firstOtherIds is null)
            {
                firstOtherIds = otherIds;
                firstBestMatchId = result.BestMatch!.Id;
            }
            else
            {
                // 无随机：三次 Other 顺序与 BestMatch 完全一致（确定性）
                Assert.Equal(firstOtherIds, otherIds);
                Assert.Equal(firstBestMatchId, result.BestMatch!.Id);
            }
        }
        Assert.NotNull(firstOtherIds);
        Assert.NotEmpty(firstOtherIds);
    }

    /// <summary>
    /// R7 无 LLM — /recommendations 随对话内容匹配关键词，不调用任何 LLM：
    /// mock IChatClient 的 GetResponseAsync 全程未被调用（无对话历史也直接出兜底推荐）。
    /// </summary>
    [Fact]
    public async Task ShouldNotCallLlm_WhenRequestingRecommendations()
    {
        WebApplicationFactory<Program>? factory = null;
        Meai.IChatClient? capturedMock = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, _connStr);
                services.RemoveAll<ModelRouter>();

                var mockClient = Substitute.For<Meai.IChatClient>();
                capturedMock = mockClient;
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                        """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""")));
                mockClient.GetStreamingResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = factory!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
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
                ]);
                services.AddSingleton(mockRouter);
            }));

        using var f = factory;
        using var client = f.CreateClient();
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);                          // 无对话历史 + 无偏好 → 精选兜底
        Assert.Equal("为您精选商品", result.Message);
        Assert.Equal(6, result.Other.Count);
        // host 构建后 capturedMock 已被赋值（ConfigureServices 回调在 CreateClient 时执行）
        Assert.NotNull(capturedMock);
        // /recommendations 不调用 LLM（无 agent / 无缓存路径）
        await capturedMock!.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<Meai.ChatMessage>>(),
            Arg.Any<Meai.ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 构造 WebApplicationFactory：隔离库 + mock Agent（返回指定的 agentJson）+ mock Router。
    /// 每个测试自建独立 factory（不基于其他 factory 派生），避免多 host 竞争。
    /// </summary>
    private WebApplicationFactory<Program> BuildFactory(string agentJson)
    {
        WebApplicationFactory<Program>? factory = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, _connStr);
                services.RemoveAll<ModelRouter>();

                // Mock IChatClient：返回指定 agentJson（可配置 Keywords 为空或当前关键词），
                // 聚焦 /recommendations 的端点层偏好合并，不依赖真实 LLM。
                var mockClient = Substitute.For<Meai.IChatClient>();
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, agentJson)));
                mockClient.GetStreamingResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                var capturedFactory = factory!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                mockRouter.GetAgent(Arg.Any<string>()).Returns(callInfo =>
                {
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
                ]);
                services.AddSingleton(mockRouter);
            }));

        return factory;
    }

    /// <summary>
    /// 查询 marla 的 UserId（Program.cs SeedUser 播种，Id 随机生成，须从库中读取）。
    /// </summary>
    private static async Task<Guid> GetMarlaUserIdAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var marla = await users.GetByUsernameAsync("marla");
        Assert.NotNull(marla);
        return marla!.Id;
    }

    /// <summary>
    /// 向隔离库播种一条用户偏好。
    /// </summary>
    private async Task SeedPreferencesAsync(Guid userId, string keywordsJson)
    {
        using var seedCtx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connStr).Options);
        seedCtx.UserPreferences.Add(new UserPreferences
        {
            UserId = userId,
            KeywordsJson = keywordsJson,
            UpdatedAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync();
    }

    /// <summary>
    /// 隔离数据库：替换 EF 注册指向临时文件库，建表 + 播种 18 商品。
    /// </summary>
    private static void ReplaceWithIsolatedDb(IServiceCollection services, string connStr)
    {
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();

        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connStr));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        using var seedCtx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options);
        seedCtx.Database.EnsureCreated();
        seedCtx.Products.AddRange(ProductSeedData.Products);
        seedCtx.SaveChanges();
    }
}
