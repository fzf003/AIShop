#pragma warning disable MAAI001 // CompactionStrategy 为 MAF [Experimental]（上下文压缩 API，会话快照收敛）
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.Service.Agui;

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
public sealed class SqliteAgentSessionStore : AgentSessionStore
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

    /// <summary>TTL 阈值查询所用 <c>updated_at</c> 索引 DDL（幂等；既有库首启即补建，spec R6）。</summary>
    private const string CreateIndexSql =
        """
        CREATE INDEX IF NOT EXISTS idx_agent_sessions_updated_at ON agent_sessions (updated_at)
        """;

    private readonly string _connectionString;
    private readonly AguiSessionOptions _options;
    private readonly CompactionStrategy _compactionStrategy;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>初始化 <see cref="SqliteAgentSessionStore"/>。</summary>
    /// <param name="connectionString">SQLite 连接串（独立会话库，如 <c>Data Source=agui.sessions.db</c>，不得为老 aishop.db）。</param>
    /// <param name="options">会话配置（TTL / 清理周期 / 快照轮数上限）；null 时回退 <see cref="AguiSessionOptions"/> 类默认（30/12/12），
    /// 保持既有 <c>new SqliteAgentSessionStore(conn)</c> 调用点源码兼容（S3）。</param>
    /// <param name="compactionStrategy">落库前快照收敛所用压缩策略；null 时回退 <see cref="AguiCompaction.CreateStrategy"/>（S4）。
    /// 生产经 DI 注入与 <c>AGUIShoppingAgent</c> 侧同一单例，保证策略/阈值单一来源（spec R1「复用装配同一实例」）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> 为 null。</exception>
    public SqliteAgentSessionStore(
        string connectionString,
        AguiSessionOptions? options = null,
        CompactionStrategy? compactionStrategy = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _options = options ?? new AguiSessionOptions();
        // null → AguiCompaction.CreateStrategy()：与 AGUIShoppingAgent.Create 同一缺省语义（阈值/策略唯一来源），
        // 保证既有直构调用点源码兼容且未注入时不产生第二套阈值（S4）。
        _compactionStrategy = compactionStrategy ?? AguiCompaction.CreateStrategy();
    }

    /// <summary>会话库连接串（供测试断言指向独立库、非老 aishop.db）。</summary>
    public string ConnectionString => _connectionString;

    /// <summary>会话配置（S4/S5 消费 TTL / 清理周期 / 快照轮数上限；测试可断言绑定生效）。</summary>
    public AguiSessionOptions Options => _options;

    /// <summary>
    /// 幂等建表 + 建索引（启动预热或首次访问兜底）。AguiHost 启动引导（<see cref="AguiServiceCollectionExtensions.InitializeAsync"/>）
    /// 会预热调用一次，使首个会话请求不承担 DDL；测试亦可直调。重复调用零副作用。
    /// </summary>
    /// <remarks>
    /// 建表后追加 <c>CREATE INDEX IF NOT EXISTS idx_agent_sessions_updated_at</c>（spec R6「updated_at 索引」）——
    /// 支撑 <see cref="CleanupExpiredAsync"/> 与惰性 TTL 的阈值查询；既有库首次启动即补建（幂等）。
    /// </remarks>
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

            // 建表后幂等建索引（既有库首启补建；spec R6）。
            command.CommandText = CreateIndexSql;
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
    /// JSON upsert（同 store_id 覆盖旧会话，保证同 ThreadId 多次续聊只保留最新快照）。
    /// <para>
    /// S4（agui-session-prod）：序列化之前先做<strong>收敛快照</strong>（<see cref="CompactSessionHistoryAsync"/>）——
    /// 使落库快照大小有界（spec R1）。压缩失败仅告警、<strong>不阻断落库</strong>。
    /// </para></remarks>
    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // 收敛快照（仅 InMemoryChatHistoryProvider 路径；其余跳过）——必须在 SerializeSessionAsync 前完成，
        // 使写回的压缩历史随会话一并落库（spec R1）。
        await CompactSessionHistoryAsync(agent, session, cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// 命中且未过期 → <c>agent.DeserializeSessionAsync</c> 还原为<b>独立新实例</b>（每次调用独立快照，符合
    /// <see cref="AgentSessionStore.GetSessionAsync"/> 隔离契约）；无行 → <c>agent.CreateSessionAsync</c>（等价 Noop 语义）。
    /// <para>
    /// S5（agui-session-prod）惰性 TTL 兜底（design §5.4 / spec R4）：一次查询取 <c>session_json + updated_at</c>，
    /// 若 TTL 启用（<see cref="AguiSessionOptions.IsTtlEnabled"/>）且 <c>updated_at</c> 早于闲置阈值，该行视为过期
    /// → best-effort 删行（失败仅 Warning，后台批次兜底）并当<b>新会话</b>返回。过期与否单一归一语义来自选项类派生属性，
    /// store 内不再自行判断 <c>&lt;= 0</c>（handoff-S3 决策 1）。
    /// </para>
    /// </remarks>
    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var storeId = GetStoreId(agent.Name, sessionStoreId);
        var row = await ReadSessionRowAsync(storeId, cancellationToken).ConfigureAwait(false);

        // 无行 → 新会话（修正旧 ReadSessionJsonAsync 无行返回 "{}" 的行为，spec R4）。
        if (row is null)
            return await CreateSessionTaggedWithConversationIdAsync(agent, sessionStoreId, cancellationToken).ConfigureAwait(false);

        var (sessionJson, updatedAt) = row.Value;

        // 惰性 TTL 过期兜底：TTL 启用且 updated_at 早于 cutoff（字符串序即时间序，见 GetExpiryCutoff）。
        if (_options.IsTtlEnabled && updatedAt is not null &&
            string.CompareOrdinal(updatedAt, GetExpiryCutoff(_options.SessionTtlDays)) < 0)
        {
            await DeleteExpiredRowBestEffortAsync(storeId, cancellationToken).ConfigureAwait(false);
            return await CreateSessionTaggedWithConversationIdAsync(agent, sessionStoreId, cancellationToken).ConfigureAwait(false);
        }

        // JsonElement 只在 JsonDocument 存活期间有效；DeserializeSessionAsync 同步解析完成，await 内 doc 保持存活。
        using var document = JsonDocument.Parse(sessionJson);
        var session = await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 把本次取会话所用的 ThreadId 落进会话 StateBag（键已存在则保留），供 SqlChatHistoryProvider 用作 conversation_id。
        TagConversationId(session, sessionStoreId);
        return session;
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
        await DeleteRowAsync(storeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 分批删除 <c>updated_at</c> 早于阈值的过期会话行（agui-session-prod S5，design §5.5 / spec R6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每批删除以 <c>store_id IN (SELECT ... WHERE updated_at &lt; $cutoff LIMIT $batch)</c> 限定批量、
    /// <b>每批独立连接</b>（短事务，SQLite autocommit 单语句原子），循环至单批 <c>affected &lt; batchSize</c>（删净），
    /// 返回累计删除数——避免一次性删海量行长时间持锁、阻塞并行会话写入。
    /// </para>
    /// <para>
    /// <paramref name="ttlDays"/> <c>&lt;= 0</c> 直接返回 0（禁用 TTL）；<paramref name="batchSize"/> <c>&lt;= 0</c>
    /// 返回 0（防御除零/死循环）。
    /// </para>
    /// </remarks>
    /// <param name="ttlDays">闲置生存天数阈值；<c>&lt;= 0</c> 表示禁用（不删）。</param>
    /// <param name="batchSize">单批删除上限（避免长锁），默认 500。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>累计删除的会话行数。</returns>
    public async ValueTask<int> CleanupExpiredAsync(
        int ttlDays,
        int batchSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (ttlDays <= 0 || batchSize <= 0)
            return 0;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = GetExpiryCutoff(ttlDays);
        int totalDeleted = 0;
        while (true)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM agent_sessions
                WHERE store_id IN (
                    SELECT store_id FROM agent_sessions WHERE updated_at < $cutoff LIMIT $batch
                )
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$batch", batchSize);

            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += affected;

            if (affected < batchSize)
                break;
        }

        return totalDeleted;
    }

    /// <summary>store_id = "{agentName}:{sessionStoreId}"。用 agent.Name（稳定，重启不变）而非 agent.Id（随机 GUID，每实例不同），
    /// 保证持久化 key 跨宿主重启稳定；多 agent 共享同一库文件时按名称天然隔离。</summary>
    private static string GetStoreId(string? agentName, string sessionStoreId) => $"{agentName}:{sessionStoreId}";

    /// <summary>
    /// <c>CreateSessionAsync</c> 后打上会话标识（新会话 / 过期重开两条路径共用）。
    /// </summary>
    private static async ValueTask<AgentSession> CreateSessionTaggedWithConversationIdAsync(
        AIAgent agent, string sessionStoreId, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        TagConversationId(session, sessionStoreId);
        return session;
    }

    /// <summary>
    /// 把取会话所用的 AG-UI <c>ThreadId</c>（<paramref name="sessionStoreId"/>）写进会话 StateBag 的
    /// <see cref="AguiSessionStateKeys.ConversationId"/> 键，供 <see cref="SqlChatHistoryProvider"/> 以 ThreadId 作为
    /// <c>chat_messages.conversation_id</c>（design-sql-chat-history-provider §2.2「会话标识（ThreadId）」、
    /// §10.3 按 <c>conversation_id='thread-...'</c> 审计）。
    /// </summary>
    /// <remarks>
    /// <b>键已存在时保留、不覆盖</b>：会话行按 <c>{agentName}:{ThreadId}</c> 唯一标识，从该行读出的会话其标识只可能等于本
    /// ThreadId；保留已有值可 (1) 避免把「标识以其它方式生成的既有会话」的历史按新 key 劈成两段（连续性优先），
    /// (2) 读路径值未变则不改写会话快照、不产生 session_json 无谓 diff。仅当键缺失 / 空白（新会话、或未接线宿主）时写入当前 ThreadId。
    /// 取舍备选「总是覆盖」在本特性默认关闭、无存量 <c>chat_messages</c> 行时同样安全，但会牺牲上述连续性并每次改写快照。
    /// </remarks>
    private static void TagConversationId(AgentSession session, string sessionStoreId)
    {
        if (session.StateBag.TryGetValue<string>(AguiSessionStateKeys.ConversationId, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            return;
        }

        session.StateBag.SetValue(AguiSessionStateKeys.ConversationId, sessionStoreId);
    }

    /// <summary>
    /// 落库前对会话历史做收敛快照（agui-session-prod S4，design §4.4 / spec R1-R3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 仅当 agent 解析为 <see cref="ChatClientAgent"/> 且其 <see cref="ChatClientAgent.ChatHistoryProvider"/> 为
    /// <see cref="InMemoryChatHistoryProvider"/> 时才收敛：取其消息历史 → <see cref="SnapshotCompactor"/> 轮归一
    /// （保轮/工具配对，spec R2；受保护最后 ≥2 轮 + <see cref="AguiSessionOptions.SessionMaxRounds"/> 硬上限，spec R3）
    /// → <b>确有收缩才</b>写回。其余情况（非该 provider / provider 为 null / 远端托管会话）<b>跳过压缩</b>，
    /// 按现状序列化，保持既有语义零回归（design §4.4 防御）。
    /// </para>
    /// <para>
    /// <see cref="AIAgent.GetService{TService}(object)"/> 缝可穿透 <c>OpenTelemetryAgent</c> 装饰器（官方同款用法）。
    /// 压缩异常一律捕获并记 Warning，退化为原样快照落库（spec R1 场景 2，不抛出、不丢会话）。
    /// </para>
    /// <para>
    /// <b>副作用说明</b>：写回会同时收敛活动会话的内存历史（<c>SetMessages</c> 覆盖 StateBag backing list）——
    /// save 为流结束的终点操作，此时收敛安全且有益（design §4.4）。
    /// </para>
    /// </remarks>
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

            // 确有收缩才写回。快速路径可能返回【传入的同一实例】（引用相等但无收缩），故按 Count 判定，勿只比实例引用。
            if (compacted.Count < originalCount)
                provider.SetMessages(session, [.. compacted]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 压缩失败不阻断落库（spec R1 场景 2）：仅告警并退化为原样快照（下方 SerializeSessionAsync 原样序列化）。
            Log.Warning(ex, "会话快照压缩失败，退化为原样快照落库（会话不丢失）");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => await InitializeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>读会话行（<c>session_json</c> + <c>updated_at</c>）；不存在返回 null（供惰性 TTL 判定行是否存在）。</summary>
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
            // updated_at 为 NOT NULL；防御式取 null 表示「未知」，判定侧不作过期处理（避免误删）。
            var updatedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (sessionJson, updatedAt);
        }

        return null;
    }

    /// <summary>按 store_id 删除会话行（DeleteSessionAsync 与惰性 TTL 共用）。</summary>
    private async Task DeleteRowAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// best-effort 删除惰性 TTL 命中的过期行（design §5.4）：删除失败仅记 Warning，不阻断
    /// <c>CreateSessionAsync</c> 返回新会话——该行会由后台批次 <see cref="CleanupExpiredAsync"/> 兜底。
    /// </summary>
    private async Task DeleteExpiredRowBestEffortAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteRowAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 删除失败不阻断「当新会话返回」（spec R4 场景 1）：仅告警，后台批次将再次尝试。
            Log.Warning(ex, "惰性 TTL 删除过期会话行失败，已按新会话返回（后台批次将兜底）");
        }
    }

    /// <summary>
    /// TTL 过期阈值（字符串序即时间序，design §5.4 / §9）。
    /// 与写入侧 <see cref="SaveSessionAsync"/> 的 <c>DateTimeOffset.Now.ToString("O")</c> <b>同本地偏移、同定宽</b>，
    /// 故字符串 Ordinal 比较等价于时间比较。<b>不得改用 UtcNow</b>（与写入侧偏移不一致会破坏该不变量）。
    /// </summary>
    private static string GetExpiryCutoff(int ttlDays) => DateTimeOffset.Now.AddDays(-ttlDays).ToString("O");
}
