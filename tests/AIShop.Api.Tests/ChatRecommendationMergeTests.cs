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
/// T19 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争。
/// </summary>
[CollectionDefinition(nameof(ChatRecommendationMergeTests), DisableParallelization = true)]
public sealed class ChatRecommendationMergeTestsCollection;

/// <summary>
/// T19 — /chat 端点接入推荐合并 + 兜底（design 4.3 / T17 RecommendationMerger）。
/// 覆盖 spec 4 条款：当前关键词优先偏好补齐、不足 3 个不补齐、无关键词无偏好兜底、偏好存在时用偏好推荐。
/// </summary>
[Collection(nameof(ChatRecommendationMergeTests))]
public sealed class ChatRecommendationMergeTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connStr;
    private readonly string _dbPath;

    public ChatRecommendationMergeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"t19_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";

        WebApplicationFactory<Program>? factory = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, _connStr);
                services.RemoveAll<ModelRouter>();

                // Mock IChatClient：消息不含关键词时回退路径也不补 Keywords（全部 []），
                // 保证 validKeywords 只来自消息直接匹配 + 偏好合并，聚焦推荐合并逻辑。
                var mockClient = Substitute.For<Meai.IChatClient>();
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

        _factory = factory;
    }

    public void Dispose()
    {
        _factory.Dispose();
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
    /// spec「推荐合并 — 当前关键词优先，偏好补齐」：
    /// 消息含「跑鞋」（仅匹配 2 个当前关键词：鞋子/跑步，均不足 3 个）→ 预置偏好 {"咖啡":3,"健身":2} →
    /// merged = [鞋子, 跑步, 咖啡, 健身] → RecommendedProducts 含专业跑鞋（当前关键词优先）
    /// 与意式浓缩咖啡机（偏好补齐的咖啡）。
    /// 注：不选「跑步」作为消息词——"跑步"同时是 健身/运动 关键词的 expansion tag，
    /// 消息"推荐跑步鞋"会匹配出 3 个当前关键词（跑步/健身/运动）而不触发补齐（见测试 2）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithCurrentKeywordAndPrefs_MergesPreferenceKeywords()
    {
        var marlaId = await GetMarlaUserIdAsync();
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "推荐跑鞋"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.True(reply!.HasRecommendation);
        Assert.Equal("根据您的兴趣，为您推荐：", reply.RecMessage);
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 3);   // 专业跑鞋（当前关键词「鞋子/跑步」优先）
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 5);    // 意式浓缩咖啡机（偏好「咖啡」补齐）
    }

    /// <summary>
    /// spec「推荐合并 — 当前关键词不足 3 个才补齐」：
    /// 消息「推荐跑步鞋」中"跑步"是 健身/运动 的共享 tag，实际匹配出 3 个当前关键词
    /// （跑步/健身/运动）→ merged 保持 3 个、不追加偏好
    /// （仅偏好「音乐/阅读」匹配的商品——复古黑胶唱片机/畅销悬疑小说——不出现在推荐中）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithThreeCurrentKeywords_DoesNotAppendPreferenceKeywords()
    {
        var marlaId = await GetMarlaUserIdAsync();
        await SeedPreferencesAsync(marlaId, """{"音乐":3,"阅读":2}""");

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "推荐跑步鞋"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.True(reply!.HasRecommendation);
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 3);   // 专业跑鞋（当前关键词「跑步」）
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 6);    // 高级瑜伽垫（当前关键词「健身」）
        Assert.DoesNotContain(reply.RecommendedProducts!, p => p.Id == 9); // 畅销悬疑小说（仅偏好「阅读」）
        Assert.DoesNotContain(reply.RecommendedProducts!, p => p.Id == 7); // 复古黑胶唱片机（仅偏好「音乐」）
    }

    /// <summary>
    /// spec「无关键词无偏好兜底」：
    /// 消息无关键词 + DB 无偏好 → HasRecommendation=false、RecommendedProducts=null、
    /// OtherProducts = All.Take(6)（Id 1..6）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithoutKeywordAndPreference_ReturnsFallback()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.False(reply!.HasRecommendation);
        Assert.Null(reply.RecommendedProducts);
        Assert.Equal("暂无特定推荐 — 浏览精选商品", reply.RecMessage);
        Assert.NotNull(reply.OtherProducts);
        Assert.Equal([1, 2, 3, 4, 5, 6], reply.OtherProducts!.Select(p => p.Id).ToArray());
    }

    /// <summary>
    /// spec「偏好存在但无当前关键词时用偏好推荐」：
    /// 消息无关键词 + 预置偏好 {"咖啡":3,"健身":2} → merged = [咖啡, 健身] →
    /// HasRecommendation=true、RecommendedProducts 含咖啡机（咖啡）与瑜伽垫（健身）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithoutKeywordButWithPreference_UsesPreferenceKeywords()
    {
        var marlaId = await GetMarlaUserIdAsync();
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.True(reply!.HasRecommendation);
        Assert.Equal("根据您的兴趣，为您推荐：", reply.RecMessage);
        Assert.NotEmpty(reply.RecommendedProducts!);
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 5);   // 意式浓缩咖啡机（偏好「咖啡」）
        Assert.Contains(reply.RecommendedProducts!, p => p.Id == 6);    // 高级瑜伽垫（偏好「健身」）
    }

    /// <summary>
    /// 查询 marla 的 UserId（Program.cs SeedUser 播种，Id 随机生成，须从库中读取）。
    /// </summary>
    private async Task<Guid> GetMarlaUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
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
