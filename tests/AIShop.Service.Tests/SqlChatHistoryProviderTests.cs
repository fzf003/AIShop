#pragma warning disable MAAI001 // ChatHistoryProvider.InvokingContext / InvokedContext 构造属 MAF [Experimental]
using System.Reflection;
using System.Text.Json;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace AIShop.Service.Tests;

/// <summary>
/// <see cref="AIShop.Service.Agui.SqlChatHistoryProvider"/>（design-sql-chat-history-provider §1–§6 / §8）测试。
/// 用临时 SQLite 文件（provider 每次操作独立开连接，故不能用 <c>:memory:</c>）+ 反射调用 protected 的
/// <c>ProvideChatHistoryAsync</c> / <c>StoreChatHistoryAsync</c>（对齐既有 SqliteChatHistoryProviderTests 模式）。
/// </summary>
public sealed class SqlChatHistoryProviderTests : IDisposable
{
    private static readonly Type ProviderType = typeof(AIShop.Service.Agui.SqlChatHistoryProvider);
    private static readonly MethodInfo ProvideMethod = ProviderType.GetMethod(
        "ProvideChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StoreMethod = ProviderType.GetMethod(
        "StoreChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly string _conversationId = Guid.NewGuid().ToString("N");
    private readonly AIShop.Service.Agui.SqlChatHistoryProvider _provider;
    private readonly TestSession _session;

    public SqlChatHistoryProviderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlchat_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        var options = new AIShop.Service.Agui.SqlChatHistoryOptions
        {
            ConnectionString = _connectionString,
            MaxRoundsToLoad = 2
        };
        // 注入确定性会话标识（默认初始化器生成 GUID，测试需要可断言的稳定 id）。
        _provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            options,
            _ => new AIShop.Service.Agui.SqlChatHistoryProvider.State(_conversationId));
        _session = new TestSession();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    // ---------- 写入 + 轮次计算（含前导 system 归 round 1） ----------

