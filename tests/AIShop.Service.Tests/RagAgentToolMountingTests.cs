#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// Task 11 — search_knowledge 工具由 TextSearchProvider 挂载到 ShoppingAssistantAgent（AK-1/AK-4）。
///
/// 验证链路：
/// - AK-1：ShoppingAssistantAgent 的 AIContextProviders 挂载 TextSearchProvider（OnDemandFunctionCalling）后，
///   首轮 LLM 请求的 ChatOptions.Tools 含 search_knowledge（工具由 MAF 内建机制收集注入，非手写 AIFunction）。
/// - AK-4：BuildInstructions 只新增一行 search_knowledge 工具说明（无静态指令数据注入）；
///   且 ragSearchService 未注入时（RAG 未启用宿主）工具列表不含 search_knowledge——
///   证明该工具来源于 TextSearchProvider 挂载，而非构造函数硬编码注册。
///
/// 不调真实 LLM（AI-4）：mock IChatClient 返回纯文本回复（无 tool_calls），
/// TextSearchProvider 的 search 委托不会被模型调用（OnDemand 模式，AK-1 只验证工具注册）。
/// </summary>
public sealed class RagAgentToolMountingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RagAgentToolMountingTests()
    {
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
    public async Task RunChatAsync_WithRagSearchService_ToolListContainsSearchKnowledgeFromTextSearchProvider()
    {
        // 捕获 mock IChatClient 收到的完整 LLM 输入（ChatOptions：Tools + Instructions + 消息）
        Meai.ChatOptions? capturedOptions = null;
        IEnumerable<Meai.ChatMessage>? capturedMessages = null;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedMessages = ci.Arg<IEnumerable<Meai.ChatMessage>>();
                capturedOptions = ci.Arg<Meai.ChatOptions?>();
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                    """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}"""));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        // RAG 检索服务 mock：AK-1 只验证工具注册，模型不会实际调用该工具（mock 回复无 tool_calls）
        var ragSearchService = Substitute.For<IRagSearchService>();

        var agent = BuildAgent(mockClient, ragSearchService);

        await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-t1-user");

        Assert.NotNull(capturedOptions);

        // AK-1：首轮 LLM 请求工具列表含 search_knowledge，且来自 TextSearchProvider 的配置
        // （描述 = FunctionToolDescription，区分于任何手写工具）
        var tool = capturedOptions.Tools?.FirstOrDefault(t => t.Name == "search_knowledge");
        Assert.NotNull(tool);
        Assert.Contains("搜索商品知识/描述文档", tool.Description);

        // AK-4：Instructions 只新增一行工具说明（search_knowledge），无静态知识文档数据注入
        var llmInput = ExtractLlmInput(capturedOptions, capturedMessages);
        Assert.Contains("search_knowledge(query): 搜索商品知识/描述文档", llmInput);
        // 商品知识文档 Text 的「类别/价格」模板片段不应出现在 Instructions——
        // 若把知识文档静态注入指令，必然出现该模板；此断言证明检索能力只经 Tool 动态暴露（AK-4）
        Assert.DoesNotContain("类别：", llmInput);
        Assert.DoesNotContain("价格：¥", llmInput);
    }

    [Fact]
    public async Task RunChatAsync_WithoutRagSearchService_ToolListDoesNotContainSearchKnowledge()
    {
        Meai.ChatOptions? capturedOptions = null;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOptions = ci.Arg<Meai.ChatOptions?>();
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                    """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}"""));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        // RAG 未启用：不注入 IRagSearchService（对应 AddRag 未注册的宿主）
        var agent = BuildAgent(mockClient, ragSearchService: null);

        await agent.RunChatAsync(Guid.NewGuid(), "咖啡机有什么特点", "rag-t2-user");

        Assert.NotNull(capturedOptions);

        // AK-4 反向证明：未挂载 TextSearchProvider 时工具列表不含 search_knowledge
        // （该工具只能来自 TextSearchProvider 注入，不是构造函数硬编码的 AIFunction）
        Assert.DoesNotContain(capturedOptions.Tools ?? [], t => t.Name == "search_knowledge");
    }

    /// <summary>构造被测 ShoppingAssistantAgent（可选注入 IRagSearchService 验证挂载行为）。</summary>
    private ShoppingAssistantAgent BuildAgent(Meai.IChatClient mockClient, IRagSearchService? ragSearchService)
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
    /// HarnessInstructions 最终落在 ChatOptions.Instructions（provider 注入同处）还是 system 消息
    /// 由 MAF 版本决定，两处都搜保证断言与框架内部实现解耦。
    /// </summary>
    private static string ExtractLlmInput(Meai.ChatOptions? options, IEnumerable<Meai.ChatMessage>? messages)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(options?.Instructions))
            parts.Add(options.Instructions);
        if (messages is not null)
        {
            foreach (var m in messages.Where(m => m.Role == Meai.ChatRole.System))
            {
                parts.Add(string.Concat(m.Contents.OfType<Meai.TextContent>().Select(c => c.Text)));
            }
        }
        return string.Join("\n", parts);
    }
}
