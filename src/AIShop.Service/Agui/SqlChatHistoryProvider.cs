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
/// <c>AGUIEndpointRouteBuilderExtensions.cs</c>）。故照搬官方 <c>ValkeyChatHistoryProvider</c> 的机制：经
/// <see cref="ProviderSessionState{TState}"/> 把会话标识存进 <see cref="AgentSession.StateBag"/>，随会话快照被
/// <see cref="SqliteAgentSessionStore"/> 持久化 → 跨请求 / 重启稳定（<see cref="ProviderSessionState{TState}"/> 是
/// MAF Abstractions 的 public API，见镜像 <c>ProviderSessionState{TState}.cs</c> / <c>ValkeyChatHistoryProvider.cs</c> L44/L67/L89）。
/// </para>
/// <para>
/// <b>标识 = ThreadId（design §2.2）</b>：ThreadId 由 <see cref="SqliteAgentSessionStore.GetSessionAsync"/> 取会话时写入
/// StateBag 的 <see cref="AguiSessionStateKeys.ConversationId"/> 键；<see cref="DefaultStateInitializer"/> 优先读该键，
/// 使 <c>conversation_id</c> 等于 AG-UI ThreadId（§1.3 / §10.3 的审计 / 召回按 ThreadId 可查）。未接线（纯单测直构
/// provider、StateBag 无该键）时回退生成 GUID，保证不崩；构造参数仍可注入确定性标识供测试。
/// </para>
/// <para>
/// <b>读写语义 = 只处理「当前轮次」，纯按位置判定（不依赖 message id）</b>：AG-UI 客户端每次会把<b>整段前文</b>
/// 随请求发来，其 payload 结构恒为 <c>[历史前缀…, 当前轮次]</c>，最后一条 user 消息即本轮起点。故
/// <see cref="StoreChatHistoryAsync"/> 只落库当前轮次 + 全部响应消息（重发的前缀一律不落库）；
/// <see cref="ProvideChatHistoryAsync"/> 只在客户端没带历史（curl 式只发本轮）时从库补历史，否则返回空——
/// 避免历史被反复写入库 / 反复返回给模型。**不按 <see cref="ChatMessage.MessageId"/> 比对**：客户端重发的
/// assistant 消息带的是客户端自己的 id，与库中服务端返回 id（如 <c>chatcmpl-…</c>）永远对不上。
/// 详见两个方法的 remarks。
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
public sealed class SqlChatHistoryProvider : ChatHistoryProvider, IChatHistoryCleaner
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
    /// <para>
    /// <b>语义：恒返回空，且不查库、不注入任何历史（「只存不取」定型）。</b>
    /// </para>
    /// <para>
    /// <b>① 为什么恒空</b>：会话上下文由<b>客户端每轮重发的全量历史</b>承载（真实 AG-UI 客户端的 wire 行为，
    /// 见 <see cref="StoreChatHistoryAsync"/> 的当前轮次切分）。若服务端在此再返回一遍历史，模型就会看到两遍
    /// （既费 token 又干扰模型），故恒空是「客户端重发是唯一上下文来源」这一前提的落点。
    /// </para>
    /// <para>
    /// <b>② 为什么原来的两个分支现在都返回空</b>：原实现按「客户端是否自带历史」分两支——
    /// ①客户端已带历史（<see cref="FindCurrentTurnStart"/> 返回 <c>&gt; 0</c>）→ 返回空避免重复；
    /// ②curl 式只发本轮 → 原应回填最近 N 个非删除轮（整轮，保 FCC↔FRC 配对）。第 ② 支的加载能力随
    /// 「只存不取」决策<b>整体停止并已删除</b>，故两支语义归并为同一个恒空实现；本方法不再打开任何连接。
    /// </para>
    /// <para>
    /// <b>③ 后果（有意为之，不是缺陷）</b>：curl 式只发当前轮次、不自带历史前缀的调用方将<b>完全失忆</b>——
    /// 不再有任何服务端补历史。这是「只存不取」的既定代价，请勿当作 bug「修复」。
    /// </para>
    /// <para>
    /// <b>④ 为什么显式 override 返回空、而不删掉 override 靠基类默认实现</b>：基类默认实现当前虽也返回 <c>[]</c>，
    /// 但那是 MAF 的实现细节；一旦上游把默认实现改成返回已存历史，服务端就会<b>静默开始重复注入</b>。
    /// 显式 override + 本注释把「有意空」固化进代码，是防回归护栏。配合「继续继承 <see cref="ChatHistoryProvider"/>
    /// 并占据 <c>ChatClientAgentOptions.ChatHistoryProvider</c> 槽位」构成三者缺一不可的不变量：槽位空出会让 MAF
    /// 退回会注入历史的 <c>InMemoryChatHistoryProvider</c>，模型同样看到两遍（design §4）。
    /// </para>
    /// </remarks>
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>语义：只落库「当前轮次」。</b>基类默认请求过滤器已剔除 provider 自己在
    /// <see cref="ProvideChatHistoryAsync"/> 里返回的历史（来源标记 <see cref="AgentRequestMessageSourceType.ChatHistory"/>），
    /// 但 AG-UI 客户端还会把<b>整段前文</b>当调用方消息重发——若整包落库，历史就被反复写入（本缺陷根因）。
    /// </para>
    /// <para>
    /// <b>当前轮次纯按位置划分</b>（见 <see cref="FindCurrentTurnStart"/>）：客户端 payload 结构恒为
    /// <c>[历史前缀…, 当前轮次]</c>，最后一条 <see cref="ChatRole.User"/> 消息即本轮起点，其之前的前缀一律不落库。
    /// <b>不依赖 <see cref="ChatMessage.MessageId"/></b>——客户端重发的 assistant 消息带的是客户端自己的 id，
    /// 与库中服务端返回的 id（如 <c>chatcmpl-…</c>）永远对不上，按 id 判「已拥有」在真实链路必然落空。
    /// 落库 = 当前轮次消息 + <b>全部</b> <c>ResponseMessages</c>；<c>round_id</c> 递增、前导 system 归 round 1、
    /// 事务原子、乐观重试等由 <see cref="InsertMessagesWithRetryAsync"/> 保持。
    /// </para>
    /// <para>
    /// 落库成功后把新增消息追加进会话内缓存（<see cref="State.Messages"/>），按
    /// <see cref="SqlChatHistoryOptions.MaxRoundsToLoad"/> 截到最近 N 轮，使下次 <see cref="ProvideChatHistoryAsync"/> 命中缓存。
    /// </para>
    /// </remarks>
    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var conversationId = GetConversationId(context.Session);
        var state = _sessionState.GetOrInitializeState(context.Session);

        var requestMessages = context.RequestMessages.ToList();
        var currentTurn = requestMessages.Skip(FindCurrentTurnStart(requestMessages));
        var newMessages = currentTurn.Concat(context.ResponseMessages ?? []).ToList();
        if (newMessages.Count == 0)
            return;

        var stored = await InsertMessagesWithRetryAsync(conversationId, newMessages, cancellationToken).ConfigureAwait(false);

        // 同步会话缓存：追加本轮新增 + 截到最近 N 轮（与 Provide 的活跃轮窗口口径一致）。
        state.Messages.AddRange(stored);
        TrimToRounds(state.Messages, _options.MaxRoundsToLoad < 1 ? 1 : _options.MaxRoundsToLoad);
        _sessionState.SaveState(context.Session, state);
    }

    /// <summary>
    /// 按<b>位置</b>找出「当前轮次」的起点下标：客户端 payload 结构恒为 <c>[历史前缀…, 当前轮次]</c>，
    /// 最后一条 <see cref="ChatRole.User"/> 消息即本轮起点（无 User 消息时整包为本轮）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 起点之前若存在<b>对话类消息</b>（<see cref="ChatRole.User"/> / <see cref="ChatRole.Assistant"/> / <see cref="ChatRole.Tool"/>）
    /// → 视为客户端已自带的既有历史，起点 = 该最后一条 User；否则（前面只有 <see cref="ChatRole.System"/> 等提示消息，
    /// 不是历史）起点 = 0，使前导 system 仍属本轮（design §2.3：前导非 User 归 round 1）。
    /// </para>
    /// <para>
    /// 返回 <c>&gt; 0</c> 即表示「客户端已带历史」——<see cref="ProvideChatHistoryAsync"/> 据此返回空、不再补历史。
    /// </para>
    /// </remarks>
    private static int FindCurrentTurnStart(IReadOnlyList<ChatMessage> requestMessages)
    {
        var lastUserIndex = requestMessages
            .Select((message, index) => (message, index))
            .Where(x => x.message.Role == ChatRole.User)
            .Select(x => x.index)
            .DefaultIfEmpty(-1)
            .Max();

        if (lastUserIndex <= 0)
            return 0;

        // 最后一条 user 之前存在对话类消息 → 客户端带了历史，当前轮次从该 user 起；否则整包为本轮。
        return requestMessages.Take(lastUserIndex).Any(message => IsConversationalRole(message.Role))
            ? lastUserIndex
            : 0;
    }

    /// <summary>是否对话类角色（user / assistant / tool）——system / developer 属提示消息，不算历史。</summary>
    private static bool IsConversationalRole(ChatRole role)
        => role == ChatRole.User || role == ChatRole.Assistant || role == ChatRole.Tool;

    /// <summary>缓存覆盖的轮 id（按消息顺序首次出现，轮 id 升序）。</summary>
    private static List<int> CacheRounds(IReadOnlyList<CachedMessage> messages)
        => messages.Select(message => message.RoundId).Distinct().ToList();

    /// <summary>把缓存截到最近 <paramref name="maxRounds"/> 个轮（整轮保留，轮 id 升序）。</summary>
    private static void TrimToRounds(List<CachedMessage> messages, int maxRounds)
    {
        var rounds = CacheRounds(messages);
        if (rounds.Count <= maxRounds)
            return;

        var keepFrom = rounds[^maxRounds];
        messages.RemoveAll(m => m.RoundId < keepFrom);
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

    /// <inheritdoc />
    /// <remarks>
    /// 供 <see cref="SessionCleanupService"/> 周期调用的维护入口：用配置的
    /// <see cref="SqlChatHistoryOptions.TtlDays"/> / <see cref="SqlChatHistoryOptions.CleanupBatchSize"/> 委托
    /// <see cref="CleanupExpiredRoundsAsync"/>。显式接口实现（不经 provider 的公开 API 面暴露）。
    /// 清理节奏由共享的 <see cref="SessionCleanupService"/> 后台循环统一驱动
    /// （<see cref="AguiSessionOptions.EffectiveCleanupInterval"/>，默认 12h）。
    /// </remarks>
    Task<int> IChatHistoryCleaner.CleanupExpiredRoundsAsync(CancellationToken cancellationToken)
        => CleanupExpiredRoundsAsync(_options.TtlDays, _options.CleanupBatchSize, cancellationToken);

    /// <summary>
    /// 默认状态初始化器：优先采用宿主 <see cref="SqliteAgentSessionStore"/> 写入会话 StateBag 的 AG-UI <c>ThreadId</c>
    /// （<see cref="AguiSessionStateKeys.ConversationId"/>）作为会话标识（design §2.2：<c>conversation_id</c> = ThreadId）；
    /// 未接线（纯单测直构 provider / 会话 StateBag 无该键）时回退生成 GUID，保证不崩。
    /// </summary>
    private static State DefaultStateInitializer(AgentSession? session)
    {
        if (session?.StateBag.TryGetValue<string>(AguiSessionStateKeys.ConversationId, out var threadId) is true
            && !string.IsNullOrWhiteSpace(threadId))
        {
            return new State(threadId);
        }

        return new State(Guid.NewGuid().ToString("N"));
    }

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
    /// 返回实际落库的（轮 id, 消息）列表，供 <see cref="StoreChatHistoryAsync"/> 同步会话缓存。
    /// </remarks>
    private async Task<List<CachedMessage>> InsertMessagesWithRetryAsync(
        string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var (maxSequence, currentRound) = await GetMaxSequenceAndRoundAsync(conversationId, cancellationToken).ConfigureAwait(false);
            var sequence = maxSequence;
            var round = currentRound;
            var createdAt = DateTimeOffset.Now.ToString("O");
            var stored = new List<CachedMessage>(messages.Count);

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

                    stored.Add(new CachedMessage(effectiveRound, message));
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return stored;
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
    /// 会话状态：承载会话标识（<see cref="ConversationId"/>）与会话内历史缓存（<see cref="Messages"/>）。
    /// 经 <see cref="ProviderSessionState{TState}"/> 存 <see cref="AgentSession.StateBag"/>，随会话快照持久化
    /// （镜像官方 <c>ValkeyChatHistoryProvider.State</c> / 老 <c>SqliteChatHistoryProvider.State</c> 的会话内缓存模式）。
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

        /// <summary>
        /// 会话内缓存：provider 已拥有的历史消息（含所属轮 id）。用途：(1) <see cref="ProvideChatHistoryAsync"/>
        /// 免重复查库；(2) <see cref="StoreChatHistoryAsync"/> 判定「客户端重发的是既有历史」以免重复落库。
        /// 规模受 <see cref="SqlChatHistoryOptions.MaxRoundsToLoad"/> 约束（按轮截断）。
        /// </summary>
        [JsonPropertyName("messages")]
        public List<CachedMessage> Messages { get; set; } = [];
    }

    /// <summary>会话内缓存的历史消息：消息本体 + 所属轮 id（用于按 <see cref="SqlChatHistoryOptions.MaxRoundsToLoad"/> 整轮截断与活跃轮比对）。</summary>
    public sealed class CachedMessage
    {
        /// <summary>初始化缓存项。</summary>
        /// <param name="roundId">该消息所属轮次 id。</param>
        /// <param name="message">消息本体。</param>
        [JsonConstructor]
        public CachedMessage(int roundId, ChatMessage message)
        {
            RoundId = roundId;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        /// <summary>获取该消息所属轮次 id。</summary>
        [JsonPropertyName("roundId")]
        public int RoundId { get; }

        /// <summary>获取消息本体。</summary>
        [JsonPropertyName("message")]
        public ChatMessage Message { get; }
    }
}
