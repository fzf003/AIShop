#pragma warning disable MAAI001
using System.Net.Http.Json;
using AIShop.AgentTelemetry;
using AIShop.Service;
using AIShop.Service.Clients;
using AIShop.Service.Tools;
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
/// R2 (P2-4) 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争。
/// </summary>
[CollectionDefinition(nameof(ChatPreferenceFilterTests), DisableParallelization = true)]
public sealed class ChatPreferenceFilterTestsCollection;

/// <summary>
/// R2 (P2-4) — 偏好关键词白名单过滤。
/// 偏好词来自 DB，未像 validKeywords 那样经 KeywordMap 白名单校验；若含非法词/空白词，
/// 合并后 SplitProducts 返回空推荐，端点却仍 HasRecommendation=true（空推荐却显示已推荐）。
/// 修复：/chat 与 /recommendations 两端点在合并前过滤偏好词——IsNullOrWhiteSpace 剔除，
/// 且仅保留 KeywordMap.ContainsKey 或任一商品 Tags 包含的词。
/// </summary>
[Collection(nameof(ChatPreferenceFilterTests))]
public sealed class ChatPreferenceFilterTests : IDisposable
{
    private readonly string _connStr;
    private readonly string _dbPath;

    public ChatPreferenceFilterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"r2p24_{Guid.NewGuid():N}.db");
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
    /// P2-4 /chat — 偏好仅含非法词 + 空白词、消息无当前关键词时，过滤后 merged 为空 →
    /// 走 All.Take(6) 兜底（HasRecommendation=false），而非「空推荐却 HasRecommendation=true」。
    /// </summary>
    [Fact]
    public async Task PostChat_WithOnlyInvalidPreferenceKeywords_FallsBackInsteadOfEmptyRecommendation()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"外星语":5,"   ":3}""");

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 非法/空白偏好词被过滤 → 无当前关键词 → 兜底，而非空推荐
        Assert.False(reply!.HasRecommendation);
        Assert.Null(reply.RecommendedProducts);
        Assert.Equal("为您精选商品", reply.RecMessage);
        Assert.Equal(6, reply.OtherProducts!.Count);
        Assert.Equal(1, reply.OtherProducts[0].Id);   // All.Take(6) 首条为 Id=1
    }

    /// <summary>
    /// P2-4 /chat — 偏好混合非法词 + 合法词时，非法词被过滤、合法词保留：
    /// 预置 {"外星语":5,"咖啡":2} → 过滤后仅剩 咖啡 → 推荐含意式浓缩咖啡机（Id=5）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithMixedPreferenceKeywords_FiltersInvalidAndKeepsValid()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"外星语":5,"咖啡":2}""");

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        Assert.True(reply!.HasRecommendation);                      // 合法词仍在 → 有推荐
        Assert.NotNull(reply.RecommendedProducts);
        Assert.Contains(reply.RecommendedProducts, p => p.Id == 5); // 意式浓缩咖啡机（偏好「咖啡」）
    }

    /// <summary>
    /// P2-4 /chat — 偏好词不是 KeywordMap key 但属于某商品 Tag 时保留（"或 catalog.All 任一商品 Tags 包含"）：
    /// 预置 {"穿戴":3} → 过滤后保留 穿戴 → 推荐含智能运动手表（Id=10，tags 含 穿戴）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithProductTagPreferenceKeyword_KeepsTagMatch()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"穿戴":3}""");

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        Assert.True(reply!.HasRecommendation);
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 10); // 智能运动手表（tags 含 穿戴）
    }

    /// <summary>
    /// P2-4 + R8 /recommendations — 偏好仅含非法词、消息无关键词时，/chat 过滤后 merged 为空 →
    /// 走兜底分支并写入快照（BestMatch=null / Message=「为您精选商品」），
    /// 而非「空推荐却提示已推荐」；/recommendations 镜像该快照（与聊天文案一致，非缓存 miss 的「为您精选商品」）。
    /// </summary>
    [Fact]
    public async Task PostRecommendations_WithOnlyInvalidPreferenceKeywords_FallsBack()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"外星语":5}""");

        // 先产生对话历史（/chat「你好」，消息无关键词 + 非法偏好被过滤 → /chat 兜底分支写快照）
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chatResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);

        Assert.Null(result!.BestMatch);
        // Message 取聊天快照（/chat 兜底 RecMessage），与聊天 100% 一致
        Assert.Equal("为您精选商品", result.Message);
        Assert.Null(result.MatchedCategories);
        Assert.Equal(6, result.Other.Count);
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

                // Mock IChatClient：返回指定 agentJson（Keywords=[] / Preferences=[]），
                // 聚焦 P2-4 的偏好关键词白名单过滤，不依赖真实 LLM。
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
                        sp.GetRequiredService<IChatHistoryStore>(), sp.GetRequiredService<IChatCompactionPolicy>(),
                        sp.GetRequiredService<CartToolProvider>(),
                        isOpenAI: false,
                        sp.GetRequiredService<AgentTelemetryOptions>());
                });
                mockRouter.GetDefaultAgent().Returns(
                    _ => new ShoppingAssistantAgent(
                        capturedFactory.Services.GetRequiredService<Meai.IChatClient>(),
                        capturedFactory.Services.GetRequiredService<IChatHistoryStore>(), capturedFactory.Services.GetRequiredService<IChatCompactionPolicy>(),
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
        // 用 Migrate 建表（对齐宿主 Program.cs 的 MigrateAsync）：EnsureCreated 不写迁移历史，
        // 会让宿主的 MigrateAsync 重跑迁移撞已存在的表 → 集成测试 host 启动失败
        seedCtx.Database.Migrate();
        seedCtx.Products.AddRange(ProductSeedData.Products);
        seedCtx.SaveChanges();
    }
}
