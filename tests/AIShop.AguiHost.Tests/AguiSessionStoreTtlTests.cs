using AIShop.AguiHost;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S5（agui-session-prod）TTL 测试：惰性过期兜底（<see cref="SqliteAgentSessionStore.GetSessionAsync"/> 命中过期行
/// → 当新会话 + best-effort 删行，spec R4）、分批清理（<see cref="SqliteAgentSessionStore.CleanupExpiredAsync"/>
/// 循环删净 + TTL 禁用，spec R6）、以及 <c>updated_at</c> 索引（<see cref="SqliteAgentSessionStore.InitializeAsync"/>，spec R6）。
/// 与 <see cref="AguiSessionStoreTests"/> 同串行集合，避免 SQLite 文件并发写入冲突；Dispose 时
/// <c>SqliteConnection.ClearAllPools</c> + 删临时库。
/// </summary>
[Collection(nameof(AguiSessionStoreTests))]
public sealed class AguiSessionStoreTtlTests : IDisposable
{
    private const string AgentName = "TtlAgent";

    /// <summary>过期阈值测试用 TTL 天数（与默认 30 一致）。</summary>
    private const int TtlDays = AguiSessionOptions.DefaultSessionTtlDays;

    private readonly List<string> _createdDbPaths = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _createdDbPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 文件仍被其他进程占用时忽略，交由系统清理
            }
        }
    }

    private string NewDbPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_t12_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    /// <summary>构造一个最小 ChatClientAgent（无工具/无压缩 provider，只验证 store 依赖的 StateBag 消息历史机制）。</summary>
    private static ChatClientAgent CreateBareAgent(string name)
    {
        var chatClient = NSubstitute.Substitute.For<Meai.IChatClient>();
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new Meai.ChatOptions { Instructions = "测试人设" }
        });
        return Assert.IsType<ChatClientAgent>(agent);
    }

    /// <summary>读取会话中 InMemoryChatHistoryProvider 的消息文本（state 存 Session.StateBag["InMemoryChatHistoryProvider"]）。</summary>
    private static string HistoryText(ChatClientAgent agent, AgentSession session)
    {
        var historyProvider = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        return string.Join(" | ", historyProvider.GetMessages(session).Select(TextOf));
    }

    /// <summary>拼接消息所有 TextContent 文本（供断言含指定回复标记）。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    // ---------- 过期清理（CleanupExpiredAsync）----------

    [Fact]
    public async Task CleanupExpired_DeletesOnlyExpiredRows_ReturnsDeletedCount()
    {
        // R6：updated_at 早于 TTL 的行被删、新鲜行保留、返回计数正确
        var (connectionString, store) = await NewInitializedStoreAsync();
        var now = DateTimeOffset.Now;
        await InsertRowAsync(connectionString, $"{AgentName}:expired-1", now.AddDays(-(TtlDays + 10)));
        await InsertRowAsync(connectionString, $"{AgentName}:expired-2", now.AddDays(-(TtlDays + 1)));
        await InsertRowAsync(connectionString, $"{AgentName}:expired-3", now.AddDays(-(TtlDays * 2)));
        await InsertRowAsync(connectionString, $"{AgentName}:fresh-1", now);
        await InsertRowAsync(connectionString, $"{AgentName}:fresh-2", now.AddDays(-1));

        int deleted = await store.CleanupExpiredAsync(TtlDays);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await CountRowAsync(connectionString, $"{AgentName}:expired-1"));
        Assert.Equal(0, await CountRowAsync(connectionString, $"{AgentName}:expired-3"));
        Assert.Equal(1, await CountRowAsync(connectionString, $"{AgentName}:fresh-1"));
        Assert.Equal(1, await CountRowAsync(connectionString, $"{AgentName}:fresh-2"));
        Assert.Equal(2, await CountRowsAsync(connectionString));
    }

    [Fact]
    public async Task CleanupExpired_WhenExpiredExceedBatch_DeletesAllInBatches_ReturnsTotal()
    {
        // R6 场景 1：过期行数 > batchSize → 多批删净、返回累计数 == 过期行总数、一条不漏
        var (connectionString, store) = await NewInitializedStoreAsync();
        var now = DateTimeOffset.Now;
        for (int i = 0; i < 5; i++)
            await InsertRowAsync(connectionString, $"{AgentName}:bulk-{i}", now.AddDays(-(TtlDays + 5)));

        int deleted = await store.CleanupExpiredAsync(TtlDays, batchSize: 2);

        Assert.Equal(5, deleted);
        Assert.Equal(0, await CountRowsAsync(connectionString));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CleanupExpired_WhenTtlDisabled_ReturnsZeroAndDeletesNothing(int ttlDays)
    {
        // R7 场景 2：ttlDays <= 0 → 返回 0 且不删任何行
        var (connectionString, store) = await NewInitializedStoreAsync();
        var now = DateTimeOffset.Now;
        for (int i = 0; i < 3; i++)
            await InsertRowAsync(connectionString, $"{AgentName}:keep-{i}", now.AddDays(-1000));

        int deleted = await store.CleanupExpiredAsync(ttlDays);

        Assert.Equal(0, deleted);
        Assert.Equal(3, await CountRowsAsync(connectionString));
    }

    // ---------- 惰性 TTL 兜底（GetSessionAsync）----------

    [Fact]
    public async Task GetSession_WhenRowExpired_ReturnsNewEmptySessionAndDeletesRow()
    {
        // R4 场景 1：回填超期行 → GetSessionAsync 返回不含历史的新会话（等价 CreateSessionAsync）且该行被删除
        var (connectionString, store) = await NewInitializedStoreAsync();
        const string threadId = "thread-expired";
        var storeId = $"{AgentName}:{threadId}";

        // 先经 store 正常落一个含历史的会话，再把 updated_at 回填至超期
        await SaveSessionWithHistoryAsync(store, threadId, "过期前的回复 TTL-EXPIRED-MARKER");
        Assert.Equal(1, await CountRowAsync(connectionString, storeId));
        await BackdateRowAsync(connectionString, storeId, DateTimeOffset.Now.AddDays(-(TtlDays + 1)));

        var agent = CreateBareAgent(AgentName);
        var session = await store.GetSessionAsync(agent, threadId);

        // 当新会话返回：不含任何历史
        Assert.Equal(string.Empty, HistoryText(agent, session));
        // best-effort 删行生效
        Assert.Equal(0, await CountRowAsync(connectionString, storeId));
    }

    [Fact]
    public async Task GetSession_WhenRowFresh_RestoresMessages()
    {
        // R4 场景 2：未过期行正常反序列化，消息历史与落库时一致
        var (connectionString, store) = await NewInitializedStoreAsync();
        const string threadId = "thread-fresh";
        var storeId = $"{AgentName}:{threadId}";
        const string reply = "新鲜会话回复 FRESH-MARKER";

        await SaveSessionWithHistoryAsync(store, threadId, reply);

        var agent = CreateBareAgent(AgentName);
        var session = await store.GetSessionAsync(agent, threadId);

        Assert.Contains(reply, HistoryText(agent, session));
        Assert.Equal(1, await CountRowAsync(connectionString, storeId));
    }

    [Fact]
    public async Task GetSession_WhenTtlDisabled_DoesNotTreatRowAsExpired()
    {
        // R7 场景 2：ttlDays <= 0 时 GetSessionAsync 对任意行都不按过期处理（即使 updated_at 极旧仍正常还原）
        var sessionDbPath = NewDbPath("sessions");
        var connectionString = $"Data Source={sessionDbPath}";
        var store = new SqliteAgentSessionStore(
            connectionString,
            new AguiSessionOptions { SessionTtlDays = 0 });
        await store.InitializeAsync();
        const string threadId = "thread-ttl-disabled";
        var storeId = $"{AgentName}:{threadId}";
        const string reply = "TTL 禁用仍应还原 DISABLED-MARKER";

        await SaveSessionWithHistoryAsync(store, threadId, reply);
        await BackdateRowAsync(connectionString, storeId, DateTimeOffset.Now.AddDays(-1000));

        var agent = CreateBareAgent(AgentName);
        var session = await store.GetSessionAsync(agent, threadId);

        Assert.Contains(reply, HistoryText(agent, session));
        Assert.Equal(1, await CountRowAsync(connectionString, storeId));
    }

    // ---------- 索引（InitializeAsync）----------

    [Fact]
    public async Task InitializeAsync_CreatesUpdatedAtIndex()
    {
        // R6 场景 2：InitializeAsync 后 sqlite_master 存在名为 idx_agent_sessions_updated_at 且作用于 agent_sessions(updated_at) 的索引
        var (connectionString, _) = await NewInitializedStoreAsync();

        var index = await ReadIndexAsync(connectionString, "idx_agent_sessions_updated_at");

        Assert.NotNull(index);
        Assert.Equal("agent_sessions", index.Value.Table);
        Assert.Contains("agent_sessions", index.Value.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updated_at", index.Value.Sql, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- helpers ----------

    private async Task<(string ConnectionString, SqliteAgentSessionStore Store)> NewInitializedStoreAsync()
    {
        var sessionDbPath = NewDbPath("sessions");
        var connectionString = $"Data Source={sessionDbPath}";
        var store = new SqliteAgentSessionStore(connectionString);
        await store.InitializeAsync();
        return (connectionString, store);
    }

    /// <summary>经 store 正常落一个含一轮 user+assistant 历史的会话（updated_at 为落库时的新鲜时间）。</summary>
    private static async Task SaveSessionWithHistoryAsync(SqliteAgentSessionStore store, string threadId, string assistantReply)
    {
        var agent = CreateBareAgent(AgentName);
        var session = await agent.CreateSessionAsync();
        var messages = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider).GetMessages(session);
        messages.Add(new Meai.ChatMessage(Meai.ChatRole.User, "帮我推荐一双跑步鞋"));
        messages.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, assistantReply));
        await store.SaveSessionAsync(agent, threadId, session);
    }

    /// <summary>直接写入一行（session_json 占位 "{}"；仅用于删除/计数类断言，不经反序列化）。</summary>
    private static async Task InsertRowAsync(string connectionString, string storeId, DateTimeOffset updatedAt)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_sessions (store_id, session_json, updated_at) VALUES ($id, '{}', $ts)";
        command.Parameters.AddWithValue("$id", storeId);
        command.Parameters.AddWithValue("$ts", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>回填某行的 updated_at（与写入侧同格式 "O"，字符串序即时间序）。</summary>
    private static async Task BackdateRowAsync(string connectionString, string storeId, DateTimeOffset updatedAt)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_sessions SET updated_at = $ts WHERE store_id = $id";
        command.Parameters.AddWithValue("$ts", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", storeId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> CountRowAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<(string Table, string Sql)?> ReadIndexAsync(string connectionString, string indexName)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl_name, sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return (reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
        return null;
    }
}
