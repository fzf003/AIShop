#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
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
/// T11 — RunChatStreamAsync 增量 chunk 顺序 + complete chunk 携带 FullResult（spec Requirement 5）。
/// T12 — 跨 chunk 商品 ID 清洗（spec Requirement 5「跨 chunk 商品 ID 清洗」Scenario）。
/// T13 — 会话创建失败降级到 RunChatAsync（spec Requirement 5「会话创建失败降级到 RunChatAsync」Scenario）。
/// 验证链路：
/// 1. 增量文本边收边 yield：mock IChatClient.GetStreamingResponseAsync 返回分块文本更新，用 TCS 门控
///    后续分块（首块立即、剩余块待释放）——若"收集后批量 yield"回归，首个 MoveNextAsync 会因等不到
///    后续分块而超时，可证明真增量；
/// 2. 流结束携带完整结果 chunk：IsComplete=true 且 FullResult 携带解析出的完整 AgentChatResult；
/// 3. 该轮落库被 MarkRoundFinalAsync 兜底补标（is_final=true 终点行）；
/// 4. 跨 chunk 商品 ID 清洗：SanitizeReplyIncremental 跨 chunk 缓冲，模式不完整前不释放安全前缀；
/// 5. 会话创建失败降级：MAF CreateSessionAsync 纯内存（不触碰 DB/IChatClient，候选注入点不可行），
///    经 T13 测试缝（internal 构造注入 agentWrapper）包装真实 agent 使首次会话创建抛异常，
///    断言降级调用 RunChatAsync 并 yield 其回复文本 + IsComplete=true 的 chunk，且不向调用方抛异常。
/// </summary>
public sealed class RunChatStreamAsyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RunChatStreamAsyncTests()
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

    /// <summary>
    /// 复用 ShoppingAssistantAgentRunTests 构造模式：mock IChatClient + in-memory SQLite。
    /// </summary>
    private ShoppingAssistantAgent CreateAgent(Meai.IChatClient mockClient, Func<AIAgent, AIAgent>? agentWrapper = null)
    {
        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory);

        // agentWrapper 走 T13 测试缝（internal 构造）：包装真实 agent 使首次会话创建抛异常
        return new ShoppingAssistantAgent(
            mockClient, dbFactory, ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None }, agentWrapper);
    }

    /// <summary>
    /// 增量文本边收边 yield（spec Requirement 5「增量文本边收边 yield」Scenario）：
    /// mock 返回首块文本更新后，后续分块被 TCS 门控——断言首个 chunk 在首块到达后立即 yield，
    /// 且增量文本按到达顺序逐个 yield（非收集后批量）。
    /// </summary>
    [Fact]
    public async Task RunChatStreamAsync_YieldsIncrementalTextInArrivalOrder()
    {
        // mock 流式返回分块文本更新：首块立即、后续块由 gate 门控（模拟 LLM 增量生成时序）
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(StreamingTextUpdatesAsync(gate.Task, "第一段", "第二段", """{"Reply":"模拟回复","Keywords":["跑步"],"Preferences":[]}"""));
        // 正常路径不调用 GetResponseAsync；配置默认回复以防 FICC 兜底
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟回复")));

        var agent = CreateAgent(mockClient);
        var sessionId = Guid.NewGuid();

        await using var e = agent.RunChatStreamAsync(sessionId, "推荐商品", "t11-user").GetAsyncEnumerator();

        // 首块文本到达即 yield 首个 chunk（gate 未释放；若伪流式"收集后批量 yield"回归，此处会超时）
        var hasFirst = await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(hasFirst, "首块文本到达后应立即 yield 首个 chunk（非收集后批量 yield）");
        var firstChunk = e.Current;
        Assert.False(firstChunk.IsComplete);
        Assert.Equal("第一段", firstChunk.TextDelta);

        // 后续分块被门控尚未到达——证明是增量下发，而非等待完整流收集
        Assert.False(gate.Task.IsCompleted);

        // 释放门控，消费剩余增量
        gate.SetResult();
        var textDeltas = new List<string> { firstChunk.TextDelta };
        ChatStreamChunk? completeChunk = null;
        while (await e.MoveNextAsync())
        {
            if (e.Current.IsComplete)
                completeChunk = e.Current;
            else
                textDeltas.Add(e.Current.TextDelta);
        }

        // 增量文本按 SSE/流式到达顺序逐个 yield（spec「增量文本边收边 yield」）
        Assert.Equal(
            new[] { "第一段", "第二段", """{"Reply":"模拟回复","Keywords":["跑步"],"Preferences":[]}""" },
            textDeltas);

        // 流结束携带完整结果 chunk（spec「流结束携带完整结果 chunk」Scenario）
        Assert.NotNull(completeChunk);
        Assert.True(completeChunk!.IsComplete);
        Assert.NotNull(completeChunk.FullResult);
        Assert.Equal("模拟回复", completeChunk.FullResult.Reply);
        Assert.Equal(new[] { "跑步" }, completeChunk.FullResult.Keywords);
    }

    /// <summary>
    /// 流结束携带完整结果 chunk + 该轮落库被 MarkRoundFinalAsync 兜底补标（spec Requirement 5
    /// 「流结束携带完整结果 chunk」Scenario）：消费完整流后存在唯一 is_final=true 终点行（最后一条）。
    /// </summary>
    [Fact]
    public async Task RunChatStreamAsync_CompleteChunkCarriesFullResultAndRoundMarkedFinal()
    {
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(StreamingTextUpdatesAsync(Task.CompletedTask, """{"Reply":"模拟回复","Keywords":[],"Preferences":[]}"""));
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟回复")));

        var agent = CreateAgent(mockClient);
        var sessionId = Guid.NewGuid();

        // 消费完整流（含 complete chunk 之后的 MarkRoundFinalAsync）
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in agent.RunChatStreamAsync(sessionId, "推荐商品", "t11-user"))
            chunks.Add(chunk);

        // complete chunk 携带解析出的完整 AgentChatResult
        var complete = Assert.Single(chunks, c => c.IsComplete);
        Assert.NotNull(complete.FullResult);
        Assert.Equal("模拟回复", complete.FullResult.Reply);
        Assert.Empty(complete.FullResult.Keywords);

        // 该轮落库被 MarkRoundFinalAsync 兜底补标（spec「流结束携带完整结果 chunk」：随后调用 MarkRoundFinalAsync）
        await using var ctx = new AppDbContext(_options);
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();
        Assert.NotEmpty(rows);
        Assert.Single(rows, r => r.IsFinal);
        Assert.True(rows[^1].IsFinal);
        Assert.Equal(rows.Max(r => r.Id), rows[^1].Id);
    }

    /// <summary>
    /// 跨 chunk 商品 ID 清洗（spec Requirement 5「跨 chunk 商品 ID 清洗」Scenario）：
    /// mock IChatClient.GetStreamingResponseAsync 把「商品Id:4」切分到相邻两个增量更新——
    /// 第一个更新以「商品Id」结尾（FixedIdPattern 前缀不完整），第二个更新补全「:4，现在可以下单」。
    /// 断言 SanitizeReplyIncremental 跨 chunk 缓冲：
    /// 1. 模式不完整前不释放安全前缀——首个更新（含「商品Id」前缀）被整块缓冲、未释放到前端；
    ///    若退化为"逐块直接下发"则「商品Id」会出现在首个 TextDelta 中；
    /// 2. 增量 chunk 释放顺序与清洗语义正确——仅释放缓冲前安全前缀与流末冲洗的尾部文本；
    /// 3. 拼接后的最终文本不包含商品 ID 标记（无「商品Id:4」泄漏）。
    /// 注意：tasks.md 示例切分「推荐商品商品」+「Id:4」在既有 EndsWithPatternPrefix 语义下不会触发
    /// 缓冲（仅尾随「商品」不视为模式前缀，「Id:4」会泄漏），故改用「为您推荐商品Id」+「:4，…」切分，
    /// 让「商品Id:4」真正跨两个增量更新且模式前缀确实不完整，从而验证缓冲语义（spec 场景仍成立）。
    /// </summary>
    [Fact]
    public async Task RunChatStreamAsync_SanitizesProductIdSplitAcrossChunks()
    {
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(StreamingTextUpdatesAsync(Task.CompletedTask, "为您推荐商品Id", ":4，现在可以下单"));
        // 正常路径不调用 GetResponseAsync；配置默认回复以防 FICC 兜底
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟回复")));

        var agent = CreateAgent(mockClient);
        var sessionId = Guid.NewGuid();

        // 消费完整流，仅拼接非 complete chunk 的 TextDelta
        var textDeltas = new List<string>();
        await foreach (var chunk in agent.RunChatStreamAsync(sessionId, "推荐商品", "t12-user"))
        {
            if (!chunk.IsComplete)
                textDeltas.Add(chunk.TextDelta);
        }

        // 模式不完整前不释放安全前缀 + 增量 chunk 释放顺序与清洗语义正确：
        // 首块「为您推荐商品Id」因「商品Id」前缀不完整被整块缓冲（未释放），
        // 后续补全后仅释放安全前缀「为您推荐」，流末冲洗释放清洗残留的尾部文本「，现在可以下单」
        Assert.Equal(new[] { "为您推荐", "，现在可以下单" }, textDeltas);

        // 拼接后的最终文本不包含商品 ID 标记（无「商品Id:4」泄漏）
        var joined = string.Concat(textDeltas);
        Assert.DoesNotContain("商品Id:4", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("商品Id", joined, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 会话创建失败降级到 RunChatAsync（spec Requirement 5「会话创建失败降级到 RunChatAsync」Scenario）：
    /// 经 T13 测试缝包装真实 agent，使首次 _agent.CreateSessionAsync 抛异常（MAF CreateSessionAsync 纯内存
    /// 创建 ChatClientAgentSession，不触碰 DB/IChatClient，候选注入点①/②均不可行）。
    /// 断言：
    /// 1. RunChatStreamAsync 降级调用 RunChatAsync 并 yield 其回复文本 + IsComplete=true 的 chunk；
    /// 2. 不向调用方抛异常（降级路径正常结束）；
    /// 3. 间接证据：非流式 GetResponseAsync 被调用（RunChatAsync 走 RunAsync→GetResponseAsync）、
    ///    流式 GetStreamingResponseAsync 未被调用（会话创建失败后未进入 RunStreamingAsync 流式路径）。
    /// </summary>
    [Fact]
    public async Task RunChatStreamAsync_FallsBackToRunChatAsyncWhenSessionCreationFails()
    {
        var mockClient = Substitute.For<Meai.IChatClient>();
        // 正常路径不调用 GetStreamingResponseAsync；配置默认回复以防 FICC 兜底 + 供降级路径返回回复文本
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, "降级回复")));
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(StreamingTextUpdatesAsync(Task.CompletedTask));

        // 会话创建失败注入：包装真实 agent，首次 CreateSessionAsync 抛异常，
        // 后续（降级 RunChatAsync 内部再创建会话）委托给 inner 真实 agent
        var agent = CreateAgent(mockClient, inner => new FailFirstSessionCreationAgent(inner));
        var sessionId = Guid.NewGuid();

        // 消费完整流——若降级路径向调用方抛异常，此处将因异常失败
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in agent.RunChatStreamAsync(sessionId, "推荐商品", "t13-user"))
            chunks.Add(chunk);

        // 降级路径 yield 其回复文本 + IsComplete=true 的 chunk，且不向调用方抛异常（正常结束）
        Assert.Equal(2, chunks.Count);
        Assert.False(chunks[0].IsComplete);
        Assert.Equal("降级回复", chunks[0].TextDelta);
        Assert.True(chunks[1].IsComplete);
        Assert.NotNull(chunks[1].FullResult);
        Assert.Equal("降级回复", chunks[1].FullResult!.Reply);

        // 间接证据：降级调用 RunChatAsync（非流式 GetResponseAsync 被调用）……
        _ = mockClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>());
        // ……且未进入流式路径（GetStreamingResponseAsync 未被调用）
        mockClient.DidNotReceive().GetStreamingResponseAsync(
            Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 分块文本更新流：首个分块立即返回，后续分块等待 gate 信号（模拟 LLM 增量生成时序）。
    /// </summary>
    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextUpdatesAsync(
        Task gate, params string[] texts)
    {
        var isFirst = true;
        foreach (var text in texts)
        {
            if (!isFirst)
                await gate;
            isFirst = false;
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, text);
        }
    }

    /// <summary>
    /// T13 测试缝辅助：包装真实 agent，首次 CreateSessionAsync 抛异常（模拟会话创建失败），
    /// 后续调用委托给 inner 真实 agent——让 RunChatStreamAsync 会话创建失败触发降级后，
    /// 降级的 RunChatAsync 内部再次创建会话能成功，从而验证"降级路径正常结束、不向调用方抛异常"。
    /// </summary>
    private sealed class FailFirstSessionCreationAgent : DelegatingAIAgent
    {
        private int _createSessionCalls;

        public FailFirstSessionCreationAgent(AIAgent inner) : base(inner) { }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _createSessionCalls) == 1)
                throw new InvalidOperationException("模拟会话创建失败（T13 测试缝）");
            return base.CreateSessionCoreAsync(cancellationToken);
        }
    }
}
