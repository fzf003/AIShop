#pragma warning disable MAAI001
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;
using AIShop.Api.Agents;
using NSubstitute;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Tests;

public sealed class SqliteChatHistoryProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SqliteChatHistoryProvider _provider;
    private readonly Guid _sessionId;
    private readonly AgentSession _session;

    private static readonly Type ProviderType = typeof(SqliteChatHistoryProvider);
    private static readonly MethodInfo ProvideMethod = ProviderType.GetMethod(
        "ProvideChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StoreMethod = ProviderType.GetMethod(
        "StoreChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public SqliteChatHistoryProviderTests()
    {
        _sessionId = Guid.NewGuid();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Seed the session
        using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();
            seed.Sessions.Add(new Core.Entities.Session { Id = _sessionId, UserId = Guid.NewGuid() });
            seed.SaveChanges();
        }

        _dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        _dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var ctx = new AppDbContext(options);
            ctx.Database.EnsureCreated();
            return ctx;
        });
        _dbFactory.CreateDbContext().Returns(_ =>
        {
            var ctx = new AppDbContext(options);
            ctx.Database.EnsureCreated();
            return ctx;
        });

        _provider = new SqliteChatHistoryProvider(_dbFactory);
        _session = new TestSession();
        _session.StateBag.SetValue("SessionId", _sessionId.ToString());
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private async Task<IReadOnlyList<AgentChatMessage>> InvokeProvideAsync(
        IList<AgentChatMessage>? requestMessages = null)
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokingContext(agent, _session,
            requestMessages ?? [new AgentChatMessage(ChatRole.User, "Hello")]);

        var result = ProvideMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask<IEnumerable<AgentChatMessage>>)result!;
        return (await valueTask).ToList();
    }

    private async Task InvokeStoreAsync(
        IList<AgentChatMessage>? requestMessages = null,
        IList<AgentChatMessage>? responseMessages = null)
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokedContext(agent, _session,
            requestMessages ?? [new AgentChatMessage(ChatRole.User, "Hello")],
            responseMessages ?? [new AgentChatMessage(ChatRole.Assistant, "Hi")]);

        var result = StoreMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask)result!;
        await valueTask;
    }

    [Fact]
    public async Task Store_UserMessage_WritesContentColumn()
    {
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "你好")]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "user")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("你好", row.Content);
    }

    [Fact]
    public async Task Store_AssistantMessage_WritesContentToolCallsAndReasoning()
    {
        var asstMsg = new AgentChatMessage(ChatRole.Assistant, "搜索中");
        asstMsg.Contents.Add(new FunctionCallContent("call_1", "search_product",
            new Dictionary<string, object?> { ["q"] = "手机" }));
        asstMsg.Contents.Add(new TextReasoningContent("思考过程"));

        await InvokeStoreAsync(responseMessages: [asstMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "assistant")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("搜索中", row.Content);
        Assert.NotNull(row.ToolCalls);
        Assert.Contains("search_product", row.ToolCalls);
        Assert.Equal("思考过程", row.Reasoning);
    }

    [Fact]
    public async Task Store_ToolMessage_WritesContentAndToolCallId()
    {
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_123", "查询结果"));

        await InvokeStoreAsync(responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "tool")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("查询结果", row.Content);
        Assert.Equal("call_123", row.ToolCallId);
    }

    [Fact]
    public async Task Store_ToolMessageWithMultipleFrcs_WritesJsonArraySingleRow()
    {
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_a", "结果A"));
        toolMsg.Contents.Add(new FunctionResultContent("call_b", "结果B"));

        await InvokeStoreAsync(responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "tool")
            .ToListAsync();

        // 恰好 1 行（不按 FRC 数拆行）
        var row = Assert.Single(rows);
        // ToolCallId 置空
        Assert.Null(row.ToolCallId);
        // ToolCalls 列为 JSON 数组（[{id, result}, ...]），单行存储不拆行
        Assert.False(string.IsNullOrEmpty(row.ToolCalls));

        using var doc = JsonDocument.Parse(row.ToolCalls);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("call_a", arr[0].GetProperty("id").GetString());
        Assert.Equal("结果A", arr[0].GetProperty("result").GetString());
        Assert.Equal("call_b", arr[1].GetProperty("id").GetString());
        Assert.Equal("结果B", arr[1].GetProperty("result").GetString());
    }

    [Fact]
    public async Task Store_ToolMessageSingleFrc_KeepsLegacyFormat()
    {
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_1", "结果1"));

        await InvokeStoreAsync(responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "tool")
            .FirstOrDefaultAsync();

        // 单 FRC 保持旧格式（ToolCallId + Content，ToolCalls 列空），兼容存量数据
        Assert.NotNull(row);
        Assert.Equal("call_1", row.ToolCallId);
        Assert.Equal("结果1", row.Content);
        Assert.Null(row.ToolCalls);
    }

    [Fact]
    public async Task Store_SameStateBagRunId_TwoBatchesShareRunIdAndKeepIdOrder()
    {
        // 模拟 FICC 两次迭代：StateBag 同一 RunId，两次 Store 写入的所有行共享该 run_id（对应 spec「Store 读取 StateBag 为所有 FICC 迭代打同一 run_id」）
        var runId = Guid.NewGuid();
        _session.StateBag.SetValue("RunId", runId.ToString());

        // 第 1 次 Store（FICC 迭代 1）：user + assistant(FCC)
        var asstMsg = new AgentChatMessage(ChatRole.Assistant, "正在查询");
        asstMsg.Contents.Add(new FunctionCallContent("call_1", "search_product",
            new Dictionary<string, object?> { ["q"] = "手机" }));
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "你好")],
            responseMessages: [asstMsg]);

        // 第 2 次 Store（FICC 迭代 2）：tool 结果消息
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_1", "查询结果"));
        await InvokeStoreAsync(requestMessages: [], responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 两次 Store 的所有行共享同一 run_id
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(runId, r.RunId));

        // 组内按 id 升序保持时序（写入顺序 = id 顺序），时序为 user → assistant(FCC) → tool
        var ids = rows.Select(r => r.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);
        Assert.Equal("user", rows[0].Role);
        Assert.Equal("你好", rows[0].Content);
        Assert.Equal("assistant", rows[1].Role);
        Assert.NotNull(rows[1].ToolCalls);
        Assert.Equal("tool", rows[2].Role);
        Assert.Equal("call_1", rows[2].ToolCallId);
    }

    [Fact]
    public async Task Store_NoStateBagRunId_EachBatchGetsIndependentRunId()
    {
        // StateBag 无 RunId → 兜底生成独立 run_id：每批自成独立轮次，写入正常不抛异常（对应 spec「无 RunId 时兜底生成独立 run_id」）
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "第一轮提问")],
            responseMessages: [new AgentChatMessage(ChatRole.Assistant, "第一轮回复")]);

        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "第二轮提问")],
            responseMessages: [new AgentChatMessage(ChatRole.Assistant, "第二轮回复")]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 两批均正常落库（不抛异常），共 4 行
        Assert.Equal(4, rows.Count);

        // 每批各自独立 run_id：两批 run_id 不同，共 2 个不同 run_id
        var runIds = rows.Select(r => r.RunId).ToList();
        Assert.DoesNotContain(runIds, r => r is null);
        Assert.Equal(2, runIds.Distinct().Count());

        // 同一批内的行共享同一 run_id（每批自成独立轮次），跨批 run_id 不同
        Assert.Equal(rows[0].RunId, rows[1].RunId);
        Assert.Equal(rows[2].RunId, rows[3].RunId);
        Assert.NotEqual(rows[0].RunId, rows[2].RunId);

        // 组内按 id 升序保持时序（第一轮两行在前，第二轮两行在后）
        var ids = rows.Select(r => r.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);
        Assert.Equal("第一轮提问", rows[0].Content);
        Assert.Equal("第一轮回复", rows[1].Content);
        Assert.Equal("第二轮提问", rows[2].Content);
        Assert.Equal("第二轮回复", rows[3].Content);
    }

    [Fact]
    public async Task Store_LastMessagePlainTextAssistant_MarksIsFinalTrue()
    {
        // 批末条为纯文本 assistant（无 tool_calls / FunctionCallContent，即 FICC 迭代结束信号）
        // → 该行落库时 IsFinal=true，作为本轮轮次终点标记（对应 spec「Store 内判定写 is_final（末条纯文本 assistant）」）
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "你好")],
            responseMessages: [new AgentChatMessage(ChatRole.Assistant, "最终回复")]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // user + assistant 两行；user 行恒为 false，末条纯文本 assistant 行为轮次终点标记
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsFinal);
        Assert.True(rows[1].IsFinal);
        Assert.Equal("assistant", rows[1].Role);
        Assert.Equal("最终回复", rows[1].Content);
    }

    [Fact]
    public async Task Store_LastMessageAssistantFcc_NoRowIsFinal()
    {
        // 批末条为 assistant(FCC)（FICC 中间态，含 tool_calls）→ 非纯文本 assistant，本批不落 is_final
        // （对应 spec「Store 内判定写 is_final」反例：末条非纯文本不落）
        var asstMsg = new AgentChatMessage(ChatRole.Assistant, "正在查询");
        asstMsg.Contents.Add(new FunctionCallContent("call_1", "search_product",
            new Dictionary<string, object?> { ["q"] = "手机" }));

        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "你好")],
            responseMessages: [asstMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 末条为 assistant(FCC)，非纯文本 → 该行 is_final=false，本批无 is_final 行
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsFinal));
        Assert.Equal("assistant", rows[1].Role);
        Assert.NotNull(rows[1].ToolCalls);
        Assert.False(rows[1].IsFinal);
    }

    [Fact]
    public async Task Store_LastMessageTool_NoRowIsFinal()
    {
        // 批末条为 tool（FICC 中间态，tool 结果消息）→ 非纯文本 assistant，本批不落 is_final
        // （对应 spec「Store 内判定写 is_final」反例：末条为 tool 行不落）
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_1", "查询结果"));

        await InvokeStoreAsync(
            requestMessages: [],
            responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("tool", row.Role);
        Assert.False(row.IsFinal);
    }

    [Fact]
    public async Task Store_AppendsThenTrimsToStoredLimit()
    {
        // 15 条 NULL run_id 历史行（每行自成一组）+ 1 个完整轮（2 行）= 16 完整轮，超过 K=12 → 压缩最旧 4 组（4 行）
        SeedMessages(15);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            // 压缩只标记 is_compacted=true 不物理删除，总行数不变：15 + 2 = 17
            var count = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId)
                .CountAsync();
            Assert.Equal(17, count);

            // 剩余未压缩 13 行 = 11 条 NULL 历史行 + 1 个完整轮（2 行），共 12 轮
            var uncompacted = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId && !m.IsCompacted)
                .CountAsync();
            Assert.Equal(13, uncompacted);
        }
    }

    [Fact]
    public async Task Store_TrimsWhenExceedsLimit()
    {
        // 55 条 NULL run_id 历史行（每行自成一组）+ 1 个完整轮 = 56 完整轮，压缩最旧 44 组直到剩余完整轮数 ≤ K=12
        SeedMessages(55);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            // 55 种子 + 2 新增 = 57 行；压缩 44 行后未压缩应为 13 行（11 条 NULL 行 + 1 个完整轮 2 行 = 12 轮）
            var uncompacted = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId && !m.IsCompacted)
                .CountAsync();
            Assert.Equal(13, uncompacted);
        }
    }

    [Fact]
    public async Task Store_TrimsToAtMostKCompleteRounds()
    {
        // 30 个完整轮（各 2 行，末条 is_final）+ 1 个新完整轮 = 31 完整轮 → 压缩最旧 19 轮直到剩余完整轮数 ≤ K=12
        SeedRounds(30);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            var rows = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId && !m.IsCompacted)
                .OrderBy(m => m.Id)
                .ToListAsync();

            // 剩余 12 个完整轮 × 2 行 = 24 行未压缩；压缩区与保留区均以轮为边界
            Assert.Equal(24, rows.Count);
            Assert.Equal(12, rows.GroupBy(r => r.RunId).Count());
        }
    }

    [Fact]
    public async Task Store_CompressesAtRoundBoundaries_KeepsFccToolPairsTogether()
    {
        // 构造 14 个含工具调用的完整轮（每轮 4 行：user → assistant(FCC) → tool → assistant 最终回复，
        // 同 run_id，末条 IsFinal=true），触发压缩后断言：保留区与压缩区均以轮为边界、
        // 同轮 FCC↔tool 配对结构性不分离（对应 spec「压缩按 run_id 整轮整切不拆轮」）
        SeedToolRounds(14);

        // Store 再落 1 个新完整轮（user + assistant 纯文本轮）→ 共 15 完整轮 > K=12，
        // 从最旧完整轮整轮压缩直到剩余 ≤ K=12 → 压缩最旧 3 轮（每轮 4 行 = 12 行）
        await InvokeStoreAsync();

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 压缩只标记 is_compacted=true 不物理删除：总行数 = 14×4 + 2 = 58
        Assert.Equal(58, rows.Count);

        // 15 个不同 run_id（14 个工具轮 + 1 个新轮）
        var grouped = rows.GroupBy(r => r.RunId).ToList();
        Assert.Equal(15, grouped.Count);

        // 保留区与压缩区均以轮为边界：同一 run_id 组内所有行压缩状态一致（整轮整切，无半轮被拆散）
        Assert.All(grouped, g =>
            Assert.True(
                g.All(r => r.IsCompacted) || !g.Any(r => r.IsCompacted),
                $"run_id {g.Key} 同一轮被拆散：部分行压缩、部分行保留"));

        // 保留 12 轮（46 行 = 11 工具轮×4 + 新轮 2 行），压缩 3 轮（12 行，均为最旧的工具轮）
        var uncompacted = rows.Where(r => !r.IsCompacted).ToList();
        var compacted = rows.Where(r => r.IsCompacted).ToList();
        Assert.Equal(12, uncompacted.GroupBy(r => r.RunId).Count());
        Assert.Equal(3, compacted.GroupBy(r => r.RunId).Count());
        Assert.Equal(46, uncompacted.Count);
        Assert.Equal(12, compacted.Count);

        // 压缩的是最旧 3 整轮：其行 id 恰为全量行中 id 最小的 12 行
        Assert.Equal(
            rows.Take(12).Select(r => r.Id).OrderBy(x => x),
            compacted.Select(r => r.Id).OrderBy(x => x));

        // 同轮 FCC↔tool 结构性不分离：压缩区每个工具轮 4 行同被压缩，
        // 未出现 assistant(FCC) 被压而 tool 保留、或反之的孤儿配对
        foreach (var round in compacted.GroupBy(r => r.RunId))
        {
            var roundRows = round.ToList();
            Assert.Equal(4, roundRows.Count);
            // 该轮含 assistant(FCC) 行（ToolCalls 非空）与 tool 行，且整轮同被压缩
            Assert.Contains(roundRows, r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls));
            Assert.Contains(roundRows, r => r.Role == "tool");
            Assert.All(roundRows, r => Assert.True(r.IsCompacted));
        }
    }

    [Fact]
    public async Task Store_IncompleteOldRound_KeptWholeNotCompacted()
    {
        // 无 is_final 的旧轮（FICC 迭代耗尽 / 异常中断 / 仅调工具未输出最终回复，组内无 is_final=true 行）
        // → 触发压缩时整组保留不被压缩，即使它是最旧的消息（对应 spec「未完成轮整组保留」）
        SeedIncompleteToolRound(); // 1 个未完成轮（3 行：user → assistant(FCC) → tool，无 is_final，最旧的 3 行）
        SeedRounds(14);            // 14 个完整轮（各 2 行，末条 is_final=true）

        // Store 再落 1 个新完整轮（user + assistant 纯文本轮）→ 完整轮共 15 个 > K=12，
        // 从最旧完整轮整轮压缩直到剩余 ≤ K=12 → 压缩最旧 3 个完整轮（6 行）；
        // 未完成轮不在「可压缩完整轮」集合内，整组保留不被压缩
        await InvokeStoreAsync();

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // 总行数 = 3（未完成轮）+ 14×2（完整轮）+ 2（新轮）= 33；压缩只标 is_compacted=true 不物理删除
        Assert.Equal(33, rows.Count);

        // 未完成轮（最旧的 3 行 id 1-3）整组保留：均未被压缩，即使它是最旧的消息
        var incomplete = rows.Take(3).ToList();
        Assert.All(incomplete, r => Assert.False(r.IsCompacted));

        // 未完成轮组内 3 行共享同一 run_id 且与所有完整轮 run_id 不同（独立轮组）
        var incompleteRunId = incomplete[0].RunId;
        Assert.NotNull(incompleteRunId);
        Assert.Equal(3, rows.Count(r => r.RunId == incompleteRunId));
        Assert.DoesNotContain(rows.Skip(3), r => r.RunId == incompleteRunId);

        // 被压缩的是最旧 3 个完整轮（6 行，id 4-9）：未完成轮（id 1-3）先于它们落库却保留，
        // 完整轮反而被压——证明保留的唯一原因是「未完成轮整组保留」而非新旧
        var compacted = rows.Where(r => r.IsCompacted).ToList();
        Assert.Equal(6, compacted.Count);
        Assert.Equal(
            rows.Skip(3).Take(6).Select(r => r.Id).OrderBy(x => x),
            compacted.Select(r => r.Id).OrderBy(x => x));

        // 保留区以轮为边界：未压缩 27 行 = 13 个轮组（1 未完成轮 + 11 完整轮 + 1 新轮），且未完成轮组内 FCC↔tool 配对完整
        var uncompacted = rows.Where(r => !r.IsCompacted).ToList();
        Assert.Equal(13, uncompacted.GroupBy(r => r.RunId).Count());
        Assert.Equal(27, uncompacted.Count);
        Assert.Contains(uncompacted, r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls));
        Assert.Contains(uncompacted, r => r.Role == "tool");
    }

    [Fact]
    public async Task Store_MultiFrcToolPair_CompressedTogetherWithAssistantFcc()
    {
        // seed 49 条：id1 = assistant(FCC)（ToolCalls 非空）、id2 = 多 FRC tool 行（ToolCalls 为 JSON 数组）、id3-49 = 普通 user/assistant
        var fccJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        var toolResultsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_a", result = "结果A" },
            new { id = "call_b", result = "结果B" },
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            // id1：assistant(FCC)，与紧随的多 FRC tool 行配对
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "",
                ToolCalls = fccJson,
            });
            // id2：多 FRC tool 行——ToolCalls 列存 JSON 数组、ToolCallId 置空、单行不拆行
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "",
                ToolCalls = toolResultsJson,
                ToolCallId = null,
            });
            // id3-49：47 条普通消息
            for (int i = 3; i <= 49; i++)
            {
                seed.ChatMessageRecords.Add(new ChatMessageRecord
                {
                    SessionId = _sessionId,
                    Role = i % 2 == 0 ? "assistant" : "user",
                    Content = $"消息{i}",
                });
            }
            await seed.SaveChangesAsync();
        }

        // Store 默认再增 1 个完整轮（user + assistant）→ 共 50 完整轮（49 条 NULL 行各自成组 + 1 轮），
        // 压缩最旧 38 组直到剩余完整轮数 ≤ K=12；id1/id2 是最旧两行，整组一并压缩、FCC↔tool 配对不分离
        await InvokeStoreAsync();

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var rows = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // id1（assistant FCC）与 id2（多 FRC tool 行）同生共死：同被压缩，
        // 不出现 "assistant kept + tool 被裁" 的孤儿配对
        var row1 = rows[0];
        Assert.Equal("assistant", row1.Role);
        Assert.True(row1.IsCompacted);
        Assert.False(string.IsNullOrEmpty(row1.ToolCalls));

        var row2 = rows[1];
        Assert.Equal("tool", row2.Role);
        Assert.True(row2.IsCompacted);
        Assert.Equal("call_a", JsonDocument.Parse(row2.ToolCalls!).RootElement[0].GetProperty("id").GetString());

        // 压缩最旧 38 组（id1/id2 及 id3-38 各 NULL 行），剩余未压缩 = 51 - 38 = 13 行（id39-49 共 11 行 + 新轮 2 行）
        var uncompacted = rows.Count(r => !r.IsCompacted);
        Assert.Equal(13, uncompacted);
    }

    [Fact]
    public async Task Provide_RebuildsUserMessageAsTextContent()
    {
        SeedMessages(1, roles: ["user"], contents: ["你好"]);

        var result = await InvokeProvideAsync();

        var userMsg = Assert.Single(result);
        Assert.Equal(ChatRole.User, userMsg.Role);
        var textContent = Assert.Single(userMsg.Contents.OfType<TextContent>());
        Assert.Equal("你好", textContent.Text);
    }

    [Fact]
    public async Task Provide_RebuildsAssistantWithToolCalls()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{\"q\":\"手机\"}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "搜索中",
                ToolCalls = toolCallsJson,
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var asstMsg = Assert.Single(result);
        Assert.Equal(ChatRole.Assistant, asstMsg.Role);
        Assert.Single(asstMsg.Contents.OfType<TextContent>());
        var fcc = Assert.Single(asstMsg.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", fcc.CallId);
        Assert.Equal("search_product", fcc.Name);
    }

    [Fact]
    public async Task Provide_RebuildsToolMessageAsFunctionResultContent()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "",
                ToolCalls = toolCallsJson,
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "查询结果文本",
                ToolCallId = "call_1",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var toolMsg = Assert.Single(result, m => m.Role == ChatRole.Tool);
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        var frc = Assert.Single(toolMsg.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", frc.CallId);
        Assert.Equal("查询结果文本", frc.Result);
    }

    [Fact]
    public async Task Provide_RebuildsMultiFrcToolMessageFromToolCallsJson()
    {
        // seed 多 FRC tool 行：ToolCalls 列存 JSON 数组 [{id, result}, ...]，ToolCallId 置空
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_a", result = "结果A" },
            new { id = "call_b", result = "结果B" },
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "",
                ToolCalls = toolCallsJson,
                ToolCallId = null,
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var toolMsg = Assert.Single(result, m => m.Role == ChatRole.Tool);
        var frcs = toolMsg.Contents.OfType<FunctionResultContent>().ToList();
        // Contents 数量与 JSON 数组元素数量一致（2 个 FRC，不丢）
        Assert.Equal(2, frcs.Count);
        Assert.Equal("call_a", frcs[0].CallId);
        Assert.Equal("结果A", frcs[0].Result);
        Assert.Equal("call_b", frcs[1].CallId);
        Assert.Equal("结果B", frcs[1].Result);
    }

    [Fact]
    public async Task Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent()
    {
        // 旧格式兼容：ToolCalls 列空 + Content + ToolCallId（存量单 FRC 格式），零迁移读取路径
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "旧结果",
                ToolCallId = "call_1",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var toolMsg = Assert.Single(result, m => m.Role == ChatRole.Tool);
        var frc = Assert.Single(toolMsg.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", frc.CallId);
        Assert.Equal("旧结果", frc.Result);
    }

    [Fact]
    public async Task Provide_ToolCallsJsonDeserializationFailure_LogsWarningAndSkipsOrphanTool()
    {
        // 反序列化失败：ToolCalls 列非法 JSON → catch 记 Warning 不抛异常，contents 空 → 孤儿 tool 消息被过滤、不进入返回列表
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "",
                ToolCalls = "not-json",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        // 不抛异常（走 catch → Warning 日志），孤儿 tool 消息被过滤
        Assert.DoesNotContain(result, m => m.Role == ChatRole.Tool);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Provide_FiltersCompactedMessages()
    {
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "活跃消息",
                IsCompacted = false,
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "已压缩消息",
                IsCompacted = true,
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var msg = Assert.Single(result);
        Assert.Equal("活跃消息", msg.Text);
    }

    [Fact]
    public async Task Provide_SkipsPureFccNoTextAssistant()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            // 纯 tool_call 无文本的 assistant
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "",
                ToolCalls = toolCallsJson,
            });
            // 正常 user 消息
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "正常消息",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        // 纯 FCC assistant 需要保留（否则 tool 消息无法配对），所以返回 2 条
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.Assistant, result[0].Role);
        Assert.Contains("search_product", result[0].Contents.OfType<FunctionCallContent>().Single().Name);
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Equal("正常消息", result[1].Text);
    }

    [Fact]
    public async Task Provide_ReturnsMessagesInIdAscendingOrder()
    {
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "第一条",
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "回复第二条",
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "第三条",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("第一条", result[0].Text);
        Assert.Equal("回复第二条", result[1].Text);
        Assert.Equal("第三条", result[2].Text);
    }

    private async Task StoreAndLoad(string content)
    {
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, content)]);

        var result = await InvokeProvideAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "Hello")]);

        // Store: user(content) + assistant("Hi")
        // Provide: 过滤掉未配对 tool → just user + assistant
        Assert.Equal(2, result.Count);
        Assert.Equal(content, result[0].Text);
    }

    [Fact]
    public async Task StoreAndProvide_RoundTrip_PreservesContent()
    {
        await StoreAndLoad("你好！有什么可以帮您的？");
    }

    [Fact]
    public async Task StoreAndProvide_MultiFrcRoundTrip_PreservesAllToolResults()
    {
        // 多 FRC round-trip 全链路：Store 存 assistant(FCC 配对) + tool(2 FRC)
        // → tool 行单行 JSON 数组存 ToolCalls 列 → Provide 读回 2 个 FRC，CallId/Result 与存入时一一对应、不丢
        var asstMsg = new AgentChatMessage(ChatRole.Assistant, "搜索中");
        asstMsg.Contents.Add(new FunctionCallContent("call_a", "search_product",
            new Dictionary<string, object?> { ["q"] = "手机" }));
        asstMsg.Contents.Add(new FunctionCallContent("call_b", "get_price",
            new Dictionary<string, object?> { ["id"] = 1 }));

        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_a", "结果A"));
        toolMsg.Contents.Add(new FunctionResultContent("call_b", "结果B"));

        await InvokeStoreAsync(responseMessages: [asstMsg, toolMsg]);

        var result = await InvokeProvideAsync();

        // assistant(FCC) 配对读回：2 个 FunctionCallContent 均保留
        var asstOut = Assert.Single(result, m => m.Role == ChatRole.Assistant);
        var fccs = asstOut.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, fccs.Count);
        Assert.Equal("call_a", fccs[0].CallId);
        Assert.Equal("call_b", fccs[1].CallId);

        // tool 消息读回 2 个 FunctionResultContent，CallId/Result 与存入时一致（存一行 → 读回多 FRC）
        var toolOut = Assert.Single(result, m => m.Role == ChatRole.Tool);
        var frcs = toolOut.Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, frcs.Count);
        Assert.Equal("call_a", frcs[0].CallId);
        Assert.Equal("结果A", frcs[0].Result);
        Assert.Equal("call_b", frcs[1].CallId);
        Assert.Equal("结果B", frcs[1].Result);
    }

    private void SeedMessages(int count, string[]? roles = null, string[]? contents = null)
    {
        using var ctx = _dbFactory.CreateDbContext();
        for (int i = 0; i < count; i++)
        {
            string role;
            if (roles is not null && i < roles.Length)
                role = roles[i];
            else
                role = i % 2 == 0 ? "user" : "assistant";

            string content;
            if (contents is not null && i < contents.Length)
                content = contents[i];
            else
                content = $"消息{i}";

            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = role,
                Content = content,
            });
        }
        ctx.SaveChanges();
    }

    /// <summary>
    /// 按轮 seed：每轮两行（user + assistant），共享同一 run_id，末条 assistant 标 IsFinal=true（纯文本轮）。
    /// </summary>
    private void SeedRounds(int roundCount)
    {
        using var ctx = _dbFactory.CreateDbContext();
        for (var i = 0; i < roundCount; i++)
        {
            var runId = Guid.NewGuid();
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "user",
                Content = $"第{i}轮问题",
            });
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "assistant",
                Content = $"第{i}轮回复",
                IsFinal = true,
            });
        }
        ctx.SaveChanges();
    }

    /// <summary>
    /// 按含工具调用的完整轮 seed：每轮 4 行（user → assistant(FCC) → tool → assistant 最终回复），
    /// 共享同一 run_id，末条 assistant 标 IsFinal=true（对应真实 FICC 工具调用轮的落库结构）。
    /// </summary>
    private void SeedToolRounds(int roundCount)
    {
        using var ctx = _dbFactory.CreateDbContext();
        for (var i = 0; i < roundCount; i++)
        {
            var runId = Guid.NewGuid();
            var callId = $"call_{i}";

            var fccJson = JsonSerializer.Serialize(new[]
            {
                new { id = callId, type = "function", function = new { name = "search_product", arguments = "{}" } }
            }, JsonOptions);

            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "user",
                Content = $"第{i}轮问题",
            });
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "assistant",
                Content = "正在查询",
                ToolCalls = fccJson,
            });
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "tool",
                Content = "查询结果",
                ToolCallId = callId,
            });
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                RunId = runId,
                Role = "assistant",
                Content = $"第{i}轮回复",
                IsFinal = true,
            });
        }
        ctx.SaveChanges();
    }

    /// <summary>
    /// seed 一个未完成工具调用轮（3 行：user → assistant(FCC) → tool），共享同一 run_id，
    /// 组内无 is_final=true 行（模拟 FICC 迭代耗尽 / 异常中断 / 仅调工具未输出最终回复的未完成轮）。
    /// </summary>
    private void SeedIncompleteToolRound()
    {
        using var ctx = _dbFactory.CreateDbContext();
        var runId = Guid.NewGuid();
        var callId = "call_inc";

        var fccJson = JsonSerializer.Serialize(new[]
        {
            new { id = callId, type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        ctx.ChatMessageRecords.Add(new ChatMessageRecord
        {
            SessionId = _sessionId,
            RunId = runId,
            Role = "user",
            Content = "未完成轮问题",
        });
        ctx.ChatMessageRecords.Add(new ChatMessageRecord
        {
            SessionId = _sessionId,
            RunId = runId,
            Role = "assistant",
            Content = "正在查询",
            ToolCalls = fccJson,
        });
        ctx.ChatMessageRecords.Add(new ChatMessageRecord
        {
            SessionId = _sessionId,
            RunId = runId,
            Role = "tool",
            Content = "查询结果",
            ToolCallId = callId,
        });
        ctx.SaveChanges();
    }

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
