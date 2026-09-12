#pragma warning disable MAAI001 // ChatClientAgentOptions/ChatHistoryProvider 构造属 MAF [Experimental]
using AIShop.Infrastructure.Services;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// <see cref="SqliteAgentSessionStore"/> 的<b>会话归属</b>测试（服务端会话归属：按用户名而非 AG-UI threadId）。
/// 验证 store_id = <c>{agent.Name}:{当前用户名}</c>：同一用户跨两次请求（含不同 threadId）落到同一行、upsert 刷新
/// <c>updated_at</c>；不同用户互不干扰；用户名缺失回退 <c>{agent.Name}:{threadId}</c>（既有行为）。
/// 用真实 <see cref="CurrentUserAccessor"/>（AsyncLocal）设用户名，临时 SQLite 文件逐测试隔离。
/// </summary>
public sealed class SqliteAgentSessionStoreUserBindingTests : IDisposable
{
    private const string UsernameAlice = "alice";
    private const string UsernameBob = "bob";

    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteAgentSessionStoreUserBindingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"store_user_binding_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
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

    [Fact]
    public async Task SameUsername_TwoRoundsWithDifferentThreadIds_KeepsSingleRowAndRefreshesUpdatedAt()
    {
        // 本次改动的核心断言：同一用户名跨两次请求（AG-UI 客户端每轮铸新 threadId）→ store_id 仍为
        // "{agentName}:{username}"，ON CONFLICT 命中把同一行 upsert，只有 1 行且 updated_at 被刷新。
        var accessor = new CurrentUserAccessor();
        accessor.SetCurrentUser(UsernameAlice);
        var store = new SqliteAgentSessionStore(_connectionString, currentUserAccessor: accessor);
        await store.InitializeAsync();

        var agent = CreateBareAgent("AGUIShopping");

        // 第一轮：thread-1，写入第一轮历史
        var round1 = await agent.CreateSessionAsync();
        AddHistory(agent, round1, "第一轮用户消息", "第一轮回复 ROUND-ONE");
        await store.SaveSessionAsync(agent, "thread-round-1", round1);
        var updatedAtRound1 = await ReadUpdatedAtAsync(_connectionString, "AGUIShopping:alice");
        Assert.NotNull(updatedAtRound1);

        // 间隔确保 updated_at（"O" 格式，时间序即字符串序）确有推进
        await Task.Delay(30);

        // 第二轮：thread-2（客户端每轮新 threadId），写入第二轮历史
        var round2 = await agent.CreateSessionAsync();
        AddHistory(agent, round2, "第二轮用户消息", "第二轮回复 ROUND-TWO");
        await store.SaveSessionAsync(agent, "thread-round-2", round2);
        var updatedAtRound2 = await ReadUpdatedAtAsync(_connectionString, "AGUIShopping:alice");

        // 只有 1 行（两轮归一，非每轮新增）；无 threadId 键的行
        Assert.Equal(1, await CountAllRowsAsync(_connectionString));
        Assert.Equal(1, await CountSessionRowsAsync(_connectionString, "AGUIShopping:alice"));
        Assert.Equal(0, await CountSessionRowsAsync(_connectionString, "AGUIShopping:thread-round-1"));
        Assert.Equal(0, await CountSessionRowsAsync(_connectionString, "AGUIShopping:thread-round-2"));

        // updated_at 被刷新（第二轮 > 第一轮），证明走了 ON CONFLICT DO UPDATE
        Assert.NotNull(updatedAtRound2);
        Assert.True(string.CompareOrdinal(updatedAtRound2, updatedAtRound1!) > 0,
            $"updated_at 未被刷新：round1={updatedAtRound1}，round2={updatedAtRound2}");

        // upsert 后会话内容为第二轮快照
        Assert.Contains("ROUND-TWO", await ReadSessionJsonAsync(_connectionString, "AGUIShopping:alice"));
    }

    [Fact]
    public async Task DifferentUsernames_AreIsolated_EachKeepsOwnRow()
    {
        // 不同 username → store_id 不同，互不干扰：alice 的历史 bob 读不到，各自 1 行。
        var accessor = new CurrentUserAccessor();
        var store = new SqliteAgentSessionStore(_connectionString, currentUserAccessor: accessor);
        await store.InitializeAsync();

        // alice 写历史
        accessor.SetCurrentUser(UsernameAlice);
        var aliceAgent = CreateBareAgent("AGUIShopping");
        var aliceSession = await aliceAgent.CreateSessionAsync();
        AddHistory(aliceAgent, aliceSession, "alice 的问题", "alice 的历史 ALICE-HISTORY");
        await store.SaveSessionAsync(aliceAgent, "thread-alice", aliceSession);

        // bob 读：无自己的行 → 全新空会话（读不到 alice 的历史）
        accessor.SetCurrentUser(UsernameBob);
        var bobAgent = CreateBareAgent("AGUIShopping");
        var bobRead = await store.GetSessionAsync(bobAgent, "thread-bob");
        Assert.Equal(string.Empty, HistoryText(bobAgent, bobRead));

        // bob 写自己的历史
        var bobSession = await bobAgent.CreateSessionAsync();
        AddHistory(bobAgent, bobSession, "bob 的问题", "bob 的历史 BOB-HISTORY");
        await store.SaveSessionAsync(bobAgent, "thread-bob", bobSession);

        // 各自 1 行、互不覆盖
        Assert.Equal(1, await CountSessionRowsAsync(_connectionString, "AGUIShopping:alice"));
        Assert.Equal(1, await CountSessionRowsAsync(_connectionString, "AGUIShopping:bob"));

        // alice 再读（新 thread）：仍还原自己的历史，不含 bob 的
        accessor.SetCurrentUser(UsernameAlice);
        var aliceAgent2 = CreateBareAgent("AGUIShopping");
        var aliceRestored = await store.GetSessionAsync(aliceAgent2, "thread-alice-next");
        var aliceHistory = HistoryText(aliceAgent2, aliceRestored);
        Assert.Contains("ALICE-HISTORY", aliceHistory);
        Assert.DoesNotContain("BOB-HISTORY", aliceHistory);
    }