    [Fact]
    public async Task Store_LeadingSystemMessageBeforeFirstUser_BelongsToRoundOne()
    {
        // design §2.3 示例表口径：前导 system（首轮 user 之前）属 round 1；user 触发新轮从 0→1；assistant 属本轮。
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.System, "你是购物助手"), new ChatMessage(ChatRole.User, "找跑鞋")],
            [new ChatMessage(ChatRole.Assistant, "为您找到专业跑鞋")]);

        var rows = ReadRows();
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Sequence));           // sequence 顺序递增
        Assert.Equal(new[] { "system", "user", "assistant" }, rows.Select(r => r.Role));
        Assert.Equal(new[] { 1, 1, 1 }, rows.Select(r => r.RoundId));            // 前导 system 与本轮 user/assistant 同属 round 1

        // 第 2 轮：user 触发 round 2
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "加购")],
            [new ChatMessage(ChatRole.Assistant, "已加入购物车")]);

        var allRows = ReadRows();
        Assert.Equal(5, allRows.Count);
        Assert.Equal(new[] { 1, 1, 1, 2, 2 }, allRows.Select(r => r.RoundId));   // round_id 正确递增
    }

    [Fact]
    public async Task Store_LeadingNonUserMessageOnEmptyConversation_NeverProducesRoundZero()
    {
        // 防御 design §3.1 代码缺陷：新会话 currentRound=0 且批首非 User 时，round_id 不得为 0（§2.3 要求从 1 起）。
        await InvokeStoreAsync(
            _session,
            [],
            [new ChatMessage(ChatRole.Assistant, "系统开场白")]);

        var rows = ReadRows();
        var row = Assert.Single(rows);
        Assert.Equal(1, row.RoundId);
    }

    // ---------- FCC / FRC 序列化往返保真（工具配对前提） ----------

    [Fact]
    public async Task StoreAndProvide_RoundTripsFunctionCallAndResultContent_WithCallIdPairing()
    {
        var assistant = new ChatMessage(ChatRole.Assistant, "正在查询");
        assistant.Contents.Add(new FunctionCallContent(
            "call_1", "search_product", new Dictionary<string, object?> { ["q"] = "手机" }));

        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.Contents.Add(new FunctionResultContent("call_1", "查询结果：手机 ¥1999"));

        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "找手机")],
            [assistant, tool]);

        var result = await InvokeProvideAsync(_session);

        // assistant 的 FunctionCallContent 读回：CallId / Name 不变，参数仍可读
        var assistantOut = Assert.Single(result, m => m.Role == ChatRole.Assistant);
        var fcc = Assert.Single(assistantOut.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", fcc.CallId);
        Assert.Equal("search_product", fcc.Name);
        Assert.NotNull(fcc.Arguments);
        Assert.True(fcc.Arguments!.ContainsKey("q"));
        Assert.Equal("手机", ToPlainString(fcc.Arguments["q"]));

        // tool 的 FunctionResultContent 读回：与 assistant FCC 的 CallId 配对完整、结果可读
        var toolOut = Assert.Single(result, m => m.Role == ChatRole.Tool);
        var frc = Assert.Single(toolOut.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", frc.CallId);
        Assert.Equal("查询结果：手机 ¥1999", ToPlainString(frc.Result));

        // 整轮顺序保持：user → assistant(FCC) → tool(FRC)
        Assert.Equal(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool },
            result.Select(m => m.Role));
    }

    [Fact]
    public async Task Store_MultiFunctionContentInOneMessage_RoundTripsAllPolymorphicContents()
    {
        // 同一条 assistant 消息承载 TextContent + 两个 FunctionCallContent，须同序无损往返（多态序列化保真）。
        var assistant = new ChatMessage(ChatRole.Assistant, "并行查询");
        assistant.Contents.Add(new FunctionCallContent("call_a", "search_product", new Dictionary<string, object?> { ["q"] = "鞋" }));
        assistant.Contents.Add(new FunctionCallContent("call_b", "get_stock_quote", new Dictionary<string, object?> { ["symbol"] = "MSFT" }));

        await InvokeStoreAsync(_session, [new ChatMessage(ChatRole.User, "查一下")], [assistant]);

        var result = await InvokeProvideAsync(_session);
        var assistantOut = Assert.Single(result, m => m.Role == ChatRole.Assistant);

        Assert.Single(assistantOut.Contents.OfType<TextContent>());
        var fccs = assistantOut.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, fccs.Count);
        Assert.Equal("call_a", fccs[0].CallId);
        Assert.Equal("call_b", fccs[1].CallId);
        Assert.Equal("MSFT", ToPlainString(fccs[1].Arguments!["symbol"]));
    }

    // ---------- 读取：MaxRoundsToLoad / 软删除不可见 ----------

    [Fact]
    public async Task Provide_MaxRoundsToLoad_ReturnsOnlyMostRecentNonDeletedRounds()
    {
        // 4 轮（每轮 user + assistant），第 3 轮软删除 → MaxRoundsToLoad=2 只返回最近 2 个非删除轮（round 2 与 round 4）。
        await StoreSimpleRound("问题1", "回答1");
        await StoreSimpleRound("问题2", "回答2");
        await StoreSimpleRound("问题3", "回答3");
        await StoreSimpleRound("问题4", "回答4");

        SoftDeleteRound(3);

        var result = await InvokeProvideAsync(_session);

        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { "问题2", "回答2", "问题4", "回答4" }, result.Select(m => m.Text));
        Assert.DoesNotContain(result, m => m.Text == "问题1"); // round 1 非「最近 2 个非删除轮」之一
        Assert.DoesNotContain(result, m => m.Text == "问题3"); // round 3 已软删除
    }

    [Fact]
    public async Task Provide_AllRoundsSoftDeleted_ReturnsEmpty()
    {
        await StoreSimpleRound("问题1", "回答1");
        SoftDeleteRound(1);

        var result = await InvokeProvideAsync(_session);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Provide_EmptyConversation_ReturnsEmpty()
    {
        var result = await InvokeProvideAsync(_session);
        Assert.Empty(result);
    }

    // ---------- TTL 整轮软删除 ----------

    [Fact]
    public async Task CleanupExpiredRounds_SoftDeletesWholeRound_WithoutSplitting()
    {
        await StoreSimpleRound("旧问题", "旧回答");
        await StoreSimpleRound("新问题", "新回答");

        BackdateRound(1, DateTimeOffset.Now.AddDays(-100));

        var deletedRounds = await _provider.CleanupExpiredRoundsAsync(ttlDays: 30);

        Assert.Equal(1, deletedRounds);

        // round 1 整轮两行一并软删除（不拆断），deleted_at 已写；round 2 原样保留
        var rows = ReadRows();
        var round1 = rows.Where(r => r.RoundId == 1).ToList();
        Assert.Equal(2, round1.Count);
        Assert.All(round1, r =>
        {
            Assert.True(r.IsDeleted);
            Assert.False(string.IsNullOrEmpty(r.DeletedAt));
        });

        var round2 = rows.Where(r => r.RoundId == 2).ToList();
        Assert.Equal(2, round2.Count);
        Assert.All(round2, r => Assert.False(r.IsDeleted));

        // 软删除轮读取不可见：Provide 只剩 round 2
        var result = await InvokeProvideAsync(_session);
        Assert.Equal(new[] { "新问题", "新回答" }, result.Select(m => m.Text));
    }

    [Fact]
    public async Task CleanupExpiredRounds_MultipleExpiredRounds_ProcessesAllBatches()
    {
        // 5 轮，前 4 轮过期，batchSize=2 → 循环分 2 批（2+2）后无更多 → 共删 4 轮、全部整轮删除
        for (var i = 1; i <= 5; i++)
            await StoreSimpleRound($"问题{i}", $"回答{i}");

        for (var i = 1; i <= 4; i++)
            BackdateRound(i, DateTimeOffset.Now.AddDays(-100));

        var deletedRounds = await _provider.CleanupExpiredRoundsAsync(ttlDays: 30, batchSize: 2);

        Assert.Equal(4, deletedRounds);

        var rows = ReadRows();
        for (var round = 1; round <= 4; round++)
        {
            var roundRows = rows.Where(r => r.RoundId == round).ToList();
            Assert.Equal(2, roundRows.Count);
            Assert.All(roundRows, r => Assert.True(r.IsDeleted)); // 整轮删除
        }

        Assert.All(rows.Where(r => r.RoundId == 5), r => Assert.False(r.IsDeleted));
    }

    [Fact]
    public async Task CleanupExpiredRounds_TtlDisabled_ReturnsZeroAndDeletesNothing()
    {
        await StoreSimpleRound("问题1", "回答1");
        BackdateRound(1, DateTimeOffset.Now.AddDays(-100));

        var deletedRounds = await _provider.CleanupExpiredRoundsAsync(ttlDays: 0);

        Assert.Equal(0, deletedRounds);
        Assert.All(ReadRows(), r => Assert.False(r.IsDeleted));
    }

    // ---------- 事务原子性 ----------

    [Fact]
    public async Task Store_BatchConflictsWithExistingUniqueSequence_RollsBackWholeBatchAndThrows()
    {
        // 预置一条【软删除】行占用 sequence=2（不进 MAX 计算、读取不可见，但仍受 UNIQUE(conversation_id, sequence) 约束）。
        // 随后写入 3 条：第 1 条 seq=1 成功，第 2 条 seq=2 触发 UNIQUE 冲突 → 整批回滚 + 乐观重试 3 次后抛异常。
        // 断言：第 1 条也被回滚（非删除行 0 条），证明确有事务原子性（要么全写、要么全回滚）。
        await _provider.InitializeAsync();
        ExecuteSql(
            "INSERT INTO chat_messages (conversation_id, sequence, role, message_json, round_id, is_deleted, deleted_at, created_at) " +
            "VALUES ($c, 2, 'user', '{}', 1, 1, $now, $now)",
            ("$c", _conversationId),
            ("$now", DateTimeOffset.Now.ToString("O")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "第一条"), new ChatMessage(ChatRole.User, "第二条"), new ChatMessage(ChatRole.User, "第三条")],
            []));

        var nonDeleted = ReadRows().Count(r => !r.IsDeleted);
        Assert.Equal(0, nonDeleted); // 第 1 条 seq=1 已随整批回滚，未被部分写入
    }

    // ---------- 幂等 / 重复写入不产生重复行 ----------

    [Fact]
    public async Task Store_SameBatchTwice_AppendsWithUniqueSequences_NoDuplicateRows()
    {
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "你好")],
            [new ChatMessage(ChatRole.Assistant, "您好")]);
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "你好")],
            [new ChatMessage(ChatRole.Assistant, "您好")]);

        var rows = ReadRows();
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Sequence));
        // 每条 sequence 唯一（UNIQUE 约束下不产生重复行）
        Assert.Equal(rows.Count, rows.Select(r => r.Sequence).Distinct().Count());
        Assert.Equal(new[] { "你好", "您好", "你好", "您好" }, rows.Select(r => r.Text));
    }

    // ---------- 会话标识机制：经 StateBag 往返后仍定位同一会话 ----------

    [Fact]
    public async Task Provide_AfterStateBagRoundTrip_LoadsSameConversation()
    {
        // 会话标识存 AgentSession.StateBag（随会话快照被 SqliteAgentSessionStore 持久化）。
        // 这里对 StateBag 做序列化 → 反序列化 → 新会话实例，模拟「跨请求 / 重启还原」，仍应命中同一会话历史。
        await StoreSimpleRound("问题1", "回答1");

        using var document = JsonDocument.Parse(_session.StateBag.Serialize().GetRawText());
        var restoredBag = AgentSessionStateBag.Deserialize(document.RootElement);
        var restoredSession = new TestSession(restoredBag);

        var result = await InvokeProvideAsync(restoredSession);

        Assert.Equal(new[] { "问题1", "回答1" }, result.Select(m => m.Text));
    }

    // ---------- conversation_id == ThreadId（SqliteAgentSessionStore 写、provider 读） ----------

    [Fact]
    public async Task ConversationId_EqualsThreadIdWrittenBySessionStore()
    {
        // 端到端串联：store.GetSessionAsync(ThreadId) 把 ThreadId 写进会话 StateBag 共享键 → provider（默认初始化器）
        // 读它作为会话标识 → 落库 chat_messages.conversation_id == ThreadId（design §2.2「会话标识（ThreadId）」、
        // §10.3 按 conversation_id='thread-...' 审计 / 召回）。
        const string threadId = "thread-abc-123";

        var store = new SqliteAgentSessionStore(_connectionString);
        await store.InitializeAsync();
        var session = await store.GetSessionAsync(CreateBareAgent("ConversationAgent"), threadId);

        // store 已把本次取会话的 ThreadId 写入 StateBag 的共享键
        Assert.True(session.StateBag.TryGetValue<string>(AguiSessionStateKeys.ConversationId, out var tagged));
        Assert.Equal(threadId, tagged);

        // provider 用【默认】初始化器（读共享键），非测试构造时注入的确定性 initializer
        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = _connectionString });
        await InvokeStoreAsync(provider, session,
            [new ChatMessage(ChatRole.User, "找跑鞋")],
            [new ChatMessage(ChatRole.Assistant, "为您找到专业跑鞋")]);

        Assert.Equal(new[] { threadId }, ReadConversationIds());
    }

    [Fact]
    public async Task ConversationId_FallsBackToGeneratedId_WhenStateBagHasNoThreadId()
    {
        // 未接线（直构 provider、StateBag 无共享键）→ 回退生成 GUID 仍可用、不崩；
        // 同一会话多次写入落同一 conversation_id（回退 id 在会话内稳定）。
        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = _connectionString });
        var session = new TestSession();

        await InvokeStoreAsync(provider, session,
            [new ChatMessage(ChatRole.User, "第一轮")], [new ChatMessage(ChatRole.Assistant, "回复一")]);
        await InvokeStoreAsync(provider, session,
            [new ChatMessage(ChatRole.User, "第二轮")], [new ChatMessage(ChatRole.Assistant, "回复二")]);

        var id = Assert.Single(ReadConversationIds());
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.NotEqual(_conversationId, id); // 确系会话内自生成，而非测试注入的确定性 id
    }

    [Fact]
    public async Task ConversationId_SurvivesStateBagRoundTrip_StaysThreadId()
    {
        // 重启续聊路径：store 取会话（写入 ThreadId）→ StateBag 序列化往返 → 新会话实例 → provider 落库，
        // conversation_id 仍等于 ThreadId、不漂移。
        const string threadId = "thread-resume-42";

        var store = new SqliteAgentSessionStore(_connectionString);
        await store.InitializeAsync();
        var original = await store.GetSessionAsync(CreateBareAgent("ConversationAgent"), threadId);

        using var document = JsonDocument.Parse(original.StateBag.Serialize().GetRawText());
        var restored = new TestSession(AgentSessionStateBag.Deserialize(document.RootElement));

        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = _connectionString });
        await InvokeStoreAsync(provider, restored,
            [new ChatMessage(ChatRole.User, "继续聊")],
            [new ChatMessage(ChatRole.Assistant, "好的")]);

        Assert.Equal(new[] { threadId }, ReadConversationIds());
    }

    // ---------- 只处理「当前轮次」：客户端重发整段前文不重复入库 / 不重复进上下文 ----------

    [Fact]
    public async Task Store_ClientResendsFullHistory_DoesNotGrowRows()
    {
        // 缺陷回归：AG-UI 客户端每次把整段前文一起发来。第 2 次请求（新会话，模拟跨请求）重发第 1 轮 + 新消息，
        // 落库必须只新增第 2 轮 → 共 4 行（而非把重发的第 1 轮再存一遍 = 6 行）。
        const string conversationId = "conv-full-resend";

        var provider1 = NewProvider(conversationId);
        await InvokeStoreAsync(provider1, new TestSession(),
            [Msg(ChatRole.User, "第一轮问题", "m-u1")],
            [Msg(ChatRole.Assistant, "第一轮回复", "m-a1")]);
        Assert.Equal(2, CountAllChatRows());

        // 第 2 次请求：新 provider + 新会话（缓存空 → 从库加载既有历史），客户端重发完整前文 + 新消息
        var provider2 = NewProvider(conversationId);
        var session2 = new TestSession();
        var clientPayload = new List<ChatMessage>
        {
            Msg(ChatRole.User, "第一轮问题", "m-u1"),
            Msg(ChatRole.Assistant, "第一轮回复", "m-a1"),
            Msg(ChatRole.User, "第二轮问题", "m-u2"),
        };

        await InvokeProvideAsync(provider2, session2, clientPayload);   // 真实链路里 Provide 先于 Store
        await InvokeStoreAsync(provider2, session2, clientPayload, [Msg(ChatRole.Assistant, "第二轮回复", "m-a2")]);

        Assert.Equal(4, CountAllChatRows());
        Assert.Equal(
            new[] { "第一轮问题", "第一轮回复", "第二轮问题", "第二轮回复" },
            ReadRows(conversationId).Select(r => r.Text));
    }

    [Fact]
    public async Task Provide_WhenClientResendsHistory_DoesNotReturnClientSuppliedMessages()
    {
        // 模型上下文不重复：客户端已带的前文，provider 不再返回（否则模型看到两遍）。
        const string conversationId = "conv-no-dup-context";

        await InvokeStoreAsync(NewProvider(conversationId), new TestSession(),
            [Msg(ChatRole.User, "问题一", "mu1")],
            [Msg(ChatRole.Assistant, "回复一", "ma1")]);

        // 完整重发 → provider 返回为空（客户端已带全部历史）
        var fullPayload = new List<ChatMessage>
        {
            Msg(ChatRole.User, "问题一", "mu1"),
            Msg(ChatRole.Assistant, "回复一", "ma1"),
            Msg(ChatRole.User, "问题二", "mu2"),
        };
        var fullResult = await InvokeProvideAsync(NewProvider(conversationId), new TestSession(), fullPayload);
        var payloadIds = fullPayload.Select(m => m.MessageId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(fullResult, m => m.MessageId is not null && payloadIds.Contains(m.MessageId));
        Assert.Empty(fullResult);

        // 部分重发（只带 user）→ 只返回客户端未带的后续历史，且不含已带的 mu1
        var partialResult = await InvokeProvideAsync(
            NewProvider(conversationId), new TestSession(),
            new List<ChatMessage> { Msg(ChatRole.User, "问题一", "mu1") });
        Assert.Equal(new[] { "ma1" }, partialResult.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Provide_WhenClientSendsOnlyCurrentTurn_PrependsStoredHistory()
    {
        // 老行为不能丢：curl 式客户端只发本轮 → provider 从库里补回上一轮，模型能拿到上文。
        const string conversationId = "conv-curl-style";

        await InvokeStoreAsync(NewProvider(conversationId), new TestSession(),
            [Msg(ChatRole.User, "问题一", "mu1")],
            [Msg(ChatRole.Assistant, "回复一", "ma1")]);

        var provider2 = NewProvider(conversationId);
        var session2 = new TestSession();
        var currentTurn = new List<ChatMessage> { Msg(ChatRole.User, "问题二", "mu2") };

        var provided = await InvokeProvideAsync(provider2, session2, currentTurn);

        // 客户端只发本轮 → provider 补回上一轮（mu1、ma1）
        Assert.Equal(new[] { "mu1", "ma1" }, provided.Select(m => m.MessageId));

        await InvokeStoreAsync(provider2, session2, currentTurn, [Msg(ChatRole.Assistant, "回复二", "ma2")]);

        Assert.Equal(4, CountAllChatRows());
        Assert.Equal(
            new[] { "问题一", "回复一", "问题二", "回复二" },
            ReadRows(conversationId).Select(r => r.Text));
    }

    [Fact]
    public async Task Store_ClientResendsHistoryBeyondCacheWindow_StillDeduplicates()
    {
        // 客户端重发的历史超过 MaxRoundsToLoad 缓存窗口（默认 2 轮）时，仍只落库当前轮新增：
        // 已拥有历史按「最后一条已拥有消息之后」定位，故窗口外的旧轮也不会被重复入库。
        const string conversationId = "conv-beyond-window";

        var provider = NewProvider(conversationId);
        var session = new TestSession();
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q1", "u1")], [Msg(ChatRole.Assistant, "a1", "a1")]);
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q2", "u2")], [Msg(ChatRole.Assistant, "a2", "a2")]);
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q3", "u3")], [Msg(ChatRole.Assistant, "a3", "a3")]);
        Assert.Equal(6, CountAllChatRows());

        // 新会话（缓存空，Provide 只从库加载最近 2 轮）+ 客户端重发全部 3 轮前文 + 第 4 轮
        var provider2 = NewProvider(conversationId);
        var session2 = new TestSession();
        var payload = new List<ChatMessage>
        {
            Msg(ChatRole.User, "q1", "u1"), Msg(ChatRole.Assistant, "a1", "a1"),
            Msg(ChatRole.User, "q2", "u2"), Msg(ChatRole.Assistant, "a2", "a2"),
            Msg(ChatRole.User, "q3", "u3"), Msg(ChatRole.Assistant, "a3", "a3"),
            Msg(ChatRole.User, "q4", "u4"),
        };
        await InvokeProvideAsync(provider2, session2, payload);
        await InvokeStoreAsync(provider2, session2, payload, [Msg(ChatRole.Assistant, "a4", "a4")]);

        Assert.Equal(8, CountAllChatRows()); // 6 + 当前轮 2 行（而非 6 + 8）
    }

    // ---------- helpers ----------

    private async Task StoreSimpleRound(string question, string answer)
    {
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, question)],
            [new ChatMessage(ChatRole.Assistant, answer)]);
    }

    private Task<IReadOnlyList<ChatMessage>> InvokeProvideAsync(AgentSession session)
        => InvokeProvideAsync(_provider, session);

    private static Task<IReadOnlyList<ChatMessage>> InvokeProvideAsync(AIShop.Service.Agui.SqlChatHistoryProvider provider, AgentSession session)
        => InvokeProvideAsync(provider, session, [new ChatMessage(ChatRole.User, "你好")]);

    private static async Task<IReadOnlyList<ChatMessage>> InvokeProvideAsync(
        AIShop.Service.Agui.SqlChatHistoryProvider provider, AgentSession session, IList<ChatMessage> requestMessages)
    {
        var context = new ChatHistoryProvider.InvokingContext(
            Substitute.For<AIAgent>(), session, requestMessages);

        var result = ProvideMethod.Invoke(provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask<IEnumerable<ChatMessage>>)result!;
        return (await valueTask).ToList();
    }

    private Task InvokeStoreAsync(
        AgentSession session,
        IList<ChatMessage> requestMessages,
        IList<ChatMessage> responseMessages)
        => InvokeStoreAsync(_provider, session, requestMessages, responseMessages);

    private static async Task InvokeStoreAsync(
        AIShop.Service.Agui.SqlChatHistoryProvider provider,
        AgentSession session,
        IList<ChatMessage> requestMessages,
        IList<ChatMessage> responseMessages)
    {
        var context = new ChatHistoryProvider.InvokedContext(
            Substitute.For<AIAgent>(), session, requestMessages, responseMessages);

        var result = StoreMethod.Invoke(provider, [context, CancellationToken.None]);
        await (ValueTask)result!;
    }

    private List<(int Sequence, string Role, int RoundId, bool IsDeleted, string? DeletedAt, string Text)> ReadRows()
        => ReadRows(_conversationId);

    private List<(int Sequence, string Role, int RoundId, bool IsDeleted, string? DeletedAt, string Text)> ReadRows(string conversationId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, role, round_id, is_deleted, deleted_at, message_json
            FROM chat_messages
            WHERE conversation_id = $c
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$c", conversationId);

        var rows = new List<(int, string, int, bool, string?, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var role = reader.GetString(1);
            var json = reader.GetString(5);
            var message = JsonSerializer.Deserialize<ChatMessage>(json, AgentAbstractionsJsonUtilities.DefaultOptions);
            rows.Add((
                reader.GetInt32(0),
                role,
                reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                message?.Text ?? string.Empty));
        }

        return rows;
    }

    private void SoftDeleteRound(int roundId)
        => ExecuteSql(
            "UPDATE chat_messages SET is_deleted = 1, deleted_at = $now WHERE conversation_id = $c AND round_id = $r",
            ("$now", DateTimeOffset.Now.ToString("O")),
            ("$c", _conversationId),
            ("$r", roundId));

    private void BackdateRound(int roundId, DateTimeOffset createdAt)
        => ExecuteSql(
            "UPDATE chat_messages SET created_at = $at WHERE conversation_id = $c AND round_id = $r",
            ("$at", createdAt.ToString("O")),
            ("$c", _conversationId),
            ("$r", roundId));

    /// <summary>构造一个最小真实 <see cref="ChatClientAgent"/>（无工具 / 无压缩 provider），供
    /// <see cref="SqliteAgentSessionStore.GetSessionAsync"/> 走真实 CreateSessionAsync / 序列化路径。</summary>
    private static ChatClientAgent CreateBareAgent(string name)
        => Assert.IsType<ChatClientAgent>(Substitute.For<IChatClient>().AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions { Instructions = "测试人设" }
        }));

    /// <summary>读临时库 chat_messages 里出现过的 conversation_id（去重）。</summary>
    private List<string> ReadConversationIds()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT conversation_id FROM chat_messages";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>构造一个 provider，会话标识固定为 <paramref name="conversationId"/>（模拟同一 AG-UI ThreadId 的多次请求 / 重启）。</summary>
    private AIShop.Service.Agui.SqlChatHistoryProvider NewProvider(string conversationId)
        => new(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = _connectionString },
            _ => new AIShop.Service.Agui.SqlChatHistoryProvider.State(conversationId));

    /// <summary>构造带 MessageId 的消息（客户端重发历史时按 id 与 provider 已拥有历史比对）。</summary>
    private static ChatMessage Msg(ChatRole role, string text, string messageId)
        => new(role, text) { MessageId = messageId };

    /// <summary>读临时库 chat_messages 总行数（全部会话）。</summary>
    private long CountAllChatRows()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chat_messages";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private void ExecuteSql(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    /// <summary>JSON 往返后多态值可能为 <see cref="JsonElement"/>，归一成可比较的字符串。</summary>
    private static string? ToPlainString(object? value) => value switch
    {
        null => null,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
        _ => value.ToString()
    };

    private sealed class TestSession : AgentSession
    {
        public TestSession(AgentSessionStateBag? stateBag = null) : base(stateBag ?? new AgentSessionStateBag()) { }
    }
}
