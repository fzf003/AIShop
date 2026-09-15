#pragma warning disable MAAI001 // ChatHistoryProvider.InvokingContext / InvokedContext 构造属 MAF [Experimental]
using System.Reflection;
using System.Text.Json;
using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Service.Agui;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AIShop.Service.Tests;

/// <summary>
/// <see cref="AIShop.Service.Agui.SqlChatHistoryProvider"/>（design-sql-chat-history-provider §1–§6 / §8）测试。
/// 用临时 SQLite 文件（provider 每次操作独立开连接，故不能用 <c>:memory:</c>）+ 反射调用 protected 的
/// <c>StoreChatHistoryAsync</c> / <c>ProvideChatHistoryAsync</c>（对齐既有 SqliteChatHistoryProviderTests 模式）。
/// </summary>
/// <remarks>
/// <b>新语义（有意变更，非缺陷）</b>：provider 只负责「存」（把当前轮次落 <c>chat_messages</c>），不再负责「提供」——
/// <c>ProvideChatHistoryAsync</c> 已定型为恒返回空，模型上下文由客户端每轮重发全量历史承载（真实 AG-UI 客户端行为）。
/// 因此「读回已落库消息」的断言一律<b>直接查库反序列化 <c>message_json</c></b>（<see cref="ReadStoredMessages()"/>），
/// 不经 <c>Provide</c>。仅当断言带<b>可证伪前提</b>时才直调 <c>Provide</c>（如连接串不可打开 / 库中确已有历史行）——
/// 那样的断言有验证力；无前提地只断「Provide 返回空」才是恒真空转断言。
/// </remarks>
public sealed class SqlChatHistoryProviderTests : IDisposable
{
    private static readonly Type ProviderType = typeof(AIShop.Service.Agui.SqlChatHistoryProvider);
    private static readonly MethodInfo StoreMethod = ProviderType.GetMethod(
        "StoreChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ProvideMethod = ProviderType.GetMethod(
        "ProvideChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

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
            ConnectionString = _connectionString
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
    public async Task Store_RoundTripsFunctionCallAndResultContent_WithCallIdPairing()
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

        // 直接读库 message_json 并反序列化（Provide 已停用，验证存储层的多态内容无损往返）
        var stored = ReadStoredMessages();

        // assistant 的 FunctionCallContent 读回：CallId / Name 不变，参数仍可读
        var assistantOut = Assert.Single(stored, m => m.Role == ChatRole.Assistant);
        var fcc = Assert.Single(assistantOut.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", fcc.CallId);
        Assert.Equal("search_product", fcc.Name);
        Assert.NotNull(fcc.Arguments);
        Assert.True(fcc.Arguments!.ContainsKey("q"));
        Assert.Equal("手机", ToPlainString(fcc.Arguments["q"]));

        // tool 的 FunctionResultContent 读回：与 assistant FCC 的 CallId 配对完整、结果可读
        var toolOut = Assert.Single(stored, m => m.Role == ChatRole.Tool);
        var frc = Assert.Single(toolOut.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", frc.CallId);
        Assert.Equal("查询结果：手机 ¥1999", ToPlainString(frc.Result));

        // 整轮顺序保持：user → assistant(FCC) → tool(FRC)
        Assert.Equal(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool },
            stored.Select(m => m.Role));
    }

    [Fact]
    public async Task Store_MultiFunctionContentInOneMessage_RoundTripsAllPolymorphicContents()
    {
        // 同一条 assistant 消息承载 TextContent + 两个 FunctionCallContent，须同序无损往返（多态序列化保真）。
        var assistant = new ChatMessage(ChatRole.Assistant, "并行查询");
        assistant.Contents.Add(new FunctionCallContent("call_a", "search_product", new Dictionary<string, object?> { ["q"] = "鞋" }));
        assistant.Contents.Add(new FunctionCallContent("call_b", "get_stock_quote", new Dictionary<string, object?> { ["symbol"] = "MSFT" }));

        await InvokeStoreAsync(_session, [new ChatMessage(ChatRole.User, "查一下")], [assistant]);

        // 直接读库反序列化（Provide 已停用）
        var stored = ReadStoredMessages();
        var assistantOut = Assert.Single(stored, m => m.Role == ChatRole.Assistant);

        Assert.Single(assistantOut.Contents.OfType<TextContent>());
        var fccs = assistantOut.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, fccs.Count);
        Assert.Equal("call_a", fccs[0].CallId);
        Assert.Equal("call_b", fccs[1].CallId);
        Assert.Equal("MSFT", ToPlainString(fccs[1].Arguments!["symbol"]));
    }

