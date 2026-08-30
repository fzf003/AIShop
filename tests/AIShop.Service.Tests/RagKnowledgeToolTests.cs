#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using AIShop.Service.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// Task 13 — search_knowledge Agent 级工具测试（AK-1/AK-2/AK-3/AI-3）。
///
/// 与 Task 11（RagAgentToolMountingTests，只验证工具「注册」）的差异：
/// 本类用 FICC 双轮 mock 验证「调用路径可执行」——模型首轮生成
/// FunctionCallContent(search_knowledge, userQuestion) → HarnessAgent 的 FICC 循环执行
/// TextSearchProvider 注入的工具 → search 委托真实收到模型生成的 userQuestion →
/// 格式化知识片段作为工具结果回传模型 → 模型第二轮输出最终回复。
///
/// 不调真实 LLM / 真实 ONNX（AI-4）：mock IChatClient + mock IRagSearchService。
/// 检索语义本身（SearchKnowledgeAsync 返回标题/类别/文本片段）已由 Task 8/12 覆盖，
/// 本类聚焦 Agent↔工具适配层（RagTextSearchAdapter + TextSearchProvider 格式化）的契约。
/// </summary>
public sealed class RagKnowledgeToolTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RagKnowledgeToolTests()
    {
        // in-memory SQLite 按连接隔离：每个测试实例独立连接，无跨测试共享状态（与 RagAgentToolMountingTests 同构）
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(_options))
        {
            // 用 Migrate 建表（对齐宿主 Program.cs 的 MigrateAsync，项目 memory「integration-test-db-migrate」）：
            // EnsureCreated 建表但不写 __EFMigrationsHistory，与宿主迁移历史不一致；本项目集成测试
            // 隔离库建表统一 Migrate，避免「表已建却被迁移重跑」的语义分叉。
            ctx.Database.Migrate();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task RunChatAsync_WhenModelCallsSearchKnowledge_ToolRegisteredDelegateReceivesUserQuestionAndFinalReplyReturns()
    {
        // AK-1：工具已注册（首轮 ChatOptions.Tools 含 search_knowledge）+ 调用路径可执行
        // （FICC 迭代真实调用 search 委托、工具结果回传后模型第二轮输出最终回复）
        var (agent, requests, options, ragSearch) = BuildFiccAgent(
            userQuestion: "咖啡机的特点",
            hits: [CoffeeMachineKnowledgeHit],
            finalReply: """{"Reply":"咖啡机采用意式浓缩萃取","Keywords":[],"Preferences":[]}""");

        var (result, _) = await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-k1-user");

        // 注册：首轮模型请求的工具列表含 search_knowledge（来自 TextSearchProvider 注入，非构造函数硬编码）
        Assert.NotNull(options[0]);
        Assert.Contains(options[0]!.Tools ?? [], t => t.Name == "search_knowledge");

        // 调用路径可执行：search 委托真实收到模型生成的 userQuestion（不是「注册了但从未被调用」）
        await ragSearch.Received(1).SearchKnowledgeAsync("咖啡机的特点", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // 工具结果回传后 FICC 循环正常结束，最终回复 = 模型第二轮文本
        Assert.Equal("咖啡机采用意式浓缩萃取", result.Reply);

        // 恰好两轮请求（工具调用轮 + 最终回复轮），证明工具调用发生在同一 RunChatAsync 内
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task RunChatAsync_WhenSearchKnowledgeReturnsHits_ToolResultContainsTitleCategoryTextFragments()
    {
        // AK-2：工具调用后，回传给模型的结果含商品知识片段（标题/类别/描述文本）。
        // 实际格式 = TextSearchProvider 内置格式化器输出（SourceDocName/Contents 行），
        // 非早期手写工具契约的「找到 N 条相关知识：」前缀（design §5.7 已改为 TextSearchProvider
        // 承载，ContextFormatter=null 用内置格式化器；契约偏差见 handoff 说明）
        var (agent, requests, _, _) = BuildFiccAgent(
            userQuestion: "咖啡机的特点",
            hits: [CoffeeMachineKnowledgeHit],
            finalReply: """{"Reply":"咖啡机采用意式浓缩萃取","Keywords":[],"Preferences":[]}""");

        await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-k2-user");

        var toolText = ExtractToolResultText(requests[1]);

        // 标题 / 类别 / 描述文本片段（spec AK-2：返回相关 ProductDocument 片段，含标题、类别、描述）
        Assert.Contains("SourceDocName: 意式浓缩咖啡机", toolText);
        Assert.Contains("（厨房用品）：", toolText);
        Assert.Contains("标签：咖啡、浓缩", toolText);
    }

    [Fact]
    public async Task RunChatAsync_WhenSearchKnowledgeReturnsNoHits_CompletesWithoutCrash()
    {
        // AI-3：无相关知识文档（检索返回空）→ 不崩溃、RunChatAsync 正常返回最终回复。
        // TextSearchProvider 内置格式化器对空结果返回空串，模型仍收到「工具已调用」的回执并继续输出
        var (agent, requests, _, ragSearch) = BuildFiccAgent(
            userQuestion: "咖啡机的特点",
            hits: [],
            finalReply: """{"Reply":"暂时没有找到相关知识","Keywords":[],"Preferences":[]}""");

        var (result, _) = await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-k3-user");

        // 工具被调用（返回空结果），全程无异常抛出
        await ragSearch.Received(1).SearchKnowledgeAsync("咖啡机的特点", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
        Assert.Equal("暂时没有找到相关知识", result.Reply);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task RunChatAsync_KnowledgeContentNotInInstructions_BeforeToolCall_DynamicInjectionOnly()
    {
        // AK-4：检索能力全部经 Tool 暴露——知识文档内容在工具调用前不出现于
        // Instructions/system（首轮请求），只在工具被调用后才作为工具结果动态注入（第二轮）。
        // 对比第一/二轮请求即可证明「未新增静态指令数据注入」。
        var (agent, requests, options, _) = BuildFiccAgent(
            userQuestion: "咖啡机的特点",
            hits: [CoffeeMachineKnowledgeHit],
            finalReply: """{"Reply":"咖啡机采用意式浓缩萃取","Keywords":[],"Preferences":[]}""");

        await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-k4-user");

        // 工具调用前（首轮）：Instructions 含 search_knowledge 工具说明行（AK-4 允许的工具说明，非数据注入），
        // 但不含任何知识文档内容（如「意式浓缩」「价格：¥349.99」）——知识未被静态注入指令
        var firstLlmInput = ExtractLlmInput(options[0], requests[0]);
        Assert.Contains("search_knowledge(query): 搜索商品知识/描述文档", firstLlmInput);
        Assert.DoesNotContain("意式浓缩", firstLlmInput);
        Assert.DoesNotContain("价格：¥349.99", firstLlmInput);

        // 工具调用后（第二轮）：同一知识内容作为工具结果动态注入（两轮对比 = 动态注入的实证）
        Assert.Contains("意式浓缩咖啡机", ExtractToolResultText(requests[1]));
    }

    /// <summary>商品 5 的受控知识命中（Text 与 ProductDocument.Text 拼接规则一致，design §4.1）。</summary>
    private static readonly KnowledgeSearchHit CoffeeMachineKnowledgeHit = new(
        "product-5", "意式浓缩咖啡机", "厨房用品",
        "意式浓缩咖啡机。类别：厨房用品。标签：咖啡、浓缩、厨房、早晨。价格：¥349.99", 1.0);

    /// <summary>
    /// 构造 FICC 双轮 Agent：模型首轮生成 search_knowledge 工具调用、第二轮返回最终回复。
    /// 返回（agent, 每轮请求消息, 每轮 ChatOptions, mock IRagSearchService）供各用例断言。
    /// </summary>
    private (ShoppingAssistantAgent Agent, List<Meai.ChatMessage[]> Requests, List<Meai.ChatOptions?> Options, IRagSearchService RagSearch)
        BuildFiccAgent(string userQuestion, IReadOnlyList<KnowledgeSearchHit> hits, string finalReply)
    {
        var requests = new List<Meai.ChatMessage[]>();
        var options = new List<Meai.ChatOptions?>();
        var turn = 0;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                requests.Add(ci.Arg<IEnumerable<Meai.ChatMessage>>().ToArray());
                options.Add(ci.Arg<Meai.ChatOptions?>());
                turn++;
                if (turn == 1)
                {
                    // 首轮：模型决定调用 search_knowledge。注入工具的参数名固定为 userQuestion（POC 实测，handoff-1 R11）
                    return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, new Meai.AIContent[]
                    {
                        new Meai.FunctionCallContent("call_knowledge_1", "search_knowledge",
                            new Dictionary<string, object?> { ["userQuestion"] = userQuestion }),
                    }));
                }
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, finalReply));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var ragSearch = Substitute.For<IRagSearchService>();
        ragSearch.SearchKnowledgeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hits));

        return (BuildAgent(mockClient, ragSearch), requests, options, ragSearch);
    }

    /// <summary>构造被测 ShoppingAssistantAgent（注入 IRagSearchService 挂载 search_knowledge，与 RagAgentToolMountingTests 同构）。</summary>
    private ShoppingAssistantAgent BuildAgent(Meai.IChatClient mockClient, IRagSearchService ragSearchService)
    {
        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory, new CurrentUserAccessor());

        return new ShoppingAssistantAgent(
            mockClient, new ChatHistoryStore(dbFactory), new RoundBasedCompactionPolicy(), ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            preferenceQueue: null, scopeFactory, ragSearchService);
    }

    /// <summary>
    /// 汇总 LLM 输入文本：ChatOptions.Instructions + system 消息文本。
    /// HarnessInstructions 最终落在 ChatOptions.Instructions 还是 system 消息由 MAF 版本决定，
    /// 两处都搜保证断言与框架内部实现解耦（与 RagAgentToolMountingTests 同构）。
    /// </summary>
    private static string ExtractLlmInput(Meai.ChatOptions? chatOptions, IEnumerable<Meai.ChatMessage>? messages)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(chatOptions?.Instructions))
            parts.Add(chatOptions.Instructions);
        if (messages is not null)
        {
            foreach (var m in messages.Where(m => m.Role == Meai.ChatRole.System))
            {
                parts.Add(string.Concat(m.Contents.OfType<Meai.TextContent>().Select(c => c.Text)));
            }
        }
        return string.Join("\n", parts);
    }

    /// <summary>提取请求消息中回传的工具结果文本（FunctionResultContent 或 TextContent，兼容 MAF/MEAI 两种表示）。</summary>
    private static string ExtractToolResultText(IEnumerable<Meai.ChatMessage> messages)
    {
        var parts = new List<string>();
        foreach (var m in messages)
        {
            parts.AddRange(m.Contents.OfType<Meai.FunctionResultContent>().Select(f => f.Result?.ToString() ?? ""));
            parts.AddRange(m.Contents.OfType<Meai.TextContent>().Select(t => t.Text));
        }
        return string.Join("\n", parts);
    }
}
