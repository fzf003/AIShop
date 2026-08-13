#pragma warning disable MAAI001
using System.Net.Http.Json;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;
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
    /// R8 — 消息无关键词但有偏好时，推荐栏镜像 /chat 偏好推荐快照：
    /// /chat「你好」（无关键词）+ 预置偏好 {"咖啡":3,"健身":2} → /chat 推荐 merged = [咖啡, 健身] →
    /// 写入 recommend_marla 快照，/recommendations 读缓存 → BestMatch 恒为意式浓缩咖啡机
    /// （Id 5，权重最高的「咖啡」优先），Message 取快照（与 /chat 的 RecMessage 一致）。
    /// </summary>
    [Fact]
    public async Task ShouldRecommendPreferenceProducts_WhenUserHasPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 先产生对话消息（无关键词「你好」），使 /chat 走「偏好推荐」分支并写入快照缓存
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
        // Message 取聊天快照（/chat 推荐分支的 RecMessage），与聊天 100% 一致
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品"); // 偏好「咖啡」的咖啡机分类在列
    }

    /// <summary>
    /// R8 — 推荐栏镜像 /chat 产物（而 /chat 本身消息关键词优先于 Agent 结构化 Keywords）：
    /// mock Agent 返回 Keywords=["数码"]，但消息「推荐跑鞋」在 /chat 中字面命中 鞋子/跑步 →
    /// chatReply 推荐含专业跑鞋（Id 3）；/recommendations 读 recommend_marla 快照 →
    /// BestMatch 恒为跑鞋 Id 3，而非 Agent 关键词「数码」命中的数码产品（推荐栏以聊天产物为准）。
    /// </summary>
    [Fact]
    public async Task ShouldUseMessageKeywords_NotAgentKeywords()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["数码"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先跑一次 /chat（mock Agent 返回 Keywords=["数码"]），产生快照缓存（/chat 优先消息字面匹配）
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "推荐跑鞋"));
        chatResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 消息「推荐跑鞋」→ 鞋子/跑步 → BestMatch 跑鞋 Id 3（Agent Keywords=["数码"] 不参与）
        Assert.Equal(3, result.BestMatch!.Id);
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
    }

    /// <summary>
    /// R8 — 缓存 miss（无 /chat 快照 recommend_marla）+ 无偏好时，兜底 All.Take(6) 且提示语「为您精选商品」：
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
    /// R8 — /chat 消息含关键词「咖啡」→ 写入快照缓存，/recommendations 读缓存 →
    /// BestMatch 恒为意式浓缩咖啡机（Id 5）、Message 取聊天快照（「根据您的兴趣，为您推荐：」）。
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
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品");
    }

    /// <summary>
    /// R8 — 有对话但消息无关键词且无偏好 → /chat 走兜底分支并写入快照（BestMatch=null、
    /// Other=All.Take(6)、Message=「暂无特定推荐 — 浏览精选商品」），/recommendations 镜像该快照：
    /// 推荐栏与聊天提示语一致（而非缓存 miss 时的「为您精选商品」）；固定顺序，无 shuffle。
    /// </summary>
    [Fact]
    public async Task ShouldReturnCuratedFallback_WhenMessageHasNoKeywordAndNoPreference()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先产生对话消息（无关键词「你好」），/chat 兜底分支写入快照（Message=暂无特定推荐 — 浏览精选商品）
        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "你好"));
        chat.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.BestMatch);
        // Message 取聊天快照（/chat 兜底 RecMessage），与聊天 100% 一致
        Assert.Equal("暂无特定推荐 — 浏览精选商品", result.Message);
        Assert.Null(result.MatchedCategories);
        Assert.Equal(6, result.Other.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6],
            result.Other.Select(p => p.Id).ToArray());         // 固定顺序（去 shuffle），可精确断言
    }

    /// <summary>
    /// R8 确定性 — 连续 3 次 POST /api/recommendations 都命中同一 recommend_marla 快照缓存，
    /// 每次 Other 的 Id 序列逐次相同、BestMatch 稳定为咖啡机（Id 5）、Message 一致（缓存返回同一快照）。
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
            Assert.Equal("根据您的兴趣，为您推荐：", result.Message);

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
    /// R8 — 推荐栏与聊天产物联动（核心修复，复现并验证字段问题）：
    /// mock Agent 返回 Keywords=["家居"]（LLM 语义回退，消息「有枕套吗」在 KeywordMap 字面匹配无命中），
    /// /chat 推荐集合含「家居」相关商品（含真丝枕套套装 Id 16）→ 写 recommend_marla 快照；
    /// /recommendations 读快照 → BestMatch 与 /chat recommendedProducts 首个完全一致
    /// （修复前 R7 字面匹配无命中 → 回退偏好咖啡机 Id 5）。
    /// 注：SplitProducts(["家居"]) 命中 Id 12（香薰蜡烛套装）与 Id 16（真丝枕套套装），
    /// 按 All 稳定序 12 在前 → BestMatch=12；id=16（枕套）在聊天推荐集合中。
    /// </summary>
    [Fact]
    public async Task ShouldMirrorChatRecommendation_WhenChatHasSemanticFallback()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["家居"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 消息「有枕套吗」字面匹配无命中 → /chat 回退到 Agent Keywords=["家居"]（LLM 语义）
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "有枕套吗"));
        chatResponse.EnsureSuccessStatusCode();
        var chatReply = await chatResponse.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(chatReply);
        Assert.True(chatReply!.HasRecommendation);
        // 聊天确实推荐了「家居」商品（字段问题：chat 语义回退命中 家居，含真丝枕套套装 Id 16）
        Assert.Contains(chatReply.RecommendedProducts!, p => p.Id == 16);

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        // 推荐栏 = 聊天推荐镜像：BestMatch 与 /chat recommendedProducts 首个 100% 一致
        //（修复前为咖啡机 Id 5）；Other/Message/MatchedCategories 全部取快照
        Assert.Equal(chatReply.RecommendedProducts![0].Id, result.BestMatch!.Id);
        Assert.NotEqual(5, result.BestMatch.Id);                  // 非咖啡机 → 未被偏好兜底驱动
        Assert.Equal(chatReply.RecMessage, result.Message);
        Assert.Equal(chatReply.OtherProducts!.Select(p => p.Id), result.Other.Select(p => p.Id));
        Assert.Equal(chatReply.MatchedCategories, result.MatchedCategories);
    }

    /// <summary>
    /// R8 — 缓存 miss（无聊天）+ 有偏好 → 偏好兜底：
    /// 无 recommend_marla 快照时，/recommendations 走 FilterValidPreferenceKeywords +
    /// MergeKeywords([], 偏好) + SplitProducts，BestMatch 恒为意式浓缩咖啡机（Id 5，权重最高的「咖啡」优先）。
    /// </summary>
    [Fact]
    public async Task ShouldUsePreferenceFallback_WhenCacheMiss()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 不先调 /chat → recommend_marla 缓存 miss → 偏好兜底
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.Equal(5, result.BestMatch!.Id);                   // 意式浓缩咖啡机（偏好「咖啡」权重最高）
        Assert.Equal("根据您的兴趣，为您推荐：", result.Message);
        Assert.Contains(result.MatchedCategories!, c => c == "厨房用品");
    }

    /// <summary>
    /// R8 — 缓存命中优先于 DB 最新消息：/chat「有枕套吗」写入家居快照缓存后，
    /// 直接向 chat_messages 注入一条字面命中「咖啡」的新用户消息（R7 下 /recommendations 读该消息
    /// 会字面匹配咖啡机），/recommendations 仍返回缓存快照（家居推荐、非咖啡机），
    /// 证明推荐栏不再自行做消息字面匹配、以聊天产物（缓存快照）为准。
    /// </summary>
    [Fact]
    public async Task ShouldUseCachedSnapshot_EvenWhenDbLatestMessageDiffers()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["家居"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        // 先 /chat「有枕套吗」→ 写 recommend_marla 家居快照缓存
        var chatResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "有枕套吗"));
        chatResponse.EnsureSuccessStatusCode();
        var chatReply = await chatResponse.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(chatReply);

        // 注入新用户消息「推荐咖啡机」作为 DB 最新消息（R7 下会字面匹配 → 咖啡机）
        var marlaId = await GetMarlaUserIdAsync(factory);
        await InsertLatestUserMessageAsync(marlaId, "推荐咖啡机");

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.BestMatch);
        Assert.NotEqual(5, result.BestMatch!.Id);                 // 非咖啡机 → 未被 DB 消息字面匹配驱动
        Assert.Equal(chatReply!.RecommendedProducts![0].Id, result.BestMatch.Id); // = 聊天家居快照首个
    }

    /// <summary>
    /// R8.1 — 推荐栏响应含完整推荐列表（含枕套，非仅 BestMatch）：
    /// mock Agent 返回 Keywords=["咖啡","家居"]（消息「有枕套吗」字面无命中，走语义回退）→
    /// /chat 推荐 recommendedProducts=[5咖啡机,11煎锅,12蜡烛,16枕套] 写入快照 →
    /// /recommendations 读缓存 → Recommended == 聊天完整列表 [5,11,12,16]、
    /// BestMatch == Recommended[0]（5）、Other 不含 5/11/12/16（bestMatch ∉ other 契约）。
    /// 注：tasks.md L470 写「Keywords=["家居"]」，但 SplitProducts(["家居"])=[12,16] 无法得到
    /// 设计示例 [5,11,12,16]；[5,11,12,16] 对应 SplitProducts(["咖啡","家居"])，故 mock 用该组关键词。
    /// </summary>
    [Fact]
    public async Task ShouldIncludeFullRecommendedList_WhenMirroringChatSnapshot()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":["咖啡","家居"],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var chat = await client.PostAsJsonAsync("/api/chat", new ChatRequest("marla", "有枕套吗"));
        chat.EnsureSuccessStatusCode();
        var chatReply = await chat.Content.ReadFromJsonAsync<ChatReply>();
        Assert.NotNull(chatReply);
        Assert.Equal([5, 11, 12, 16],
            chatReply!.RecommendedProducts!.Select(p => p.Id).ToArray()); // 聊天完整推荐列表（含枕套）

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);

        // Recommended == 聊天完整推荐列表（id 序列 [5,11,12,16]，含枕套）
        Assert.Equal([5, 11, 12, 16], result!.Recommended.Select(p => p.Id).ToArray());
        Assert.Equal(chatReply.RecommendedProducts!.Select(p => p.Id).ToArray(),
            result.Recommended.Select(p => p.Id).ToArray());
        // 不变量：BestMatch == Recommended[0]
        Assert.NotNull(result.BestMatch);
        Assert.Equal(result.Recommended[0].Id, result.BestMatch!.Id);
        Assert.Equal(5, result.BestMatch.Id);
        // 不变量：Recommended ∩ Other == ∅（bestMatch ∉ other）
        var recommendedIds = result.Recommended.Select(p => p.Id).ToHashSet();
        Assert.DoesNotContain(result.Other, p => recommendedIds.Contains(p.Id));
    }

    /// <summary>
    /// R8.1 — 缓存 miss + 偏好兜底有推荐：Recommended 为偏好命中完整列表、
    /// BestMatch == Recommended[0]、Other 与 Recommended 互斥。
    /// </summary>
    [Fact]
    public async Task ShouldIncludeRecommendedList_WhenPreferenceFallback()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var marlaId = await GetMarlaUserIdAsync(factory);
        await SeedPreferencesAsync(marlaId, """{"咖啡":3,"健身":2}""");

        // 不先调 /chat → recommend_marla 缓存 miss → 偏好兜底
        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);

        // 偏好命中完整列表非空、BestMatch 恒为意式浓缩咖啡机（Id 5，咖啡权重最高）
        Assert.NotEmpty(result!.Recommended);
        Assert.NotNull(result.BestMatch);
        Assert.Equal(5, result.BestMatch!.Id);
        // 不变量：BestMatch == Recommended[0]；Recommended ∩ Other == ∅
        Assert.Equal(result.Recommended[0].Id, result.BestMatch.Id);
        var recommendedIds = result.Recommended.Select(p => p.Id).ToHashSet();
        Assert.DoesNotContain(result.Other, p => recommendedIds.Contains(p.Id));
    }

    /// <summary>
    /// R8.1 — 缓存 miss + 无偏好 → 精选兜底：Recommended==[]、BestMatch==null、
    /// Other==All.Take(6) 固定顺序（Id 1..6）、Message「为您精选商品」。
    /// </summary>
    [Fact]
    public async Task ShouldReturnEmptyRecommended_WhenCuratedFallback()
    {
        using var factory = BuildFactory("""{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/recommendations",
            new RecommendationRequest("marla", "keymatch"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RecommendationResponse>();
        Assert.NotNull(result);

        Assert.Null(result!.BestMatch);
        Assert.Empty(result.Recommended);                          // 无推荐 → Recommended 空
        Assert.Equal("为您精选商品", result.Message);
        Assert.Null(result.MatchedCategories);
        Assert.Equal([1, 2, 3, 4, 5, 6], result.Other.Select(p => p.Id).ToArray());
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
    /// 直接向隔离库注入一条用户消息作为 DB 最新消息（R8 测试用：验证缓存命中优先于 DB 消息）。
    /// </summary>
    private async Task InsertLatestUserMessageAsync(Guid userId, string content)
    {
        using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connStr).Options);
        var session = await ctx.Sessions.FirstAsync(s => s.UserId == userId);
        ctx.ChatMessageRecords.Add(new ChatMessageRecord
        {
            SessionId = session.Id,
            Role = "user",
            Content = content,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
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
