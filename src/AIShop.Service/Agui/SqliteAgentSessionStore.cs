#pragma warning disable MAAI001 // CompactionStrategy 为 MAF [Experimental]（上下文压缩 API，会话快照收敛）
using System.Text.Json;
using AIShop.Core.Interfaces;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.Service.Agui;

public sealed class SqliteAgentSessionStore : AgentSessionStore
{
    private const string CreateTableSql =
        """
        CREATE TABLE IF NOT EXISTS agent_sessions (
            store_id TEXT PRIMARY KEY,
            session_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private const string CreateIndexSql =
        """
        CREATE INDEX IF NOT EXISTS idx_agent_sessions_updated_at ON agent_sessions (updated_at)
        """;

    private readonly string _connectionString;
    private readonly AguiSessionOptions _options;
    private readonly CompactionStrategy _compactionStrategy;
    private readonly ICurrentUserAccessor? _currentUserAccessor;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteAgentSessionStore(
        string connectionString,
        AguiSessionOptions? options = null,
        CompactionStrategy? compactionStrategy = null,
        ICurrentUserAccessor? currentUserAccessor = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _options = options ?? new AguiSessionOptions();
        _compactionStrategy = compactionStrategy ?? AguiCompaction.CreateStrategy();
        _currentUserAccessor = currentUserAccessor;
    }

    public string ConnectionString => _connectionString;
    public AguiSessionOptions Options => _options;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = CreateIndexSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);

        // 保存失败不得上抛：调用点在 AG-UI 端点内（镜像 AGUIEndpointRouteBuilderExtensions.cs:197），
        // 位于 SSE 流【所有事件之后】且无兜底——上抛会让「响应已完整吐出」的流异常断开（RUN_FINISHED 不发出，
        // 客户端表现为连接中断）。与 SqlChatHistoryProvider.InvokedCoreAsync 的落库兜底同一口径。
        // 代价（有意接受）：本次快照不落库，仅留 Error 日志。会话标识 conversation_id 取自用户名（不依赖快照），
        // 下次请求会重建会话并重新打标，故不丢会话归属；丢失的只是快照内的缓存副本。
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            await CompactSessionHistoryAsync(agent, session, cancellationToken).ConfigureAwait(false);

            // 清除 InMemoryChatHistoryProvider 的消息（消息走 chat_messages 表）

#pragma warning disable S125 // Sections of code should not be commented out
                            //session.SetInMemoryChatHistory(new List<ChatMessage>(),stateKey:nameof(SqlChatHistoryProvider));
                            // 剔除 CompactionProvider 的运行时状态（不需要持久化）
            session.StateBag.TryRemoveValue("AGUIShopping-Compaction");
#pragma warning restore S125 // Sections of code should not be commented out

            JsonElement json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
            var storeId = ResolveStoreId(agent.Name, sessionStoreId);
            var updatedAt = DateTimeOffset.Now.ToString("O");
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO agent_sessions (store_id, session_json, updated_at) VALUES ($storeId, $sessionJson, $updatedAt) ON CONFLICT(store_id) DO UPDATE SET session_json = excluded.session_json, updated_at = excluded.updated_at";
            command.Parameters.AddWithValue("$storeId", storeId);
            command.Parameters.AddWithValue("$sessionJson", json.GetRawText());
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "会话快照保存失败，本次不入库但请求继续（StoreId {StoreId}）", sessionStoreId);
        }
    }

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var storeId = ResolveStoreId(agent.Name, sessionStoreId);
        var row = await ReadSessionRowAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return await CreateSessionTaggedWithConversationIdAsync(agent, sessionStoreId, cancellationToken).ConfigureAwait(false);
        var (sessionJson, updatedAt) = row.Value;
        if (_options.IsTtlEnabled && updatedAt is not null &&
            string.CompareOrdinal(updatedAt, GetExpiryCutoff(_options.SessionTtlDays)) < 0)
        {
            await DeleteExpiredRowBestEffortAsync(storeId, cancellationToken).ConfigureAwait(false);
            return await CreateSessionTaggedWithConversationIdAsync(agent, sessionStoreId, cancellationToken).ConfigureAwait(false);
        }
        using var document = JsonDocument.Parse(sessionJson);
        var session = await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: cancellationToken).ConfigureAwait(false);
        TagConversationId(session, sessionStoreId);
        return session;
    }

    public override async ValueTask DeleteSessionAsync(
        AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var storeId = ResolveStoreId(agent.Name, sessionStoreId);
        await DeleteRowAsync(storeId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> CleanupExpiredAsync(int ttlDays, int batchSize = 500, CancellationToken cancellationToken = default)
    {
        if (ttlDays <= 0 || batchSize <= 0) return 0;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = GetExpiryCutoff(ttlDays);
        int totalDeleted = 0;
        while (true)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_sessions WHERE store_id IN (SELECT store_id FROM agent_sessions WHERE updated_at < $cutoff LIMIT $batch)";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$batch", batchSize);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += affected;
            if (affected < batchSize) break;
        }
        return totalDeleted;
    }

    private string ResolveStoreId(string? agentName, string sessionStoreId)
    {
        var username = _currentUserAccessor?.CurrentUser;
        if (!string.IsNullOrWhiteSpace(username))
            return $"{agentName}:{username}";
        Log.Error("用户上下文缺失，会话归属回退到 threadId（{ThreadId}）", sessionStoreId);
        return $"{agentName}:{sessionStoreId}";
    }

    private async ValueTask<AgentSession> CreateSessionTaggedWithConversationIdAsync(AIAgent agent, string sessionStoreId, CancellationToken ct)
    {
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        TagConversationId(session, sessionStoreId);
        return session;
    }

    private void TagConversationId(AgentSession session, string sessionStoreId)
    {
        if (session.StateBag.TryGetValue<string>(AguiSessionStateKeys.ConversationId, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return;
        session.StateBag.SetValue(AguiSessionStateKeys.ConversationId, ResolveConversationId(sessionStoreId));
    }

    private string ResolveConversationId(string sessionStoreId)
    {
        var username = _currentUserAccessor?.CurrentUser;
        return string.IsNullOrWhiteSpace(username) ? sessionStoreId : username;
    }

    private async Task CompactSessionHistoryAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        var chatClientAgent = agent.GetService<ChatClientAgent>();
        if (chatClientAgent?.ChatHistoryProvider is not InMemoryChatHistoryProvider provider)
            return;
        try
        {
            List<ChatMessage> messages = provider.GetMessages(session);
            int originalCount = messages.Count;
            IReadOnlyList<ChatMessage> compacted = await SnapshotCompactor
                .CompactAsync(_compactionStrategy, messages, _options.SessionMaxRounds, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (compacted.Count < originalCount)
                provider.SetMessages(session, [.. compacted]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "会话快照压缩失败，退化为原样快照落库");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => await InitializeAsync(cancellationToken).ConfigureAwait(false);

    private async Task<(string SessionJson, string? UpdatedAt)?> ReadSessionRowAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_json, updated_at FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionJson = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
            var updatedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (sessionJson, updatedAt);
        }
        return null;
    }

    private async Task DeleteRowAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteExpiredRowBestEffortAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteRowAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "惰性 TTL 删除过期会话行失败，已按新会话返回");
        }
    }

    private static string GetExpiryCutoff(int ttlDays) => DateTimeOffset.Now.AddDays(-ttlDays).ToString("O");
}
