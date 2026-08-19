#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Service;
using AIShop.Service.Tools;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// T10T1 — 两次 RunChatAsync（同 sessionId）→ 每轮生成独立 run_id、落库均带非空 run_id。
/// 验证链路：RunChatAsync 每轮开始 Guid.NewGuid() 写 StateBag（spec「RunChatAsync 每轮开始生成
/// run_id 写 StateBag」#1）→ Provider.Store 从 StateBag 读同一值给该轮所有 FICC 迭代打标
/// （spec「Store 读取 StateBag…」#2）→ 跨模型切换/继续对话追加新轮无冲突
/// （spec「跨模型切换追加新轮无冲突」#16：两轮 run_id 不同、不覆盖）。
/// </summary>
public sealed class ShoppingAssistantAgentRunTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ShoppingAssistantAgentRunTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task RunChatAsync_TwiceSameSession_PersistsTwoRoundsWithDistinctNonEmptyRunIds()
    {
        // mock IChatClient：返回纯文本 assistant 回复（无 tool_calls），模拟一次无工具调用的完整对话轮。
        // 不调真实 LLM；同一 mock 供两轮复用（GetResponseAsync 每次返回同一固定回复）
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""")));
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        // dbFactory 每次 CreateDbContext 返回绑定同一内存 SQLite 的上下文（与 RunChatAsyncPreferenceBackfillTests 同构）
        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory);

        var agent = new ShoppingAssistantAgent(
            mockClient, dbFactory, ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        var sessionId = Guid.NewGuid();

        // 同一 sessionId 连续两轮对话（模拟跨模型切换/继续对话追加新轮）
        await agent.RunChatAsync(sessionId, "推荐商品", "t10t1-user");
        await agent.RunChatAsync(sessionId, "再推荐一个", "t10t1-user");

        // 读回该 session 落库消息，按 id 升序保持时序
        await using var ctx = new AppDbContext(_options);
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // Store 打标链路接通：两轮均有消息落库（非空）
        Assert.NotEmpty(rows);

        // 所有落库消息均带非空 run_id（spec #1/#2：Run 生成的 run_id 经 StateBag 传给 Store）
        Assert.All(rows, r => Assert.NotNull(r.RunId));

        // 按 run_id 分组 → 恰好两轮（spec「跨模型切换追加新轮无冲突」：追加新轮不覆盖既有轮）
        var rounds = rows.GroupBy(r => r.RunId!.Value).ToList();
        Assert.Equal(2, rounds.Count);

        // 两轮 run_id 互不相同（spec #1：每轮开始独立生成一次，非复用）
        var runIds = rounds.Select(g => g.Key).ToList();
        Assert.Equal(2, runIds.Distinct().Count());

        // 组内按 id 升序保持时序（spec「Store 读取 StateBag…」：组内行仍按 id 升序）
        foreach (var round in rounds)
        {
            var ids = round.Select(r => r.Id).ToList();
            Assert.Equal(ids.OrderBy(i => i).ToList(), ids);
        }
    }

    [Fact]
    public async Task RunChatAsync_NormalReturn_RoundHasIsFinalTerminalRow()
    {
        // T10T2 主路径：mock 返回纯文本 assistant 回复（无 tool_calls）→ Store 内判定（T5）
        // 将末条纯文本 assistant 落 is_final=true；RunChatAsync 正常返回后该轮存在轮次终点行。
        // 对应 spec「Run 后兜底补标 is_final」#5 的主判定链路（Store 判定为主）+「轮次完成判断基于
        // is_final 查询」#7——验证 Agent→Provider 链路：RunChatAsync 正常返回后该轮必有 is_final=true 终点行。
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}""")));
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory);

        var agent = new ShoppingAssistantAgent(
            mockClient, dbFactory, ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        var sessionId = Guid.NewGuid();
        await agent.RunChatAsync(sessionId, "推荐商品", "t10t2-user");

        // 读回该轮落库消息，按 id 升序保持时序
        await using var ctx = new AppDbContext(_options);
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 一轮消息落库（Store 打标链路接通）
        Assert.NotEmpty(rows);

        // 一轮共享同一 run_id
        Assert.Single(rows.Select(r => r.RunId).Distinct());

        // RunChatAsync 正常返回后该轮存在且仅有唯一的 is_final=true 终点行
        // （spec #5「Run 后兜底补标 is_final」：正常完成轮必有轮次终点行）
        Assert.Single(rows, r => r.IsFinal);

        // 终点行即该轮最后一条（max id），为末条纯文本 assistant 最终回复
        // （Store T5 主判定路径落标；Content 经 StripAgentReplyJson 提取 Reply 字段）
        Assert.True(rows[^1].IsFinal);
        Assert.Equal(rows.Max(r => r.Id), rows[^1].Id);
        Assert.Equal("assistant", rows[^1].Role);
        Assert.Equal("模拟回复", rows[^1].Content);
        Assert.False(rows[0].IsFinal); // 首条 user 行非终点
    }

    [Fact]
    public async Task RunChatAsync_ToolOnlyNoTextEnding_RunTimeBackfillMarksLastRowFinal()
    {
        // T10T2 补标路径：mock 每轮迭代都只返回 assistant(FunctionCallContent)——模型「仅调工具未输出文本」。
        // 工具 add_to_cart(productId=999, quantity=0) 数量非法时短路返回，不触碰真实 DI/DB，
        // FICC 循环至 MaximumIterationsPerRequest=3 耗尽后 RunChatAsync 正常返回。
        // Store 内判定（T5）只对「末条纯文本 assistant」落 is_final，此场景末条恒为 tool/FCC 行 → 不落标；
        // RunChatAsync 正常返回后由 _provider.MarkRoundFinalAsync(runId) 兜底补标（spec「Run 后兜底补标
        // is_final」#5，评审 Y1：终点行可为 tool/FCC 行）——本用例验证 Agent→Provider 补标链路真实接通：
        // 若 RunChatAsync 忘记调用 MarkRoundFinalAsync，该轮将无 is_final=true 行。
        var callCounter = 0;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCounter++;
                var fcc = new Meai.FunctionCallContent($"call_{callCounter}", "add_to_cart",
                    new Dictionary<string, object?> { ["productId"] = 999, ["quantity"] = 0 });
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, new[] { fcc }));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory);

        var agent = new ShoppingAssistantAgent(
            mockClient, dbFactory, ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        var sessionId = Guid.NewGuid();
        var (result, _) = await agent.RunChatAsync(sessionId, "加购商品", "t10t2-user");

        // RunChatAsync 正常返回（FICC 迭代耗尽不抛异常，补标链路可达）
        Assert.NotNull(result);

        await using var ctx = new AppDbContext(_options);
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 一轮消息落库
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(r => r.RunId).Distinct());

        // 该轮存在唯一的 is_final=true 终点行——本轮末条恒为 tool/FCC 行，Store T5 不落标，
        // 只能来自 RunChatAsync 正常返回后的 MarkRoundFinalAsync 兜底补标（Agent→Provider 链路）
        Assert.Single(rows, r => r.IsFinal);

        // 终点行是最后一条（max id），且不是纯文本 assistant——
        // Store T5 只对末条纯文本 assistant 落标，末条为 tool/FCC 行被补标即证明补标来自兜底路径
        // （评审 Y1：轮次终点行≠最终回复，可为 tool/FCC 行）
        Assert.True(rows[^1].IsFinal);
        Assert.Equal(rows.Max(r => r.Id), rows[^1].Id);
        var last = rows[^1];
        Assert.False(last.Role == "assistant" && string.IsNullOrEmpty(last.ToolCalls));
    }
}