    [Fact]
    public async Task MissingUsername_FallsBackToThreadIdKey_PreservingLegacyBehavior()
    {
        // 直构 store（不传 accessor）→ 用户名缺失 → store_id 回退 "{agentName}:{threadId}"（既有行为不变），
        // 并记 Serilog.Log.Error 告警（静态 Log 未配置 logger 时为 no-op，不抛异常）。
        var store = new SqliteAgentSessionStore(_connectionString); // 无 currentUserAccessor
        await store.InitializeAsync();

        var agent = CreateBareAgent("AGUIShopping");
        var session = await agent.CreateSessionAsync();
        AddHistory(agent, session, "用户消息", "回退回复");
        await store.SaveSessionAsync(agent, "thread-fallback", session);

        Assert.Equal(1, await CountSessionRowsAsync(_connectionString, "AGUIShopping:thread-fallback"));
        Assert.Equal(1, await CountAllRowsAsync(_connectionString));

        // 回退路径按 threadId 读回（同一 threadId 命中）
        var restored = await store.GetSessionAsync(agent, "thread-fallback");
        Assert.Contains("回退回复", HistoryText(agent, restored));
    }

    [Fact]
    public async Task SameUsername_DifferentThreadIds_ResolveToSameRow()
    {
        // 这正是要修的病：客户端每轮换 threadId，旧行为（按 threadId 绑 key）会每轮产生新会话；
        // 按用户名归属后，同一用户名的两段 thread 落到同一行。
        var accessor = new CurrentUserAccessor();
        accessor.SetCurrentUser(UsernameAlice);
        var store = new SqliteAgentSessionStore(_connectionString, currentUserAccessor: accessor);
        await store.InitializeAsync();

        var agent = CreateBareAgent("AGUIShopping");
        var session = await agent.CreateSessionAsync();
        AddHistory(agent, session, "用户消息", "回复 SAME-ROW");

        await store.SaveSessionAsync(agent, "thread-device-a", session);
        await store.SaveSessionAsync(agent, "thread-device-b", session);

        // 两段 thread 归一为同一用户名行；不产生 threadId 命名的行
        Assert.Equal(1, await CountAllRowsAsync(_connectionString));
        Assert.Equal(1, await CountSessionRowsAsync(_connectionString, "AGUIShopping:alice"));
        Assert.Equal(0, await CountSessionRowsAsync(_connectionString, "AGUIShopping:thread-device-a"));
        Assert.Equal(0, await CountSessionRowsAsync(_connectionString, "AGUIShopping:thread-device-b"));
    }

    // ---------- helpers ----------

    /// <summary>构造最小 ChatClientAgent（无工具/无压缩 provider，只验证 store 依赖的 StateBag 消息历史机制）。</summary>
    private static ChatClientAgent CreateBareAgent(string name)
    {
        var chatClient = Substitute.For<Meai.IChatClient>();
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new Meai.ChatOptions { Instructions = "测试人设" }
        });
        return Assert.IsType<ChatClientAgent>(agent);
    }

    /// <summary>向会话的 InMemoryChatHistoryProvider 追加一轮 user + assistant 消息。</summary>
    private static void AddHistory(ChatClientAgent agent, AgentSession session, string userText, string assistantText)
    {
        var history = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        var messages = history.GetMessages(session);
        messages.Add(new Meai.ChatMessage(Meai.ChatRole.User, userText));
        messages.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, assistantText));
    }

    /// <summary>读取会话中 InMemoryChatHistoryProvider 的消息文本（state 存 Session.StateBag["InMemoryChatHistoryProvider"]）。</summary>
    private static string HistoryText(ChatClientAgent agent, AgentSession session)
    {
        var historyProvider = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        return string.Join(" | ", historyProvider.GetMessages(session).Select(TextOf));
    }

    /// <summary>拼接消息所有 TextContent 文本。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    /// <summary>直连会话库统计指定 store_id 的行数。</summary>
    private static async Task<long> CountSessionRowsAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>直连会话库统计总行数。</summary>
    private static async Task<long> CountAllRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>读取指定 store_id 的 updated_at（不存在返回 null）。</summary>
    private static async Task<string?> ReadUpdatedAtAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>读取指定 store_id 的 session_json（不存在返回空串）。</summary>
    private static async Task<string> ReadSessionJsonAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_json FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (await command.ExecuteScalarAsync() as string) ?? string.Empty;
    }
}
