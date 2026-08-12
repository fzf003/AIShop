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
    /// spec「推荐数据来源」MODIFIED — 有持久化偏好且 Agent 返回 Keywords 为空时，
    /// /recommendations 用偏好关键词产生 BestMatch：
    /// mock Agent 返回 Keywords=[]（无当前关键词），预置偏好 {"咖啡":3,"健身":2} →
    /// merged = [咖啡, 健身] → BestMatch 为意式浓缩咖啡机（偏好「咖啡」优先）。
    /// 与 /chat 的「偏好存在但无当前关键词时用偏好推荐」口径一致。
    /// </summary>
    [Fact]
    public async Task PostRecommendations_WithPreferenceButEmptyAgentKeywords_UsesPreferenceKeywords()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先产生对话历史 + agent_result 缓存（/chat 缓存 agent_result，Keywords=[]）
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chatResponse.EnsureSuccessStatusCode();

        // /recommendations：命中 agent_result 缓存（不重新调 LLM），偏好合并出推荐
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.Equal(5, result.BestMatch!.Id);   // 意式浓缩咖啡机（偏好「咖啡」）
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品");
    }

    /// <summary>
    /// spec「推荐合并 — 当前关键词优先，偏好补齐」在 /recommendations 同样生效：
    /// mock Agent 返回 Keywords=["鞋子"]（当前关键词）+ 预置偏好 {"咖啡":3,"健身":2} →
    /// merged = [鞋子, 咖啡, 健身] → BestMatch 为专业跑鞋（当前关键词「鞋子」优先，而非偏好「咖啡」）。
    /// 验证与 /chat 同一合并口径，避免两端推荐不一致。
    /// </summary>
    [Fact]
    public async Task PostRecommendations_WithCurrentKeywordAndPreference_PrefersCurrentKeyword()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["鞋子"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先产生对话历史 + agent_result 缓存（/chat 缓存 agent_result，Keywords=["鞋子"]）
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑鞋"));
        chatResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.Equal(3, result.BestMatch!.Id);   // 专业跑鞋（当前关键词「鞋子」优先，先于偏好「咖啡」）
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
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
