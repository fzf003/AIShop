#pragma warning disable MAAI001 // ChatHistoryProvider 会话上下文 API 属 MAF [Experimental]（Agent Framework 实验性本体）
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.Service.Agui;

/// <summary>
/// SQLite 持久化 <see cref="ChatHistoryProvider"/>（design-sql-chat-history-provider §1–§6）。
/// 每条 <see cref="ChatMessage"/> 一行独立存储（<c>chat_messages</c> 表），支持按会话 / 轮次查询；
/// 提供轮次完整性（TTL 清理整轮软删除，不拆断 FCC↔FRC 配对）与软删除（审计可查）。
/// </summary>
/// <remarks>
/// <para>
/// <b>存储形态</b>：自管理独立 SQLite 文件（默认 <c>agui.chat.db</c>，见 <see cref="SqlChatHistoryOptions.DefaultConnectionString"/>），
/// 幂等建表 <c>CREATE TABLE IF NOT EXISTS chat_messages</c> + 两个索引，不走 EF Migrations（勿触碰老库）。
/// 表结构见 design §2.1。
/// </para>
/// <para>
/// <b>会话标识（conversation_id）怎么拿</b>：MAF <see cref="ChatHistoryProvider"/> 只拿到 <see cref="AgentSession"/>
/// （<c>InvokingContext.Session</c> / <c>InvokedContext.Session</c>），拿不到 AG-UI wire 上的 <c>ThreadId</c>
/// （AG-UI 宿主仅把 ThreadId 用作 <c>AgentSessionStore</c> 的 key，不写入会话 StateBag，见镜像
/// <c>AGUIEndpointRouteBuilderExtensions.cs</c>）。因此照搬官方 <c>ValkeyChatHistoryProvider</c> 的机制：经
/// <see cref="ProviderSessionState{TState}"/> 把会话标识存进 <see cref="AgentSession.StateBag"/>，随会话快照一起被
/// <see cref="SqliteAgentSessionStore"/> 持久化——首次使用生成稳定 ID，后续同会话读回同一 ID（跨请求 / 重启稳定）。
/// <see cref="ProviderSessionState{TState}"/> 是 MAF Abstractions 的 public API（镜像
/// <c>ProviderSessionState{TState}.cs</c> / <c>ValkeyChatHistoryProvider.cs</c> L44/L67/L89），其构造要求调用方提供
/// <c>stateInitializer</c>；本类默认生成 GUID（<see cref="DefaultStateInitializer"/>），并开放构造参数供宿主 / 测试注入确定性标识。
/// </para>
/// <para>
/// <b>JSON 序列化</b>：<c>message_json</c> 必须无损往返多态内容（<see cref="FunctionCallContent"/> /
/// <see cref="FunctionResultContent"/> / <see cref="TextContent"/> 等），故用 MAF 的
/// <see cref="AgentAbstractionsJsonUtilities.DefaultOptions"/>——它把 M.E.AI 的 <c>AIJsonUtilities</c> 解析器链在最前
/// （镜像 <c>AgentAbstractionsJsonUtilities.cs</c> L38/L57 + <c>ValkeyChatHistoryProvider.cs</c> L75），
/// 承载 <c>AIContent</c> 的多态（<c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c>）往返。默认裸 <see cref="JsonSerializer"/>
/// 会丢失派生类型，不可用。
/// </para>
/// </remarks>
public sealed class SqlChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>chat_messages 建表 DDL（幂等；列名 / 表名均为内部受控常量，无用户输入拼接）。design §2.1。</summary>
    private const string CreateTableSql =
        """
        CREATE TABLE IF NOT EXISTS chat_messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            role TEXT NOT NULL,
            message_json TEXT NOT NULL,
            round_id INTEGER,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            deleted_at TEXT,
            created_at TEXT NOT NULL,
            UNIQUE(conversation_id, sequence)
        )
        """;

    /// <summary>按会话 + 序号查询索引（幂等）。design §2.1。</summary>
    private const string CreateIndexConversationSequenceSql =
        """
        CREATE INDEX IF NOT EXISTS idx_chat_messages_conv_seq ON chat_messages (conversation_id, sequence)
        """;

    /// <summary>按会话 + 轮次 + 软删除查询索引（幂等）。design §2.1。</summary>
    private const string CreateIndexRoundSql =
        """
        CREATE INDEX IF NOT EXISTS idx_chat_messages_round ON chat_messages (conversation_id, round_id, is_deleted)
        """;

    /// <summary>写入单条消息 DDL。</summary>
    private const string InsertSql =
        """
        INSERT INTO chat_messages
            (conversation_id, sequence, role, message_json, round_id, created_at)
        VALUES ($conversationId, $sequence, $role, $messageJson, $roundId, $createdAt)
        """;

    private readonly SqlChatHistoryOptions _options;
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ProviderSessionState<State> _sessionState;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private IReadOnlyList<string>? _stateKeys;

    /// <summary>初始化 <see cref="SqlChatHistoryProvider"/>。</summary>
    /// <param name="options">配置；null 时回退 <see cref="SqlChatHistoryOptions"/> 类默认（agui.chat.db / 2 轮 / 30 天 / 10 / 12）。</param>
    /// <param name="stateInitializer">会话状态初始化器（首次为某会话建状态时调用）；null 时回退
    /// <see cref="DefaultStateInitializer"/>（生成稳定 GUID）。供宿主 / 测试注入确定性会话标识。</param>
    /// <exception cref="ArgumentException"><see cref="SqlChatHistoryOptions.ConnectionString"/> 为空。</exception>
    public SqlChatHistoryProvider(
        SqlChatHistoryOptions? options = null,
        Func<AgentSession?, State>? stateInitializer = null)
    {
        _options = options ?? new SqlChatHistoryOptions();
        _connectionString = _options.ConnectionString;
        ArgumentException.ThrowIfNullOrWhiteSpace(_connectionString);

        // 无损往返多态 AIContent 的 JSON 选项（M.E.AI 解析器链，见类注释）。
        _jsonOptions = AgentAbstractionsJsonUtilities.DefaultOptions;

        // 会话标识经 ProviderSessionState 存 AgentSession.StateBag（同 Valkey 官方机制），
        // 随会话快照被 SqliteAgentSessionStore 持久化 → 跨请求 / 重启稳定。
        _sessionState = new ProviderSessionState<State>(
            stateInitializer ?? DefaultStateInitializer,
            GetType().Name,
            _jsonOptions);
    }

    /// <summary>配置（供测试断言与宿主读取）。</summary>
    public SqlChatHistoryOptions Options => _options;

    /// <inheritdoc />
    /// <remarks>状态键 = 具体类型名（<c>"SqlChatHistoryProvider"</c>），与压缩 / 记忆 provider 的 StateBag 键互不冲突。</remarks>
    public override IReadOnlyList<string> StateKeys => _stateKeys ??= [_sessionState.StateKey];

    /// <summary>
    /// 幂等建表 + 建索引（启动预热或首次访问兜底）。重复调用零副作用。
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

            command.CommandText = CreateIndexConversationSequenceSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.CommandText = CreateIndexRoundSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 加载最近 <see cref="SqlChatHistoryOptions.MaxRoundsToLoad"/> 个非删除轮（design §4）：先取这些轮的
    /// <c>round_id</c>（倒序取最近 N 个后恢复时间顺序），再按 <c>sequence</c> 升序返回其全部消息——整轮加载，
    /// 保证 FCC↔FRC 配对不被拆散。软删除轮（<c>is_deleted=1</c>）不参与加载。
    /// </remarks>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var conversationId = GetConversationId(context.Session);
        // design §9：MaxRoundsToLoad < 1 时按 1 处理（配置校验，避免无历史注入）。
        var maxRounds = _options.MaxRoundsToLoad < 1 ? 1 : _options.MaxRoundsToLoad;

        var roundIds = await GetRecentRoundIdsAsync(conversationId, maxRounds, cancellationToken).ConfigureAwait(false);
        if (roundIds.Count == 0)
            return [];

        return await GetMessagesByRoundsAsync(conversationId, roundIds, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 把本轮请求消息 + 响应消息整批落库（design §3）：<c>RequestMessages</c> 已被基类默认过滤器剔除历史消息
    /// （<see cref="AgentRequestMessageSourceType.ChatHistory"/>），故这里只会写入本轮新增消息——不会重复落历史。
    /// </remarks>
    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var messages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .ToList();
        if (messages.Count == 0)
            return;

        var conversationId = GetConversationId(context.Session);
        await InsertMessagesWithRetryAsync(conversationId, messages, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 分批整轮软删除过期轮（design §5）：按轮第一条消息的 <c>created_at</c> 与 cutoff 比较，
    /// 每批至多 <paramref name="batchSize"/> 轮，整轮标记 <c>is_deleted=1</c> + <c>deleted_at</c>（不拆断 FCC↔FRC）。
    /// </summary>
    /// <param name="ttlDays">闲置生存天数阈值；<c>&lt;= 0</c> 表示禁用（直接返回 0）。</param>
    /// <param name="batchSize">单批处理的轮数上限（避免长事务锁表），默认 10；<c>&lt;= 0</c> 返回 0。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>累计软删除的轮数。</returns>
    public async Task<int> CleanupExpiredRoundsAsync(
        int ttlDays,
        int batchSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (ttlDays <= 0 || batchSize <= 0)
            return 0;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = DateTimeOffset.Now.AddDays(-ttlDays).ToString("O");
        var totalDeletedRounds = 0;

        while (true)
        {
            var expiredRounds = await FindExpiredRoundsAsync(cutoff, batchSize, cancellationToken).ConfigureAwait(false);
            if (expiredRounds.Count == 0)
                break;

            var deletedAt = DateTimeOffset.Now.ToString("O");
            await using (var connection = new SqliteConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                // BeginTransactionAsync 返回 DbTransaction，转成强类型 SqliteTransaction 供 SqliteCommand.Transaction 赋值。
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                foreach (var (conversationId, roundId) in expiredRounds)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        UPDATE chat_messages
                        SET is_deleted = 1, deleted_at = $deletedAt
                        WHERE conversation_id = $conversationId
                          AND round_id = $roundId
                          AND is_deleted = 0
                        """;
                    command.Parameters.AddWithValue("$deletedAt", deletedAt);
                    command.Parameters.AddWithValue("$conversationId", conversationId);
                    command.Parameters.AddWithValue("$roundId", roundId);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            totalDeletedRounds += expiredRounds.Count;
            if (expiredRounds.Count < batchSize)
                break;
        }

        return totalDeletedRounds;
    }

    /// <summary>默认状态初始化器：为新会话生成稳定会话标识（存 StateBag，随会话快照持久化）。</summary>
    private static readonly Func<AgentSession?, State> DefaultStateInitializer = _ => new(Guid.NewGuid().ToString("N"));

    /// <summary>取（首次则初始化）当前会话的会话标识。</summary>
    private string GetConversationId(AgentSession? session)
        => _sessionState.GetOrInitializeState(session).ConversationId;

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => await InitializeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>读当前会话已落库的 <c>MAX(sequence)</c> 与 <c>MAX(round_id)</c>（均只看非删除行，design §3.2）。</summary>
    private async Task<(int MaxSequence, int CurrentRound)> GetMaxSequenceAndRoundAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(sequence), 0), COALESCE(MAX(round_id), 0)
            FROM chat_messages
            WHERE conversation_id = $conversationId AND is_deleted = 0
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (reader.GetInt32(0), reader.GetInt32(1));

        return (0, 0);
    }

    /// <summary>取最近 <paramref name="maxRounds"/> 个非删除轮的 <c>round_id</c>（时间顺序返回，design §4.2）。</summary>
    private async Task<List<int>> GetRecentRoundIdsAsync(
        string conversationId, int maxRounds, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT round_id FROM chat_messages
            WHERE conversation_id = $conversationId AND is_deleted = 0 AND round_id IS NOT NULL
            ORDER BY round_id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$limit", maxRounds);

        var roundIds = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            roundIds.Add(reader.GetInt32(0));

        roundIds.Reverse(); // 恢复时间（升序）顺序
        return roundIds;
    }

    /// <summary>按给定轮次加载全部消息（<c>sequence</c> 升序，design §4.3）。</summary>
    private async Task<List<ChatMessage>> GetMessagesByRoundsAsync(
        string conversationId, List<int> roundIds, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var placeholders = string.Join(",", roundIds.Select((_, i) => $"$r{i}"));
        await using var command = connection.CreateCommand();
        // S2077 误报：这里插入 SQL 的只是【参数名占位符】（$r0、$r1…，由 roundIds.Count 生成），
        // 真正取值全部走下方 AddWithValue 参数绑定，无用户输入拼接。
#pragma warning disable S2077
        command.CommandText =
            $"""
            SELECT message_json FROM chat_messages
            WHERE conversation_id = $conversationId
              AND round_id IN ({placeholders})
              AND is_deleted = 0
            ORDER BY sequence ASC
            """;
#pragma warning restore S2077
        command.Parameters.AddWithValue("$conversationId", conversationId);
        for (var i = 0; i < roundIds.Count; i++)
            command.Parameters.AddWithValue($"$r{i}", roundIds[i]);

        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(reader.GetString(0), _jsonOptions);
            if (message is not null)
                messages.Add(message);
        }

        return messages;
    }

    /// <summary>取一批过期轮（每轮第一条消息 <c>created_at</c> 早于 <paramref name="cutoff"/>，最旧优先，design §5.2）。</summary>
    private async Task<List<(string ConversationId, int RoundId)>> FindExpiredRoundsAsync(
        string cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // round_id IS NOT NULL 守卫：本 provider 写入的 round_id 恒非空，排除存量 NULL 组可保证下面的
        // `round_id = $roundId` 更新必命中，永不出现「找到但更新 0 行 → 下批再次找到」的死循环。
        command.CommandText =
            """
            SELECT conversation_id, round_id
            FROM chat_messages
            WHERE is_deleted = 0 AND round_id IS NOT NULL
            GROUP BY conversation_id, round_id
            HAVING MIN(created_at) < $cutoff
            ORDER BY MIN(created_at) ASC
            LIMIT $batch
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$batch", batchSize);

        var rounds = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rounds.Add((reader.GetString(0), reader.GetInt32(1)));

        return rounds;
    }

    /// <summary>
    /// 整批事务插入（design §3.1 + §11.1 乐观重试）：读 <c>MAX(sequence)/MAX(round_id)</c> → 单事务逐条插入 →
    /// 提交；冲突（<c>UNIQUE(conversation_id, sequence)</c>，并发写入同会话）则回滚重试，最多 3 次后抛
    /// <see cref="InvalidOperationException"/>。事务保证「一轮多条要么全写、要么全回滚」。
    /// </summary>
    /// <remarks>
    /// <c>round_id</c> 口径以 design §2.3 示例表为准：从 1 起，仅遇到 <see cref="ChatRole.User"/> 时递增；
    /// 新会话（<c>currentRound=0</c>）的前导非 User 消息（如 <c>system</c>）归第 1 轮，故插入时对 <c>round==0</c>
    /// 归一为 1（修正 design §3.1 代码会产出 <c>round_id=0</c> 与 §2.3 示例表矛盾之处）。
    /// </remarks>
    private async Task InsertMessagesWithRetryAsync(
        string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var (maxSequence, currentRound) = await GetMaxSequenceAndRoundAsync(conversationId, cancellationToken).ConfigureAwait(false);
            var sequence = maxSequence;
            var round = currentRound;
            var createdAt = DateTimeOffset.Now.ToString("O");

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // BeginTransactionAsync 返回 DbTransaction，转成强类型 SqliteTransaction 供 SqliteCommand.Transaction 赋值。
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var message in messages)
                {
                    // 遇到新的 User 消息 → 新轮次（design §2.3）。
                    if (message.Role == ChatRole.User)
                        round++;

                    // §2.3 示例表口径：前导（首轮 User 之前）的非 User 消息属第 1 轮，不得落到 round_id=0。
                    var effectiveRound = round == 0 ? 1 : round;
                    sequence++;

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = InsertSql;
                    command.Parameters.AddWithValue("$conversationId", conversationId);
                    command.Parameters.AddWithValue("$sequence", sequence);
                    command.Parameters.AddWithValue("$role", message.Role.Value ?? "assistant");
                    command.Parameters.AddWithValue("$messageJson", JsonSerializer.Serialize(message, _jsonOptions));
                    command.Parameters.AddWithValue("$roundId", effectiveRound);
                    command.Parameters.AddWithValue("$createdAt", createdAt);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT：UNIQUE(conversation_id, sequence) 冲突
            {
                // 序列冲突（并发写入同会话）→ 回滚本批后重试：重新读取 MAX(sequence) 再插入（本批整体原子回滚）。
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                Log.Warning(ex, "聊天历史写入序列冲突，第 {Attempt}/{MaxRetries} 次重试（会话 {ConversationId}）",
                    attempt + 1, maxRetries, conversationId);
            }
        }

        throw new InvalidOperationException(
            $"会话 {conversationId} 的聊天历史写入在 {maxRetries} 次重试后仍因 sequence 冲突失败。");
    }

    /// <summary>
    /// 会话状态：承载会话标识（<see cref="ConversationId"/>）。经 <see cref="ProviderSessionState{TState}"/> 存
    /// <see cref="AgentSession.StateBag"/>，随会话快照持久化（镜像官方 <c>ValkeyChatHistoryProvider.State</c>）。
    /// </summary>
    public sealed class State
    {
        /// <summary>初始化会话状态。</summary>
        /// <param name="conversationId">会话唯一标识（对应 <c>chat_messages.conversation_id</c>）。</param>
        [JsonConstructor]
        public State(string conversationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            ConversationId = conversationId;
        }

        /// <summary>获取会话唯一标识。</summary>
        [JsonPropertyName("conversationId")]
        public string ConversationId { get; }
    }
}
