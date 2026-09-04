#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Core.Services;
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
/// 回归测试：user 消息不得随 MAF 上下文 Provider 合并而重复入库。
/// 根因（已修）：MemoryContextProvider 无记忆命中时若返回带 Messages 的 AIContext，
/// AIContextProvider 基类会把返回 messages 与原 input concat → user 双写。
/// 本测试挂 MemoryContextProvider（mock 记忆服务）+ FICC 工具迭代，断言 user 只存 1 条。
/// </summary>
public sealed class ChatHistoryUserNotDuplicatedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ChatHistoryUserNotDuplicatedTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// 回归：流式回复按 delta 分块（每块一个 TextContent）落库时，
    /// 不得在每块之间插入换行（修复前 ChatMessageMapper 用 Environment.NewLine 拼接 → 历史竖排）。
    /// </summary>
    [Fact]
    public async Task RunChatStreamAsync_MultiChunkAssistant_StoredWithoutLineBreakFragments()
    {
        var mockClient = Substitute.For<Meai.IChatClient>();
        // 流式：把连续回复 "客官好眼光" 拆成 3 个 delta 分块（模拟真实逐 token/逐段流式）
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(StreamingBlocksAsync("客", "官好", "眼光"));
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟")));

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var sc = new ServiceCollection();
        sc.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = sc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory, new CurrentUserAccessor());

        var agent = new ShoppingAssistantAgent(
            mockClient, new ChatHistoryStore(dbFactory), new RoundBasedCompactionPolicy(), cartTools,
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            scopeFactory: scopeFactory);

        var sessionId = Guid.NewGuid();
        var consumed = 0;
        await foreach (var chunk in agent.RunChatStreamAsync(sessionId, "推荐耳机", "t-blk"))
        {
            _ = chunk;
            consumed++;
        }
        Assert.True(consumed > 0, "流式应产出至少一个 chunk");

        using var ctx = new AppDbContext(_options);
        var stored = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && m.Role == "assistant")
            .ToListAsync();

        Assert.NotEmpty(stored);
        foreach (var row in stored)
            Assert.DoesNotContain("\n", row.Content, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingBlocksAsync(params string[] blocks)
    {
        foreach (var b in blocks)
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, b);
    }

    [Fact]
    public async Task RunChatAsync_WithMemoryProviderAndToolIteration_UserStoredOnce()
    {
        // mock LLM：第 1 次返回 add_to_cart tool_call（quantity=0 短路，不碰 DB），
        // 第 2 次（工具结果回传后的迭代）返回最终文本 JSON —— 模拟真实 FICC 两轮迭代
        var callCount = 0;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var fcc = new Meai.FunctionCallContent("call_1", "add_to_cart",
                        new Dictionary<string, object?> { ["productId"] = 999, ["quantity"] = 0 });
                    return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, new[] { fcc }));
                }
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                    """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}"""));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var sc = new ServiceCollection();
        sc.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = sc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory, new CurrentUserAccessor());

        // 挂 MemoryContextProvider（mock IMemoryService），对齐真实 ShoppingAssistantAgent 挂载
        var memory = Substitute.For<Mem0Sharp.IMemoryService>();
        memory.SearchAsync(
                Arg.Any<string>(), Arg.Any<Mem0Sharp.MemorySearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Mem0Sharp.SearchResult>>(Array.Empty<Mem0Sharp.SearchResult>()));
        memory.AddAsync(
                Arg.Any<IEnumerable<Mem0Sharp.Message>>(), Arg.Any<Mem0Sharp.MemoryAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Mem0Sharp.AddResult([])));
        var currentUser = Substitute.For<AIShop.Core.Interfaces.ICurrentUserAccessor>();
        currentUser.CurrentUser.Returns("t-dedup-user");

        var agent = new ShoppingAssistantAgent(
            mockClient, new ChatHistoryStore(dbFactory), new RoundBasedCompactionPolicy(), cartTools,
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            scopeFactory: scopeFactory, memoryService: memory, currentUserAccessor: currentUser);

        var sessionId = Guid.NewGuid();
        await agent.RunChatAsync(sessionId, "推荐商品", "t-dedup-user");

        using var ctx = new AppDbContext(_options);
        var userRows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && m.Role == "user")
            .ToListAsync();

        Assert.True(callCount >= 2, $"期望触发工具迭代(callCount>=2)，实际 callCount={callCount}");
        // user 消息只应存 1 条（FICC 迭代不应重复存同一条 user）
        Assert.Single(userRows);
    }
}
