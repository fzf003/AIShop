#pragma warning disable MAAI001
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
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
/// R4 测试集合定义：串行执行，避免与 ServiceDefaultsDebugTests 等并行宿主竞争。
/// </summary>
[CollectionDefinition(nameof(ChatReplySanitizationTests), DisableParallelization = true)]
public sealed class ChatReplySanitizationTestsCollection;

/// <summary>
/// R4 — Reply 回复文本清洗：LLM 回复中的「#5」「商品ID: 4」「商品ID：7」等商品 ID 展示
/// 从对话历史中去除，不向用户暴露商品 ID；购物车功能不受影响（前端用
/// RecommendedProducts/OtherProducts 的 ProductDto.Id 加购，不从 Reply 文本解析）。
/// 清洗只作用于 ChatReply.Response 字符串，RecommendedProducts/OtherProducts 的 Id 不变。
/// </summary>
[Collection(nameof(ChatReplySanitizationTests))]
public sealed class ChatReplySanitizationTests : IDisposable
{
    private readonly string _connStr;
    private readonly string _dbPath;

    public ChatReplySanitizationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"r4sanitize_{Guid.NewGuid():N}.db");
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
    /// R4 /chat — LLM 回复含「#5 意式浓缩咖啡机 ¥349.99（商品ID: 4）」「#3无线降噪耳机（商品ID：7）」时：
    /// Response 清洗后无 #数字 / 商品ID数字 模式（覆盖 # 带空格、# 粘连、英文冒号、中文冒号四种形态），
    /// 商品名与价格保留，RecommendedProducts 的结构化 Id 不变（前端加购来源）。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithProductIdMarkers_StripsIds_KeepsNamePriceAndRecommendedIds()
    {
        const string agentJson =
            """{"Reply":"推荐这款 #5 意式浓缩咖啡机 — ¥349.99（商品ID: 4），另一款 #3无线降噪耳机 售价 ¥199.00（商品ID：7）","Keywords":["咖啡"],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐咖啡"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 1) 无 #数字 模式（覆盖带空格 #5 与粘连 #3 两种形态）
        Assert.False(
            Regex.IsMatch(reply!.Response, @"#\d+", RegexOptions.None, TimeSpan.FromSeconds(1)),
            $"Response 仍含 #数字 模式: {reply.Response}");
        Assert.DoesNotContain("#5", reply.Response);
        Assert.DoesNotContain("#3", reply.Response);

        // 2) 无 商品ID数字 模式（覆盖英文冒号 商品ID: 4 与中文冒号 商品ID：7）
        Assert.False(
            Regex.IsMatch(reply.Response, @"商品ID[\s:：]*\d+", RegexOptions.None, TimeSpan.FromSeconds(1)),
            $"Response 仍含 商品ID数字 模式: {reply.Response}");
        Assert.DoesNotContain("商品ID: 4", reply.Response);
        Assert.DoesNotContain("商品ID：7", reply.Response);

        // 3) 商品名与价格保留（清洗不误删名称/价格中的数字）
        Assert.Contains("意式浓缩咖啡机", reply.Response);
        Assert.Contains("349.99", reply.Response);
        Assert.Contains("无线降噪耳机", reply.Response);
        Assert.Contains("199.00", reply.Response);

        // 4) 结构化推荐数据不受影响：RecommendedProducts 仍含 Id=5（意式浓缩咖啡机）
        Assert.True(reply.HasRecommendation);
        Assert.NotNull(reply.RecommendedProducts);
        Assert.Contains(reply.RecommendedProducts, p => p.Id == 5);
    }

    /// <summary>
    /// R4 /chat — 回复文本不含商品 ID 标记时清洗零副作用：正常文本原样保留（含价格数字）。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithoutProductIdMarkers_ResponseUnchanged()
    {
        const string plainReply = "这款意式浓缩咖啡机值得入手，价格 ¥349.99";
        const string agentJson =
            """{"Reply":"这款意式浓缩咖啡机值得入手，价格 ¥349.99","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 无商品 ID 标记时清洗零副作用：文本原样保留（含价格数字，不使用裸 \d+ 误删）
        Assert.Equal(plainReply, reply!.Response);
    }

    /// <summary>
    /// R4 /chat — 边界防误删：`#` 后无数字、`商品ID` 后无数字时不清洗（正则要求 ID 标记后紧跟数字，
    /// 不会把「# 话题」「商品ID 未知」这类普通文本当商品 ID 删掉）。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithHashOrProductIdWithoutDigit_DoesNotStrip()
    {
        const string agentJson =
            """{"Reply":"看看这个 # 意式浓缩咖啡机，商品ID 未知","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        Assert.Contains("# 意式浓缩咖啡机", reply!.Response);
        Assert.Contains("商品ID 未知", reply.Response);
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

                // Mock IChatClient：返回指定 agentJson（含带商品 ID 标记的 Reply），
                // 聚焦 R4 的回复文本清洗，不依赖真实 LLM。
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