    // ---------- TTL 整轮软删除 ----------

    [Fact]
    public async Task CleanupExpiredRounds_SoftDeletesWholeRound_WithoutSplitting()
    {
        // round 1 为含工具调用的整轮（user + assistant(FCC) + tool(FRC) + assistant 文本）：验证 TTL 整轮软删除
        // 原子、且不拆断 FCC↔FRC 配对（本用例仍是有意保留的活功能，直接查库断言 is_deleted / deleted_at）。
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, "正在查询");
        assistantWithCall.Contents.Add(new FunctionCallContent(
            "call_1", "search_product", new Dictionary<string, object?> { ["q"] = "手机" }));
        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.Contents.Add(new FunctionResultContent("call_1", "查询结果：手机 ¥1999"));

        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "旧问题")],
            [assistantWithCall, tool, new ChatMessage(ChatRole.Assistant, "旧回答")]);
        await StoreSimpleRound("新问题", "新回答");

        BackdateRound(1, DateTimeOffset.Now.AddDays(-100));

        var deletedRounds = await _provider.CleanupExpiredRoundsAsync(ttlDays: 30);

        Assert.Equal(1, deletedRounds);

        // round 1 整轮四行一并软删除（不拆断），deleted_at 已写；round 2 原样保留
        var rows = ReadRows();
        var round1 = rows.Where(r => r.RoundId == 1).ToList();
        Assert.Equal(4, round1.Count);
        Assert.All(round1, r =>
        {
            Assert.True(r.IsDeleted);
            Assert.False(string.IsNullOrEmpty(r.DeletedAt));
        });

        // 不拆断 FCC↔FRC：承载工具调用的 assistant 行与配对 tool 行均属 round 1 且一并软删除
        // （rows 与 ReadStoredMessages 同按 sequence 排序，下标一一对应）。
        var storedRound1 = ReadStoredMessages();
        var fccIndex = storedRound1.FindIndex(
            m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call_1"));
        var frcIndex = storedRound1.FindIndex(
            m => m.Contents.OfType<FunctionResultContent>().Any(c => c.CallId == "call_1"));
        Assert.True(fccIndex >= 0 && frcIndex >= 0, "工具调用的 FCC / FRC 均须落库");
        Assert.Equal(1, rows[fccIndex].RoundId);
        Assert.Equal(1, rows[frcIndex].RoundId);
        Assert.True(rows[fccIndex].IsDeleted);
        Assert.True(rows[frcIndex].IsDeleted);

        var round2 = rows.Where(r => r.RoundId == 2).ToList();
        Assert.Equal(2, round2.Count);
        Assert.All(round2, r => Assert.False(r.IsDeleted));

        // 软删除轮直接查库不可见（is_deleted=0 过滤后只剩 round 2）——整轮原子、未留半轮
        var visible = rows.Where(r => !r.IsDeleted).ToList();
        Assert.Equal(new[] { "新问题", "新回答" }, visible.Select(r => r.Text));
        Assert.All(visible, r => Assert.Equal(2, r.RoundId));
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

    [Fact]
    public async Task Store_AfterTtlSoftDeletesEntireConversation_NextRoundStillWrites()
    {
        // 请求流程闭环：整会话过期被 TTL【整轮软删】后，同一用户的下一条消息仍必须能落库。
        // TTL 清理只置 is_deleted=1，行还占着 (conversation_id, sequence)；而 UNIQUE 约束不含 is_deleted。
        // 会话快照侧是【硬删】（agent_sessions 行没了、用户回来建新会话），但 conversation_id 两边都等于用户名
        // —— 所以历史的「重置」并不存在，新轮必须在既有序号之后续号，不能回到 1 去撞软删行。
        await StoreSimpleRound("旧问题", "旧回答");
        BackdateRound(1, DateTimeOffset.Now.AddDays(-100));

        Assert.Equal(1, await _provider.CleanupExpiredRoundsAsync(ttlDays: 30));
        Assert.All(ReadRows(), r => Assert.True(r.IsDeleted)); // 前置：该会话已无任何可写序号

        // 30 天后该用户再次请求（客户端仍带本地历史重发，落到同一 conversation_id）
        await InvokeStoreAsync(
            _session,
            [
                new ChatMessage(ChatRole.User, "旧问题"),
                new ChatMessage(ChatRole.Assistant, "旧回答"),
                new ChatMessage(ChatRole.User, "新问题"),
            ],
            [new ChatMessage(ChatRole.Assistant, "新回答")]);

        var rows = ReadRows();
        var fresh = rows.Where(r => !r.IsDeleted).ToList();
        Assert.Equal(new[] { "新问题", "新回答" }, fresh.Select(r => r.Text));
        Assert.Equal(2, Assert.Single(fresh, r => r.Role == "user").RoundId); // 轮次继续递增，不与旧轮重号
        Assert.True(
            fresh.Min(r => r.Sequence) > 2,
            "新轮序号必须避开已软删行占用的 1..2，否则撞 UNIQUE(conversation_id, sequence)");
    }

    // ---------- 事务原子性 ----------

    [Fact]
    public async Task Store_BatchConflictsWithExistingUniqueSequence_RollsBackWholeBatchAndThrows()
    {
        // 乐观重试的触发前提是【取号之后、插入之前】有别人抢走同一序号。修复「软删行不进 MAX」之后，
        // 预置行会被 MAX 看见、新批次自动从它之后起号，再也撞不上——旧版本条测试正是靠那个缺陷搭的台子，
        // 故改用 SQLite trigger 模拟真并发：批次插入 seq=1 时，「另一位写入者」立刻占掉 seq=2。
        // 于是批次插到第 2 条撞 UNIQUE(conversation_id, sequence) → 整批回滚 → 重试 3 次仍被 trigger 重现冲突
        // → 抛 InvalidOperationException。断言回滚彻底：一行不留（trigger 那行同事务，一并回滚）。
        await _provider.InitializeAsync();
        ExecuteSql(
            """
            CREATE TRIGGER simulate_concurrent_writer
            AFTER INSERT ON chat_messages
            WHEN NEW.sequence = 1
            BEGIN
                INSERT INTO chat_messages (conversation_id, sequence, role, message_json, round_id, created_at)
                VALUES (NEW.conversation_id, 2, 'user', '{}', 1, NEW.created_at);
            END
            """);

        // 当前轮次 = 1 条 user + 2 条响应 → 批次写 seq 1/2/3，第 2 条即撞上 trigger 占掉的 seq=2。
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, "第一条")],
            [new ChatMessage(ChatRole.Assistant, "第二条"), new ChatMessage(ChatRole.Assistant, "第三条")]));

        Assert.Empty(ReadRows()); // 整批 + trigger 行同事务回滚，未留任何部分写入
    }

    // ---------- 生产场景：同一用户并发请求 ----------

    [Fact]
    public async Task Store_ConcurrentRequestsForSameConversation_AllSucceed()
    {
        // conversation_id = 用户名，故同一用户的并发请求（两个 tab / 并发 run / 客户端重试）共用同一序号空间
        // 与同一个 SQLite 写锁。所有批次都从同一个 MAX(sequence) 取号 → 必然互相争抢。
        // 断言：全部成功、序号无重复、一轮不丢。
        const int concurrency = 16;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => InvokeStoreAsync(
                _session,
                [new ChatMessage(ChatRole.User, $"问题{i}")],
                [new ChatMessage(ChatRole.Assistant, $"回答{i}")]))
            .ToArray();

        await Task.WhenAll(tasks); // 任一批次失败即在此抛出

        var rows = ReadRows();
        Assert.Equal(concurrency * 2, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Sequence).Distinct().Count());
    }

    // ---------- 落库失败兜底：不得中断请求 ----------

    [Fact]
    public async Task InvokedAsync_WhenStoreThrows_DoesNotPropagate()
    {
        // 落库失败（连接串指向不存在的目录 → 库打不开）不得上抛。流式路径下 provider 通知发生在【所有 yield
        // 之后】，上抛会让「答复已完整吐出」的流异常断开：RUN_FINISHED 不发出、端点侧 SaveSessionAsync 也不执行。
        // 兜底后只记 Error 日志、请求继续（代价是该轮静默不入库，见 InvokedCoreAsync 的 remarks）。
        var brokenDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.db");
        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = $"Data Source={brokenDbPath}" },
            _ => new AIShop.Service.Agui.SqlChatHistoryProvider.State(_conversationId));

        var context = new ChatHistoryProvider.InvokedContext(
            Substitute.For<AIAgent>(),
            _session,
            [new ChatMessage(ChatRole.User, "问题")],
            [new ChatMessage(ChatRole.Assistant, "回答")]);

        var exception = await Record.ExceptionAsync(
            () => provider.InvokedAsync(context).AsTask());

        Assert.Null(exception); // 断言：落库失败被兜底，异常未上抛
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
    public async Task Store_AfterStateBagRoundTrip_UsesSameConversationId()
    {
        // 会话标识存 AgentSession.StateBag（随会话快照被 SqliteAgentSessionStore 持久化）。
        // 这里对 StateBag 做序列化 → 反序列化 → 新会话实例，模拟「跨请求 / 重启还原」，
        // 两轮落库的 conversation_id 必须相同（标识跨往返稳定）。
        // 用【默认】初始化器（按会话 StateBag 记忆标识）：若往返丢失状态，第二轮会生成新 GUID → 出现两个会话 id。
        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = _connectionString });

        await InvokeStoreAsync(provider, _session,
            [new ChatMessage(ChatRole.User, "问题1")],
            [new ChatMessage(ChatRole.Assistant, "回答1")]);

        using var document = JsonDocument.Parse(_session.StateBag.Serialize().GetRawText());
        var restoredBag = AgentSessionStateBag.Deserialize(document.RootElement);
        var restoredSession = new TestSession(restoredBag);

        await InvokeStoreAsync(provider, restoredSession,
            [new ChatMessage(ChatRole.User, "问题2")],
            [new ChatMessage(ChatRole.Assistant, "回答2")]);

        // 两轮落库共用一个 conversation_id（单一），且四行齐全、顺序正确
        var id = Assert.Single(ReadConversationIds());
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(
            new[] { "问题1", "回答1", "问题2", "回答2" },
            ReadRows(id).Select(r => r.Text));
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

    // ---------- 会话内缓存：装入 / 随快照恢复 / 按轮截断 ----------

    [Fact]
    public async Task Cache_LoadsOnStore_SurvivesRestart_AndTrimsToMaxRounds()
    {
        // 第 1 轮 → 缓存装入本轮消息
        await StoreSimpleRound("问题1", "回答1");
        Assert.Equal(new[] { "问题1", "回答1" }, ReadCachedMessages(_session).Select(m => m.Text));

        // StateBag 序列化往返（模拟宿主重启）→ 缓存随会话快照恢复，新的 provider 实例继续写
        using var document = JsonDocument.Parse(_session.StateBag.Serialize().GetRawText());
        var restored = new TestSession(AgentSessionStateBag.Deserialize(document.RootElement));
        var provider2 = NewProvider(_conversationId);

        await InvokeStoreAsync(provider2, restored,
            [new ChatMessage(ChatRole.User, "问题2")],
            [new ChatMessage(ChatRole.Assistant, "回答2")]);

        // MaxRoundsToLoad 默认 2 → 两轮都在窗口内，未截断
        Assert.Equal(new[] { "问题1", "回答1", "问题2", "回答2" },
            ReadCachedMessages(restored).Select(m => m.Text));

        await InvokeStoreAsync(provider2, restored,
            [new ChatMessage(ChatRole.User, "问题3")],
            [new ChatMessage(ChatRole.Assistant, "回答3")]);

        // 第 3 轮越窗 → 截到最近 2 轮，最旧的「问题1/回答1」被丢弃
        Assert.Equal(new[] { "问题2", "回答2", "问题3", "回答3" },
            ReadCachedMessages(restored).Select(m => m.Text));
    }

    [Fact]
    public async Task Provide_IgnoresSessionCache_AlwaysReturnsEmpty()
    {
        // 可证伪前提：缓存【确有内容】（上一条路径已验证），Provide 仍返回空——
        // 证明缓存不参与模型上下文，即「只存不取」在注入侧成立。
        await StoreSimpleRound("问题", "回答");
        Assert.NotEmpty(ReadCachedMessages(_session));

        var provided = await InvokeProvideAsync(
            _provider, _session, [new ChatMessage(ChatRole.User, "新问题")]);

        Assert.Empty(provided);
    }

    // ---------- 只处理「当前轮次」（纯位置判定）：客户端重发整段前文不重复入库 ----------

    [Fact]
    public async Task Store_ClientResendsFullHistory_DoesNotGrowRows()
    {
        // 缺陷回归（真实链路）：客户端重发前文时，assistant 那条带的是【客户端自己的 id】（服务端返回的是
        // chatcmpl-…，客户端可能重发为自造 id），两者对不上——故不能依赖 message id 判「已拥有」，必须纯按位置
        // 划「当前轮次 = 最后一条 user 起」。本测试刻意让重发的 id 与库中 id 全不相同，复现 id 比对落空的真实失败。
        const string conversationId = "conv-full-resend";

        var provider1 = NewProvider(conversationId);
        await InvokeStoreAsync(provider1, new TestSession(),
            [Msg(ChatRole.User, "第一轮问题", "server-u1")],
            [Msg(ChatRole.Assistant, "第一轮回复", "chatcmpl-a1")]);
        Assert.Equal(2, CountAllChatRows());

        // 第 2 次请求：新 provider + 新会话，客户端重发第 1 轮（id 全不同）+ 本轮新消息
        var provider2 = NewProvider(conversationId);
        var session2 = new TestSession();
        var clientPayload = new List<ChatMessage>
        {
            Msg(ChatRole.User, "第一轮问题", "client-u1"),       // ≠ 库中 server-u1
            Msg(ChatRole.Assistant, "第一轮回复", "client-a1"),  // ≠ 库中 chatcmpl-a1
            Msg(ChatRole.User, "第二轮问题", "client-u2"),
        };

        await InvokeStoreAsync(provider2, session2, clientPayload, [Msg(ChatRole.Assistant, "第二轮回复", "chatcmpl-a2")]);

        Assert.Equal(4, CountAllChatRows());
        Assert.Equal(
            new[] { "第一轮问题", "第一轮回复", "第二轮问题", "第二轮回复" },
            ReadRows(conversationId).Select(r => r.Text));
    }

    [Fact]
    public async Task Store_ClientResendsDeepHistory_StoresOnlyCurrentTurn()
    {
        // 客户端重发的历史深于当前轮（含 3 个完整旧轮）、且 id 与库中全不同时，仍只落库当前轮新增
        // （纯按位置判定，与历史深度无关）。
        const string conversationId = "conv-deep-history";

        var provider = NewProvider(conversationId);
        var session = new TestSession();
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q1", "s-u1")], [Msg(ChatRole.Assistant, "a1", "s-a1")]);
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q2", "s-u2")], [Msg(ChatRole.Assistant, "a2", "s-a2")]);
        await InvokeStoreAsync(provider, session, [Msg(ChatRole.User, "q3", "s-u3")], [Msg(ChatRole.Assistant, "a3", "s-a3")]);
        Assert.Equal(6, CountAllChatRows());

        var provider2 = NewProvider(conversationId);
        var session2 = new TestSession();
        var payload = new List<ChatMessage>
        {
            Msg(ChatRole.User, "q1", "c-u1"), Msg(ChatRole.Assistant, "a1", "c-a1"),
            Msg(ChatRole.User, "q2", "c-u2"), Msg(ChatRole.Assistant, "a2", "c-a2"),
            Msg(ChatRole.User, "q3", "c-u3"), Msg(ChatRole.Assistant, "a3", "c-a3"),
            Msg(ChatRole.User, "q4", "c-u4"),
        };
        await InvokeStoreAsync(provider2, session2, payload, [Msg(ChatRole.Assistant, "a4", "s-a4")]);

        Assert.Equal(8, CountAllChatRows()); // 6 + 当前轮 2 行（而非 6 + 8）
    }

    // ---------- Provide 恒空且不查库（只存不取定型）+ 槽位被 Sql provider 占住 ----------

    [Fact]
    public async Task Provide_WithUnopenableConnectionString_ReturnsEmptyWithoutTouchingDatabase()
    {
        // ADDED-1 场景 1（实质性断言）：Provide 恒空且【不查库】。
        // 连接串指向「不存在的目录」下的 db 文件——任何一次 OpenAsync 都会失败（SQLite 不会创建缺失目录）。
        // 故「调用不抛异常且返回空」本身即证明本次未对 chat_messages 发起任何读取。
        var badConnection =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"no_such_dir_{Guid.NewGuid():N}", "chat.db")}";

        // 前提校验（防断言空转）：该连接串确实打不开——直接用 SqliteConnection 打开必抛。
        Assert.ThrowsAny<Exception>(() =>
        {
            using var probe = new SqliteConnection(badConnection);
            probe.Open();
        });

        var provider = new AIShop.Service.Agui.SqlChatHistoryProvider(
            new AIShop.Service.Agui.SqlChatHistoryOptions { ConnectionString = badConnection },
            _ => new AIShop.Service.Agui.SqlChatHistoryProvider.State(_conversationId));

        var result = await InvokeProvideAsync(provider, new TestSession(), [new ChatMessage(ChatRole.User, "你好")]);

        Assert.Empty(result); // 恒空；若仍查库会因连接串不可打开而抛异常
    }

    [Fact]
    public async Task Provide_WithStoredHistoryAndCurrentTurnOnlyRequest_StillReturnsEmpty()
    {
        // ADDED-1 场景 2 + MODIFIED-1：curl 式只发本轮、不自带历史前缀的调用方不再获得任何服务端补历史。
        // 先真实落库一轮（证明库里确有待加载的历史），再以【只含当前轮】的请求触发 Provide。
        await StoreSimpleRound("历史问题", "历史回答");
        Assert.Equal(2L, CountAllChatRows()); // 可证伪前提：库中确有 2 行历史可供回填

        var result = await InvokeProvideAsync(
            _provider, new TestSession(), [new ChatMessage(ChatRole.User, "只发本轮的新问题")]);

        Assert.Empty(result); // 库里虽有历史行，仍不回填（Provide 不读 chat_messages）
    }

    [Fact]
    public void Slot_ResolvedChatHistoryProvider_IsSql_AndMountedOnAgent()
    {
        // ADDED-2 场景：配置 Agui:ChatHistoryProvider=Sql 后，容器解析出的 ChatHistoryProvider 必须是
        // SqlChatHistoryProvider 实例（而非 MAF 默认 InMemoryChatHistoryProvider），且该实例经
        // AGUIShoppingAgent.Create 占据 agent 的 ChatHistoryProvider 槽位（槽位空出会退回会注入历史的 InMemory）。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AguiChatHistoryDependencyInjection.ProviderConfigKey] = AguiChatHistoryDependencyInjection.SqlProviderValue
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAguiChatHistoryProvider(config, _connectionString);
        using var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<ChatHistoryProvider>();
        Assert.IsType<AIShop.Service.Agui.SqlChatHistoryProvider>(resolved);
        Assert.IsNotType<InMemoryChatHistoryProvider>(resolved);

        var agent = Assert.IsType<ChatClientAgent>(AGUIShoppingAgent.Create(
            Substitute.For<IChatClient>(),
            new CartToolProvider(Substitute.For<IServiceScopeFactory>(), Substitute.For<ICurrentUserAccessor>()),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            chatHistoryProvider: resolved));

        Assert.Same(resolved, agent.ChatHistoryProvider);
        Assert.IsNotType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
    }

    [Fact]
    public void AddAguiChatHistoryProvider_BindsCleanupKeysFromAguiSection()
    {
        // 清理参数可从配置改：Agui:TtlDays / Agui:CleanupBatchSize。此前只设连接串、这两个值恒为类默认
        // 且无从更改——而数据量最大的 chat_messages 正归它管（运维改 Agui:SessionTtlDays 不会影响它）。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AguiChatHistoryDependencyInjection.ProviderConfigKey] = AguiChatHistoryDependencyInjection.SqlProviderValue,
                ["Agui:TtlDays"] = "7",
                ["Agui:CleanupBatchSize"] = "3"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAguiChatHistoryProvider(config, _connectionString);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<AIShop.Service.Agui.SqlChatHistoryOptions>();
        Assert.Equal(7, options.TtlDays);
        Assert.Equal(3, options.CleanupBatchSize);

        // 连接串仍以显式参数（Agui:ChatConnection seam）为唯一来源，不被节绑定干扰
        Assert.Equal(_connectionString, options.ConnectionString);
    }

    [Fact]
    public void AddAguiChatHistoryProvider_WithoutCleanupKeys_KeepsClassDefaults()
    {
        // 未配置清理参数时保持类默认（30 天 / 每批 10 轮），行为零变化。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AguiChatHistoryDependencyInjection.ProviderConfigKey] = AguiChatHistoryDependencyInjection.SqlProviderValue
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAguiChatHistoryProvider(config);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<AIShop.Service.Agui.SqlChatHistoryOptions>();

        Assert.Equal(AIShop.Service.Agui.SqlChatHistoryOptions.DefaultTtlDays, options.TtlDays);
        Assert.Equal(AIShop.Service.Agui.SqlChatHistoryOptions.DefaultCleanupBatchSize, options.CleanupBatchSize);
    }

    // ---------- helpers ----------

    private async Task StoreSimpleRound(string question, string answer)
    {
        await InvokeStoreAsync(
            _session,
            [new ChatMessage(ChatRole.User, question)],
            [new ChatMessage(ChatRole.Assistant, answer)]);
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

    /// <summary>反射调用 protected 的 <c>ProvideChatHistoryAsync</c>（构造 <see cref="ChatHistoryProvider.InvokingContext"/>）。
    /// 直调该 protected 方法（而非公开的 <c>InvokingAsync</c>）以隔离「provider 自身是否返回空」——后者会把
    /// 请求消息一并拼回，无法单独观测 provider 的提供行为。</summary>
    private static async Task<IReadOnlyList<ChatMessage>> InvokeProvideAsync(
        AIShop.Service.Agui.SqlChatHistoryProvider provider,
        AgentSession session,
        IEnumerable<ChatMessage> requestMessages)
    {
        var context = new ChatHistoryProvider.InvokingContext(
            Substitute.For<AIAgent>(), session, requestMessages);

        var result = (ValueTask<IEnumerable<ChatMessage>>)ProvideMethod.Invoke(
            provider, [context, CancellationToken.None])!;
        return (await result).ToList();
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

    /// <summary>按 sequence 升序读回临时库 <c>message_json</c> 并反序列化（Provide 已停用，读回不经 Provide；
    /// 用 provider 同一个 JSON 选项以无损还原多态 <see cref="AIContent"/>）。</summary>
    private List<ChatMessage> ReadStoredMessages() => ReadStoredMessages(_conversationId);

    private List<ChatMessage> ReadStoredMessages(string conversationId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT message_json
            FROM chat_messages
            WHERE conversation_id = $c
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$c", conversationId);

        var messages = new List<ChatMessage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(
                reader.GetString(0), AgentAbstractionsJsonUtilities.DefaultOptions);
            if (message is not null)
                messages.Add(message);
        }

        return messages;
    }

    /// <summary>从会话 StateBag 读回本 provider 自己键下的缓存消息（不经 Provide——它恒空，读不到缓存）。</summary>
    private static List<ChatMessage> ReadCachedMessages(AgentSession session)
    {
        using var document = JsonDocument.Parse(session.StateBag.Serialize().GetRawText());
        var stateKey = nameof(AIShop.Service.Agui.SqlChatHistoryProvider);
        Assert.True(
            document.RootElement.TryGetProperty(stateKey, out var stateElement),
            $"会话 StateBag 应含 {stateKey} 键");

        var state = JsonSerializer.Deserialize<AIShop.Service.Agui.SqlChatHistoryProvider.State>(
            stateElement.GetRawText(), AgentAbstractionsJsonUtilities.DefaultOptions);
        Assert.NotNull(state);
        return state!.Messages.Select(m => m.Message).ToList();
    }

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
