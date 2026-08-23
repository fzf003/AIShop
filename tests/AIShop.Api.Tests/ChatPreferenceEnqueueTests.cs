#pragma warning disable MAAI001
using System.Net.Http.Json;
using System.Text.Json;
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
using Xunit.Sdk;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Api.Tests;

/// <summary>
/// T20 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争。
/// </summary>
[CollectionDefinition(nameof(ChatPreferenceEnqueueTests), DisableParallelization = true)]
public sealed class ChatPreferenceEnqueueTestsCollection;

/// <summary>
/// T20 — /chat 端点入队轻量 UserPreferenceUpdate（design 4.3，spec「偏好权重累加」「偏好异步写入不阻塞响应」）。
/// 端点不再计算权重、不构造实体、不等待 DB 写入，只入队消息；累加由 PreferenceWriteHostedService（T14）worker 串行完成。
/// 测试 1 用 mock 队列验证「入队 + 响应在写入完成前返回」；测试 2 用真实队列验证端到端累加落库。
/// </summary>
[Collection(nameof(ChatPreferenceEnqueueTests))]
public sealed class ChatPreferenceEnqueueTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connStr;
    private readonly string _dbPath;

    public ChatPreferenceEnqueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"t20_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = BuildFactory(_connStr, queueOverride: null);
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
    /// spec「偏好异步写入不阻塞响应」：
    /// mock IPreferenceQueue 吞掉消息 → POST /api/chat 返回 ChatReply（不等待写入），
    /// 端点已用正确参数入队 UserPreferenceUpdate，且 DB 保持预置值（写入未发生）。
    /// 严格证明「响应在写入完成前返回」。
    /// </summary>
    [Fact]
    public async Task PostChat_EnqueuesPreferenceUpdate_AndReturnsBeforePersistence()
    {
        var mockQueue = Substitute.For<IPreferenceQueue>();
        mockQueue.TryEnqueue(Arg.Any<UserPreferenceUpdate>()).Returns(true);

        // 完全自建 factory（含 mock 队列），避免派生 factory 引用基 factory 的 mockRouter
        // 导致两个 host 竞争同一 Program 入口点（entry point exited without building an IHost）。
        using var f = BuildFactory(_connStr, mockQueue);
        var marlaId = await GetMarlaUserIdAsync(f);
        await SeedPreferencesAsync(marlaId, """{"咖啡":2}""");

        using var client = f.CreateClient();
        // 第一次 POST：Agent 把 Preferences 写入 StateBag["NewPreferences"]（复用 session，未直接入队）
        var response1 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response1.EnsureSuccessStatusCode();
        var reply1 = await response1.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply1);
        Assert.Contains("模拟回复", reply1!.Response);   // 端点已返回 ChatReply

        // 第二次 POST：Provider.Store 读到 StateBag["NewPreferences"] → 入队 mockQueue（响应仍先返回）
        var response2 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response2.EnsureSuccessStatusCode();

        // Provider.Store 入队轻量消息：UserId + Preferences 精确匹配（不构造实体、不等待落库）
        mockQueue.Received(1).TryEnqueue(Arg.Is<UserPreferenceUpdate>(
            u => u.UserId == marlaId && u.Preferences.SequenceEqual(new[] { "咖啡", "健身" })));

        // 消息被 mock 队列吞掉（真实 worker 未收到）→ DB 保持预置值，证明写入未在响应前发生
        var row = await ReadPreferencesAsync(marlaId);
        Assert.NotNull(row);
        Assert.Equal("{\"咖啡\":2}", row!.KeywordsJson);
    }

    /// <summary>
    /// spec「偏好权重累加」+「偏好异步写入不阻塞响应」：
    /// 预置 {"咖啡":2} → POST /api/chat（Agent 返回 Preferences=["咖啡","健身"]）→
    /// 响应返回 ChatReply 后轮询 UserPreferences 表，worker 最终落库 {"咖啡":3,"健身":1}。
    /// </summary>
    [Fact]
    public async Task PostChat_EnqueuesPreferenceUpdate_WorkerAccumulatesAndPersists()
    {
        var marlaId = await GetMarlaUserIdAsync(_factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":2}""");

        using var client = _factory.CreateClient();
        // 两次 POST：第一次写 StateBag["NewPreferences"]，第二次 Provider.Store 入队 → worker 累加落库
        var response1 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response1.EnsureSuccessStatusCode();
        var reply1 = await response1.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply1);
        Assert.Contains("模拟回复", reply1!.Response);   // 响应已返回，落库由后台 worker 异步完成

        var response2 = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好"));
        response2.EnsureSuccessStatusCode();

        var weights = await WaitForWeightsAsync(marlaId, w => w.Count == 2);
        Assert.Equal(3, weights["咖啡"]);   // 咖啡 2+1
        Assert.Equal(1, weights["健身"]);   // 健身 0+1
    }

    /// <summary>
    /// 构建带隔离库 + mock IChatClient + mock ModelRouter 的 WebApplicationFactory。
    /// <paramref name="queueOverride"/> 非 null 时替换 IPreferenceQueue 为 mock（测试 1 用）。
    /// </summary>
    private static WebApplicationFactory<Program> BuildFactory(string connStr, IPreferenceQueue? queueOverride)
    {
        WebApplicationFactory<Program>? factory = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, connStr);
                services.RemoveAll<ModelRouter>();
                if (queueOverride is not null)
                {
                    services.RemoveAll<IPreferenceQueue>();
                    services.AddSingleton<IPreferenceQueue>(queueOverride);
                }

                // Mock IChatClient：返回 Preferences=["咖啡","健身"]，驱动端点入队累加。
                var mockClient = Substitute.For<Meai.IChatClient>();
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                        """{"Reply":"模拟回复","Keywords":[],"Preferences":["咖啡","健身"]}""")));
                mockClient.GetStreamingResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

                var pipeline = new DeepSeekDelegatingChatClient(mockClient, null, "qwen");
                services.AddSingleton<Meai.IChatClient>(pipeline);

                ShoppingAssistantAgent CreateAgent(IServiceProvider sp) => new ShoppingAssistantAgent(
                    sp.GetRequiredService<Meai.IChatClient>(),
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                    ProductKeywordMap.Entries,
                    sp.GetRequiredService<CartToolProvider>(),
                    isOpenAI: false,
                    sp.GetRequiredService<AgentTelemetryOptions>(),
                    sp.GetRequiredService<IPreferenceQueue>());

                var capturedFactory = factory!;
                var mockRouter = Substitute.For<ModelRouter>();
                mockRouter.ActiveModel.Returns("qwen");
                // 缓存 agent 实例：多次 POST 复用同一 agent/session（偏好 State/StateBag 跨轮保留，
                // 本轮 NewPreferences 由下次 Run 的 Provider.Store 读到并入队）
                ShoppingAssistantAgent? cachedAgent = null;
                mockRouter.GetAgent(Arg.Any<string>()).Returns(_ => cachedAgent ??= CreateAgent(capturedFactory.Services));
                mockRouter.GetDefaultAgent().Returns(_ => cachedAgent ??= CreateAgent(capturedFactory.Services));
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
    /// 从隔离库读回用户偏好行。
    /// </summary>
    private async Task<UserPreferences?> ReadPreferencesAsync(Guid userId)
    {
        using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connStr).Options);
        return await ctx.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId);
    }

    /// <summary>
    /// 轮询隔离库读取落库的权重表，直到 matches 谓词成立；超时抛异常（避免异步写时序不稳导致 flaky）。
    /// </summary>
    private async Task<Dictionary<string, int>> WaitForWeightsAsync(
        Guid userId, Func<Dictionary<string, int>, bool> matches, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadPreferencesAsync(userId);
            if (row is not null)
            {
                var weights = JsonSerializer.Deserialize<Dictionary<string, int>>(row.KeywordsJson) ?? [];
                if (matches(weights))
                {
                    return weights;
                }
            }
            await Task.Delay(50);
        }
        throw new XunitException($"Timed out after {timeoutMs} ms waiting for UserPreferences({userId}) to be persisted");
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
