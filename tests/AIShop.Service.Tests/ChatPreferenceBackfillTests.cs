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

namespace AIShop.Service.Tests;

/// <summary>
/// T18 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争
/// （learnings 先例：共享工作树并行 WebApplicationFactory 会触发 flaky 失败）。
/// </summary>
[CollectionDefinition(nameof(ChatPreferenceBackfillTests), DisableParallelization = true)]
public sealed class ChatPreferenceBackfillTestsCollection;

/// <summary>
/// T18 — /chat 端点接入偏好回填（design 4.3 会话重建回填）。
/// 验证链路：POST /api/chat → 端点从 DB 加载 UserPreferences → 权重 Top-5 顿号连接 →
/// RunChatAsync(preferences:) → StateBag["Preferences"] → PreferenceMemoryProvider →
/// ChatOptions.Instructions（LLM system prompt）。
/// 对应 spec「会话重建从数据库回填偏好」的端到端路径（DB → 端点 → Agent 层）。
/// 断言读 <see cref="Meai.ChatOptions.Instructions"/> 而非消息列表（T16 probe 已确认注入路径）。
/// </summary>
[Collection(nameof(ChatPreferenceBackfillTests))]
public sealed class ChatPreferenceBackfillTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connStr;
    private readonly string _dbPath;
    private Meai.ChatOptions? _capturedOptions;

    public ChatPreferenceBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"t18_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";

        WebApplicationFactory<Program>? factory = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ReplaceWithIsolatedDb(services, _connStr);
                services.RemoveAll<ModelRouter>();

                // Mock IChatClient 捕获完整 LLM 输入（消息 + ChatOptions.Instructions）
                var mockClient = Substitute.For<Meai.IChatClient>();
                mockClient.GetResponseAsync(
                        Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                        Arg.Any<Meai.ChatOptions?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(ci =>
                    {
                        _capturedOptions = ci.Arg<Meai.ChatOptions?>();
                        return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                            """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}"""));
                    });
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
    /// 预置用户偏好 {"咖啡":3,"健身":2,"音乐":1} → 新会话 POST /api/chat →
    /// mock IChatClient 收到含 "咖啡、健身、音乐" 的 Instructions（spec「会话重建从数据库回填偏好」）。
    /// 消息不含任何关键词，排除推荐分支干扰，专注验证偏好回填注入 Agent 上下文。
    /// </summary>
    [Fact]
    public async Task PostChat_WithStoredPreferences_InjectsPreferenceTextIntoAgentContext()
    {
        var marlaId = await GetMarlaUserIdAsync();
        using (var seedCtx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connStr).Options))
        {
            seedCtx.UserPreferences.Add(new UserPreferences
            {
                UserId = marlaId,
                KeywordsJson = """{"咖啡":3,"健身":2,"音乐":1}""",
                UpdatedAt = DateTime.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好，随便聊聊"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("模拟回复", reply!.Response);

        // 偏好回填生效：LLM system prompt（ChatOptions.Instructions）含权重 Top-5 顿号连接文本
        Assert.NotNull(_capturedOptions);
        var instructions = _capturedOptions!.Instructions;
        Assert.NotNull(instructions);
        Assert.Contains("咖啡、健身、音乐", instructions);
        Assert.Contains("已知用户偏好", instructions);
    }

    /// <summary>
    /// 无持久化偏好 → POST /api/chat 仍正常返回，且 Instructions 不含「已知用户偏好」块
    /// （回填只在有偏好时注入，不注入空串）。
    /// </summary>
    [Fact]
    public async Task PostChat_WithoutStoredPreferences_DoesNotInjectPreferenceText()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat",
            new ChatRequest("marla", "你好，随便聊聊"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);
        Assert.Contains("模拟回复", reply!.Response);

        Assert.NotNull(_capturedOptions);
        var instructions = _capturedOptions!.Instructions;
        Assert.NotNull(instructions);
        Assert.DoesNotContain("已知用户偏好", instructions);
        Assert.DoesNotContain("咖啡、健身、音乐", instructions);
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
    /// 隔离数据库：替换 EF 注册指向临时文件库，建表 + 播种 18 商品
    /// （与 ChatEndpointsTests.ReplaceWithIsolatedDb 同一模式；Program.cs 启动 SeedUser 在 factory 启动时执行）。
    /// </summary>
    private static void ReplaceWithIsolatedDb(IServiceCollection services, string connStr)
    {
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();

        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connStr));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        // 播种 18 商品：隔离库是全新空库，ProductRepository 改查库后 /products 从空表返回 0，
        // 必须在此建表 + 播入 ProductSeedData（用独立 DbContextOptions 直接构造上下文，避免
        // ConfigureServices 阶段 BuildServiceProvider() 触发 Serilog "already frozen"）。
        using var seedCtx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options);
        seedCtx.Database.EnsureCreated();
        seedCtx.Products.AddRange(ProductSeedData.Products);
        seedCtx.SaveChanges();
    }
}
