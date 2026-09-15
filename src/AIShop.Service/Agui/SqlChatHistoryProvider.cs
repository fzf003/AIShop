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
/// <b>标识取自会话 StateBag 的共享键</b>：<see cref="AguiSessionStateKeys.ConversationId"/> 由
/// <see cref="SqliteAgentSessionStore"/> 取 / 建会话时写入（当前实现 = 当前用户名，无用户名时回退会话标识串）；
/// <see cref="DefaultStateInitializer"/> 优先读该键作为 <c>conversation_id</c>，故审计 / 召回可按会话查询。
/// 未接线（纯单测直构 provider、StateBag 无该键）时回退生成 GUID，保证不崩；构造参数仍可注入确定性标识供测试。
/// </para>
/// <para>
/// <b>只存不取</b>：<see cref="StoreChatHistoryAsync"/> 只落库<b>当前轮次</b> + 全部响应消息；
/// <see cref="ProvideChatHistoryAsync"/> <b>恒空且不查库</b>——模型上下文由客户端每轮重发的全量历史承载，
/// 服务端不再注入任何历史（两边各自的 remarks 有详述）。
/// </para>
/// <para>
/// <b>当前轮次纯按位置判定（不依赖 message id）</b>：AG-UI 客户端每次把<b>整段前文</b>随请求发来，其 payload
/// 结构恒为 <c>[历史前缀…, 当前轮次]</c>，最后一条 user 消息即本轮起点（见 <see cref="FindCurrentTurnStart"/>）。
/// **不按 <see cref="ChatMessage.MessageId"/> 比对**：客户端重发的 assistant 消息带的是客户端自己的 id，
/// 与库中服务端返回 id（如 <c>chatcmpl-…</c>）永远对不上。
/// </para>
/// <para>
/// <b>轮次抽象（<c>round_id</c> + 整轮软删）为何保留</b>：与「只存不取」无关，它只服务两件事——
/// ①TTL 清理按<b>整轮</b>软删，不留半轮残留（不拆断 <see cref="FunctionCallContent"/>↔<see cref="FunctionResultContent"/> 配对）；
/// ②审计 / 召回可按轮查询落库历史。会话上下文压缩<b>不</b>依赖它。
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
    /// <param name="options">配置；null 时回退 <see cref="SqlChatHistoryOptions"/> 类默认（agui.chat.db / TTL 30 天 / 清理批 10 轮）。</param>
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
    {
#pragma warning disable S125 // Sections of code should not be commented out（保留调试用参考实现）
       /* var state = _sessionState.GetOrInitializeState(context.Session);

        Console.WriteLine(state);
        var requestMessages = context.RequestMessages.ToList();
        Console.WriteLine(requestMessages.Count);*/
#pragma warning restore S125
        return ValueTask.FromResult<IEnumerable<ChatMessage>>([]);
    }

    /// <summary>
    /// 覆盖 provider 通知入口，为<b>落库失败兜底</b>：<see cref="StoreChatHistoryAsync"/> 抛出的非取消异常
    /// 只记日志、不再上抛。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这层</b>：MAF 的默认实现（<c>ChatHistoryProvider.InvokedCoreAsync</c>）不做兜底，异常直接上抛；
    /// 而调用点位于 <c>ChatClientAgent</c> 流式路径的<b>所有 yield 之后</b>（镜像 <c>ChatClientAgent.cs:402</c>，
    /// yield 循环在 <c>:360-389</c>）。故落库一失败，客户端看到的是「答复已完整吐出、连接却异常断开」——
    /// 内容可见但 <c>RUN_FINISHED</c> 永不发出；且 AG-UI 端点的 <c>SaveSessionAsync</c>（镜像
    /// <c>AGUIEndpointRouteBuilderExtensions.cs:197</c>，同样无兜底）也随之不执行。
    /// </para>
    /// <para>
    /// <b>这是采纳框架开放的扩展点，不是绕过框架</b>：基类注释写明「for scenarios that require more control over
    /// error handling or message filtering, overriding this method allows you to directly control the messages
    /// that are stored for the invocation」——「落库失败要不要炸掉整个请求」本就由 provider 作者决定。
    /// </para>
    /// <para>
    /// <b>代价（有意接受）</b>：异常被吞 → 该轮消息静默不入库，仅留 Error 日志。取舍依据：聊天助手不该因一次
    /// 落库抖动让用户丢掉已生成的回复；且落库是审计 / 召回用途，缺一轮不影响模型上下文
    /// （<see cref="ProvideChatHistoryAsync"/> 恒空，上下文由客户端重发承载）。与
    /// <see cref="SessionCleanupService"/> 的「清理失败记日志、不中断」保持同一容错口径。
    /// </para>
    /// <para>
    /// <b>不吞 <see cref="OperationCanceledException"/></b>：请求取消属正常控制流，须继续上抛以正确终止管线。
    /// </para>
    /// </remarks>
    protected override async ValueTask InvokedCoreAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await base.InvokedCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "聊天历史落库失败，本轮不入库但请求继续（Agent {AgentName}）", context.Agent.Name);
        }
    }

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
    /// 只落库当前轮次；落库后<b>不写回 <see cref="AgentSession.StateBag"/> 任何消息副本</b>。
    /// 会话标识仍由 <see cref="GetConversationId"/> 经 <c>GetOrInitializeState</c> 在首次访问时写入（跨请求 / 重启稳定），
    /// 故会话状态中不再需要额外的消息副本。
    /// </para>
    /// </remarks>
    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var state = _sessionState.GetOrInitializeState(context.Session);

        var conversationId = GetConversationId(context.Session);
     

        var requestMessages = context.RequestMessages.ToList();
        var currentTurn = requestMessages.Skip(FindCurrentTurnStart(requestMessages));
        var newMessages = currentTurn.Concat(context.ResponseMessages ?? []).ToList();
        if (newMessages.Count == 0)
            return;

        var storemessages= await InsertMessagesWithRetryAsync(conversationId, newMessages, cancellationToken).ConfigureAwait(false);

        state.Messages.AddRange(storemessages);
        var maxRounds = _options.MaxRoundsToLoad < 1 ? 1 : _options.MaxRoundsToLoad;
        TrimToRounds(state.Messages, maxRounds);
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
    /// 返回 <c>&gt; 0</c> 即表示「客户端已自带历史」，起点之前的前缀一律不落库。仅供
    /// <see cref="StoreChatHistoryAsync"/> 做纯位置切分使用。
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
    /// 默认状态初始化器：优先采用宿主 <see cref="SqliteAgentSessionStore"/> 写入会话 StateBag 的共享键值
    /// （<see cref="AguiSessionStateKeys.ConversationId"/>，当前实现 = 当前用户名）作为 <c>conversation_id</c>；
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

    /// <summary>
    /// 读当前会话的 <c>MAX(sequence)</c> 与 <c>MAX(round_id)</c>，<b>含已软删行</b>——供续号 / 续轮使用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>此处必须统计全部行，不得加 <c>is_deleted = 0</c> 过滤</b>：<c>UNIQUE(conversation_id, sequence)</c>
    /// 不含该列，软删行<b>仍占用序号</b>。若只统计未删行，整会话被 TTL 软删后会从 <c>sequence = 1</c> 重新起号
    /// 去撞那些占位的软删行，且重试时重复读到同一个 <c>MAX</c> → 3 次全撞 → 永久写入失败
    /// （软删行不会被物理清理，故该状态不会自愈）。<c>round_id</c> 同理：跨软删继续递增，避免新轮与旧轮重号。
    /// </para>
    /// <para>
    /// 「软删行不可见」是<b>读取</b>侧的语义（<see cref="FindExpiredRoundsAsync"/> 等按 <c>is_deleted = 0</c> 过滤），
    /// 不能外推到<b>序号空间</b>——序号空间的占用者包含软删行。两处口径必须与 UNIQUE 约束保持一致。
    /// </para>
    /// </remarks>
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
            WHERE conversation_id = $conversationId
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
    /// 落库条数已无消费者（「只存不取」后不再需要回填），故本方法无返回值。
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

    /// <summary>缓存中涉及的轮 id 列表（升序、去重）。</summary>
    private static List<int> CacheRounds(IReadOnlyList<CachedMessage> messages)
        => messages.Select(m => m.RoundId).Distinct().ToList();

    private static void TrimToRounds(List<CachedMessage> messages, int maxRounds)
    {
        var rounds = CacheRounds(messages);
        if (rounds.Count <= maxRounds) return;
        var keepFrom = rounds[^maxRounds];
        messages.RemoveAll(m => m.RoundId < keepFrom);
    }

    /// <summary>
    /// 会话状态：<b>只承载会话标识</b>（<see cref="ConversationId"/>）。
    /// 经 <see cref="ProviderSessionState{TState}"/> 存 <see cref="AgentSession.StateBag"/>，随会话快照持久化
    /// （跨请求 / 重启稳定）。原会话内历史缓存（<c>messages</c> 键）随「只存不取」定型删除；旧快照里多余的
    /// <c>messages</c> 键在反序列化时被 <see cref="JsonSerializer"/> 忽略（未知属性），无需迁移。
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
        /// 最近 N 轮消息缓存（受 MaxRoundsToLoad 约束）。重启后可恢复，不需查库。
        /// </summary>
        [JsonPropertyName("messages")]
        public List<CachedMessage> Messages { get; set; } = [];
    }

    /// <summary>缓存项：消息本体 + 所属轮次 id。</summary>
    public sealed class CachedMessage
    {
        [JsonConstructor]
        public CachedMessage(int roundId, ChatMessage message)
        {
            RoundId = roundId;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        [JsonPropertyName("roundId")]
        public int RoundId { get; }

        [JsonPropertyName("message")]
        public ChatMessage Message { get; }
    }
}
