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
/// R4/R5/R9 — Reply 回复文本清洗：LLM 回复中的「#5」「商品ID: 4」「商品ID：7」「商品ID为4」「商品Id:4」
/// 等商品 ID 展示从对话历史中去除，不向用户暴露商品 ID；购物车功能不受影响（前端用
/// RecommendedProducts/OtherProducts 的 ProductDto.Id 加购，不从 Reply 文本解析）。
/// 清洗只作用于 ChatReply.Response 字符串，RecommendedProducts/OtherProducts 的 Id 不变。
/// R5 收紧：#\d+ 只删商品 ID 1-18 范围内的（防误删「订单号 #123456」等非商品 ID 的 #数字）。
/// R9：Agent 指令固定唯一合法格式「商品Id:N」（FixedIdPattern 精确删，IgnoreCase 覆盖小写 id），
/// ProductIdLabelPattern 字符类扩入「为/是」兜底删「商品ID为4」等变体（字段泄漏案例）。
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
    /// R5 /chat — 正则收紧：#\d+ 只删商品 ID 1-18 范围内的。
    /// 非商品 ID 的 #数字保留（订单号 #123456、超出范围的 #20/#19）；
    /// 范围内的 #5/#18 仍删除；商品ID: 4（前缀明确，保持任意数字）仍删除。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithOutOfRangeHashIds_KeepsNonProductIds_StripsInRangeIds()
    {
        const string agentJson =
            """{"Reply":"订单号 #123456 处理中，#20 号商品，#5 意式浓缩咖啡机 ¥349.99，商品ID: 4 无线降噪耳机，#18 户外帐篷，#19 待上架商品","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 1) 非商品 ID 的 #数字保留：#123456（订单号）、#20/#19（超出 1-18 范围）
        Assert.Contains("#123456", reply!.Response);
        Assert.Contains("#20", reply.Response);
        Assert.Contains("#19", reply.Response);
        Assert.Contains("订单号", reply.Response);
        Assert.Contains("处理中", reply.Response);

        // 2) 范围内的 #数字仍删除：#5（1-18 内）、#18（上界 18 内）
        Assert.DoesNotContain("#5", reply.Response);
        Assert.DoesNotContain("#18", reply.Response);
        Assert.Contains("意式浓缩咖啡机", reply.Response);
        Assert.Contains("349.99", reply.Response);
        Assert.Contains("户外帐篷", reply.Response);

        // 3) 商品ID: 4（前缀明确）仍删除
        Assert.DoesNotContain("商品ID", reply.Response);
        Assert.Contains("无线降噪耳机", reply.Response);
    }

    /// <summary>
    /// R9 — LLM 回复含固定格式「商品Id:N」（Agent 指令唯一合法格式）时精确删除：
    /// 覆盖英文冒号（商品Id:4）、小写 id（商品id:5，IgnoreCase）、中文冒号（商品Id：6），
    /// 商品名与价格保留（清洗不误删名称/价格数字）。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithFixedProductIdFormat_StripsFixedFormat()
    {
        const string agentJson =
            """{"Reply":"无线降噪耳机 商品Id:4 售价 ¥249.99，意式浓缩咖啡机 商品id:5 ¥349.99，高级瑜伽垫 商品Id：6","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 固定格式「商品Id:N」被精确删除（含小写 id、中文冒号）
        Assert.DoesNotContain("商品Id:4", reply!.Response);
        Assert.DoesNotContain("商品id:5", reply.Response);
        Assert.DoesNotContain("商品Id：6", reply.Response);
        // 商品名与价格保留
        Assert.Contains("无线降噪耳机", reply.Response);
        Assert.Contains("249.99", reply.Response);
        Assert.Contains("意式浓缩咖啡机", reply.Response);
        Assert.Contains("高级瑜伽垫", reply.Response);
    }

    /// <summary>
    /// R9 — 字段泄漏案例回潮修复：LLM 回复「…价格为249.99元，商品ID为4…」——
    /// R4/R5 正则只覆盖「商品ID[:：]数字」未覆盖「为」字连接，ID 漏出。
    /// ProductIdLabelPattern 字符类扩入「为/是」兜底后：商品ID为4 / 商品ID是5 被删除，价格保留。
    /// </summary>
    [Fact]
    public async Task PostChat_ReplyWithProductIdForIsVariants_StripsFallbackVariants()
    {
        const string agentJson =
            """{"Reply":"为您推荐无线降噪耳机，价格为249.99元，商品ID为4。另一款意式浓缩咖啡机商品ID是5，需要为您加入购物车吗？","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(reply);

        // 兜底：商品ID为4 / 商品ID是5 被删除（为/是 连接词变体）
        Assert.DoesNotContain("商品ID", reply!.Response);
        Assert.DoesNotContain("为4", reply.Response);
        Assert.DoesNotContain("是5", reply.Response);
        // 商品名与价格保留
        Assert.Contains("无线降噪耳机", reply.Response);
        Assert.Contains("意式浓缩咖啡机", reply.Response);
        Assert.Contains("249.99", reply.Response);
    }

    /// <summary>
    /// R10 — /api/login 历史清洗：历史中 assistant 消息含商品 ID 标记（固定格式 商品Id:10、
    /// 变体 商品ID为4）时，login 返回的历史经 SanitizeReply 清洗——assistant 消息不含这些 ID 片段，
    /// user 消息原样保留，商品名/价格保留。
    /// </summary>
    [Fact]
    public async Task PostLogin_HistoryAssistantMessages_AreSanitized()
    {
        const string agentJson =
            """{"Reply":"为您推荐无线降噪耳机，商品Id:10 售价 ¥249.99，意式浓缩咖啡机 商品ID为4","Keywords":[],"Preferences":[]}""";
        using var factory = BuildFactory(agentJson);
        using var client = factory.CreateClient();

        // 产生对话：user「你好」+ assistant（含商品 ID 标记的 Reply，经 SqliteChatHistoryProvider 持久化到历史）
        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chat.EnsureSuccessStatusCode();

        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/login", new LoginRequest("marla"));
        login.EnsureSuccessStatusCode();
        var profile = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(profile);

        // user 消息原样保留（不参与清洗）
        Assert.Contains(profile!.History, m => m.Role == "user" && m.Content == "你好");
        // assistant 消息被清洗：不含固定格式与变体 ID，商品名与价格保留
        var assistantMsg = profile.History.Single(m => m.Role == "assistant");
        Assert.DoesNotContain("商品Id:10", assistantMsg.Content);
        Assert.DoesNotContain("商品ID为4", assistantMsg.Content);
        Assert.Contains("无线降噪耳机", assistantMsg.Content);
        Assert.Contains("249.99", assistantMsg.Content);
        Assert.Contains("意式浓缩咖啡机", assistantMsg.Content);
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
