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
    /// R6 纯偏好驱动 — 有偏好时 /recommendations 用偏好关键词出推荐：
    /// 预置偏好 {"咖啡":3,"健身":2} → merged = [咖啡, 健身] → SplitProducts 出推荐，
    /// BestMatch 为偏好命中的商品之一（咖啡机/健身相关，R6 shuffle 只变顺序不变内容）。
    /// </summary>
    [Fact]
    public async Task ShouldRecommendPreferenceProducts_WhenUserHasPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 期望集合：与端点同一 SplitProducts 口径，取偏好关键词命中的商品 Id 集合
        // （咖啡机 Id 5 / 跑鞋 3 / 瑜伽垫 6 / 水瓶 8 / 手表 10 / 蛋白粉 14）
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var expectedIds = catalog.SplitProducts(["咖啡", "健身"]).Recommended.Select(p => p.Id).ToArray();
        Assert.NotEmpty(expectedIds);   // 偏好命中至少一个商品，否则不该走推荐分支

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 偏好「咖啡」/「健身」→ BestMatch 为咖啡机/健身相关商品之一（shuffle 只变顺序不变内容）
        Assert.Contains(result.BestMatch!.Id, expectedIds);
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品"); // 偏好「咖啡」的咖啡机分类在列
    }

    /// <summary>
    /// R6 纯偏好驱动 — /recommendations 忽略 Agent/当前消息关键词，只按偏好推荐：
    /// mock Agent 返回 Keywords=["鞋子"]（旧行为下当前关键词优先会固定推专业跑鞋 Id 3），
    /// R6 后端点不再读取对话历史/Agent 关键词，BestMatch 仍从偏好命中的商品集合中产生。
    /// </summary>
    [Fact]
    public async Task ShouldIgnoreAgentKeywords_WhenPreferenceDriven()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["鞋子"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先跑一次 /chat（mock Agent 返回 Keywords=["鞋子"]），R6 后 /recommendations 不再读取其输出
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑鞋"));
        chatResponse.EnsureSuccessStatusCode();

        // 期望集合：纯偏好关键词「咖啡/健身」命中的商品（不含「鞋子」当前关键词的独立贡献）
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var expectedIds = catalog.SplitProducts(["咖啡", "健身"]).Recommended.Select(p => p.Id).ToArray();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 当前消息「推荐跑鞋」的「鞋子」关键词不参与合并：BestMatch 仍出自纯偏好集合
        Assert.Contains(result.BestMatch!.Id, expectedIds);
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
    }

    /// <summary>
    /// R6 纯偏好驱动 — 无偏好时 /recommendations 走 All.Take(6) 兜底：
    /// BestMatch=null、Message=暂无特定推荐、Other 内容恒为 Id 1..6（shuffle 只变顺序不变内容）。
    /// </summary>
    [Fact]
    public async Task ShouldReturnFallback_WhenNoPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);
        Assert.Equal("暂无特定推荐 — 浏览精选商品", result.Message);
        Assert.Null(result.MatchedCategories);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// R6 去缓存/去 LLM — /recommendations 纯偏好驱动，不调用任何 LLM：
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
        Assert.Null(result!.BestMatch);                          // 无偏好 → 兜底
        Assert.Equal("暂无特定推荐 — 浏览精选商品", result.Message);
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
