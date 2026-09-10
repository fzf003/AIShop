using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Data.Sqlite;

namespace AIShop.AguiHost;

/// <summary>
/// SQLite 持久化 <see cref="AgentSessionStore"/>（agui-host T12：会话历史持久化）。
/// AG-UI 会话（ThreadId）经 <see cref="MapAGUIServer"/> 在流结束后 <c>SaveSessionAsync</c> 落库、同 ThreadId
/// 下次 <c>GetSessionAsync</c> 还原——重启不丢上下文（spec「AG-UI 会话历史持久化（重启不丢上下文）（T12）」）。
/// </summary>
/// <remarks>
/// <para>
/// 存储形态 = 自管理独立 SQLite 文件（默认 <c>agui.sessions.db</c>，见 <see cref="AguiServiceCollectionExtensions.DefaultSessionDbConnection"/>）：
/// 幂等建表 <c>CREATE TABLE IF NOT EXISTS agent_sessions</c>，不走 EF Migrations（勿给共享 Infra 迁移加表触碰老库）。
/// 表结构仿 <c>SqliteMemoryStore</c> 自管建表模式：
/// <code>
/// agent_sessions(store_id TEXT PRIMARY KEY, session_json TEXT NOT NULL, updated_at TEXT NOT NULL)
/// </code>
/// <c>session_json</c> = <c>agent.SerializeSessionAsync</c> 产出的会话 JSON（ChatClientAgent 会话含 StateBag，
/// InMemoryChatHistoryProvider 写入的消息历史随 StateBag 落库 → 还原即上下文不丢）。
/// </para>
/// <para>
/// <strong>Key 命名空间</strong>：<see cref="AgentSessionStore"/> 契约无 principal 维度；本 store 为 keyed 注册
/// （按 agent 名 = "AGUIShopping" 一个实例），store_id 以 <c>agent.Name</c> 前缀 + sessionStoreId（ThreadId）防
/// 多 agent 共享同一库文件时撞 key。多用户隔离（把 username/principal 编进 key）属后续路线（镜像信任模型：
/// ThreadId 来自 wire，仅作续接标识，非授权令牌），MVP 以 ThreadId 区分会话即可（tasks T12 注记）。
/// </para>
/// </remarks>
internal sealed class SqliteAgentSessionStore : AgentSessionStore
{
    /// <summary>agent_sessions 建表 DDL（幂等；列名/表名均为内部受控常量，无用户输入拼接）。</summary>
    private const string CreateTableSql =
        """
        CREATE TABLE IF NOT EXISTS agent_sessions (
            store_id TEXT PRIMARY KEY,
            session_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """;

    private readonly string _connectionString;
    private readonly AguiSessionOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>初始化 <see cref="SqliteAgentSessionStore"/>。</summary>
    /// <param name="connectionString">SQLite 连接串（独立会话库，如 <c>Data Source=agui.sessions.db</c>，不得为老 aishop.db）。</param>
    /// <param name="options">会话配置（TTL / 清理周期 / 快照轮数上限）；null 时回退 <see cref="AguiSessionOptions"/> 类默认（30/12/12），
    /// 保持既有 <c>new SqliteAgentSessionStore(conn)</c> 调用点源码兼容（S3）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> 为 null。</exception>
    public SqliteAgentSessionStore(string connectionString, AguiSessionOptions? options = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _options = options ?? new AguiSessionOptions();
    }

    /// <summary>会话库连接串（供测试断言指向独立库、非老 aishop.db）。</summary>
    internal string ConnectionString => _connectionString;

    /// <summary>会话配置（S4/S5 消费 TTL / 清理周期 / 快照轮数上限；测试可断言绑定生效）。</summary>
    internal AguiSessionOptions Options => _options;

    /// <summary>
    /// 幂等建表（启动预热或首次访问兜底）。AguiHost 启动引导（<see cref="AguiServiceCollectionExtensions.InitializeAsync"/>）
    /// 会预热调用一次，使首个会话请求不承担 DDL；测试亦可直调。重复调用零副作用。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>镜像 <c>InMemoryAgentSessionStore.SaveSessionAsync</c>：<c>agent.SerializeSessionAsync(session)</c> →
    /// JSON upsert（同 store_id 覆盖旧会话，保证同 ThreadId 多次续聊只保留最新快照）。</remarks>
    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // 序列化会话（含 StateBag / InMemoryChatHistoryProvider 消息历史）为 JSON，随后整行落库
        JsonElement json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        var storeId = GetStoreId(agent.Name, sessionStoreId);
        var updatedAt = DateTimeOffset.Now.ToString("O");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_sessions (store_id, session_json, updated_at)
            VALUES ($storeId, $sessionJson, $updatedAt)
            ON CONFLICT(store_id) DO UPDATE SET
                session_json = excluded.session_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$storeId", storeId);
        command.Parameters.AddWithValue("$sessionJson", json.GetRawText());
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>命中 → <c>agent.DeserializeSessionAsync</c> 还原为<b>独立新实例</b>（每次调用独立快照，符合
    /// <see cref="AgentSessionStore.GetSessionAsync"/> 隔离契约）；未命中 → <c>agent.CreateSessionAsync</c>（等价 Noop 语义）。</remarks>
    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var storeId = GetStoreId(agent.Name, sessionStoreId);
        var sessionJson = await ReadSessionJsonAsync(storeId, cancellationToken).ConfigureAwait(false);

        if (sessionJson is null)
        {
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        // JsonElement 只在 JsonDocument 存活期间有效；DeserializeSessionAsync 同步解析完成，await 内 doc 保持存活。
        using var document = JsonDocument.Parse(sessionJson);
        return await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>按 store_id 删除会话行；无该行时为空操作（镜像 <see cref="NoopAgentSessionStore"/>/InMemory 语义）。</remarks>
    public override async ValueTask DeleteSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var storeId = GetStoreId(agent.Name, sessionStoreId);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>store_id = "{agentName}:{sessionStoreId}"。用 agent.Name（稳定，重启不变）而非 agent.Id（随机 GUID，每实例不同），
    /// 保证持久化 key 跨宿主重启稳定；多 agent 共享同一库文件时按名称天然隔离。</summary>
    private static string GetStoreId(string? agentName, string sessionStoreId) => $"{agentName}:{sessionStoreId}";

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => await InitializeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>读会话 JSON 行；不存在返回 null。</summary>
    private async Task<string?> ReadSessionJsonAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_json FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);

         await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow,cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return reader.IsDBNull(0) ? "{}" : reader.GetString(0);
        }

        return "{}";
  
    }
}
