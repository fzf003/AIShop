using System.Diagnostics;
using System.Text.Json.Serialization;
using AIShop.Core.Interfaces;
using Microsoft.Agents.AI;
using Serilog;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Service.Providers;

/// <summary>
/// 聊天历史 Provider（瘦壳）：只做 MAF 框架适配 + 会话缓存 + 编排。
/// 持久化委托 <see cref="IChatHistoryStore"/>、压缩决策委托 <see cref="IChatCompactionPolicy"/>、
/// 消息转换委托 <see cref="ChatMessageMapper"/>——三职责已从本类拆出。
/// </summary>
public sealed class SqliteChatHistoryProvider : ChatHistoryProvider
{
    private readonly ProviderSessionState<State> _sessionState;
    private readonly IChatHistoryStore _store;
    private readonly IChatCompactionPolicy _compaction;
    private IReadOnlyList<string>? _stateKeys;

    private static readonly Serilog.ILogger Logger = Log.ForContext("SourceContext", nameof(SqliteChatHistoryProvider));

    /// <summary>
    /// 初始化聊天历史 Provider（瘦壳）。
    /// </summary>
    /// <param name="store">聊天历史存储（EF 读写）。</param>
    /// <param name="compaction">压缩策略（纯规则，压缩参数在策略构造时配置）。</param>
    /// <param name="stateInitializer">会话状态初始化器；未提供时默认从 Session.StateBag 读取 "SessionId" 构造 State。</param>
    /// <param name="options">可选配置（StateKey、过滤器）。</param>
    public SqliteChatHistoryProvider(
        IChatHistoryStore store,
        IChatCompactionPolicy compaction,
        Func<AgentSession?, State>? stateInitializer = null,
        SqliteChatHistoryProviderOptions? options = null)
        : base(
            options?.ProvideOutputMessageFilter,
            options?.StoreInputRequestMessageFilter,
            options?.StoreInputResponseMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(compaction);

        this._store = store;
        this._compaction = compaction;
        this._sessionState = new ProviderSessionState<State>(
            stateInitializer ?? DefaultStateInitializer,
            options?.StateKey ?? this.GetType().Name,
            options?.JsonSerializerOptions);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => this._stateKeys ??= [this._sessionState.StateKey];

    /// <summary>
    /// 默认状态初始化器：从 Session.StateBag 读取 "SessionId" 构造 State。
    /// 兼容旧调用方（未传 stateInitializer 的场景，如测试），语义与官方 Provider 的 stateInitializer 一致。
    /// </summary>
    private static State DefaultStateInitializer(AgentSession? session)
    {
        if (session?.StateBag.TryGetValue<string>("SessionId", out var sessionId, null) is true
            && !string.IsNullOrWhiteSpace(sessionId))
        {
            return new State(sessionId);
        }

        throw new InvalidOperationException("SessionId not found in session StateBag.");
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(context.Session);
        var sessionId = Guid.Parse(state.ConversationId);

        // 会话内内存缓存：非空直接返回，免查库
        if (state.Messages.Count > 0)
        {
            Logger.Debug("ProvideChatHistory: 命中会话内缓存 Session={SessionId} Count={Count}",
                sessionId, state.Messages.Count);
            return state.Messages;
        }

        // 缓存未命中：查库回填并缓存
        var stored = await this._store.LoadUncompactedAsync(sessionId, cancellationToken);
        state.Messages = ChatMessageMapper.ToAgentMessages(stored).ToList();
        this._sessionState.SaveState(context.Session, state);

        Logger.Debug("ProvideChatHistory: 缓存回填 Session={SessionId} Count={Count}",
            sessionId, state.Messages.Count);
        return state.Messages;
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var state = this._sessionState.GetOrInitializeState(context.Session);
        var sessionId = Guid.Parse(state.ConversationId);

        var allMessages = (context.RequestMessages ?? [])
            .Concat(context.ResponseMessages ?? [])
            .ToList();

        if (allMessages.Count == 0)
        {
            Logger.Debug("StoreChatHistory: 无消息可存 Session={SessionId}", sessionId);
            return;
        }

        // 读取本轮 run_id：同一 StateBag RunId 为所有 FICC 迭代打同一轮次标记；
        // StateBag 无 RunId 或值非法时兜底生成独立 run_id，该批自成独立轮次，不抛异常（退化现状）
        var runId = context.Session!.StateBag.TryGetValue<string>("RunId", out var runIdText, null)
            && Guid.TryParse(runIdText, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        // 1. 写转换 + 追加提交（转换含 <think> 剥离、AgentReplyJson 剥离、tool_calls 序列化、IsFinal 标记）
        var newStored = ChatMessageMapper.ToStoredMessages(sessionId, runId, allMessages);
        await this._store.AppendAsync(sessionId, newStored, cancellationToken);

        // 2. 基于含新消息的最新未压缩集合做压缩决策（策略纯规则，无 IO）
        var latest = await this._store.LoadUncompactedAsync(sessionId, cancellationToken);
        var toCompact = this._compaction.SelectCompaction(latest);

        // 3. 同步会话内缓存：先追加本轮消息（与旧实现一致）
        state.Messages.AddRange(allMessages);
        this._sessionState.SaveState(context.Session, state);

        if (toCompact.Count == 0)
        {
            Logger.Debug("裁剪旧消息 Session={SessionId} 无需裁剪", sessionId);
            sw.Stop();
            Logger.Debug("StoreChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
                sessionId, allMessages.Count, sw.ElapsedMilliseconds);
            return;
        }

        // 4. 标记压缩（非物理删除）+ 重建缓存（数据库已是压缩后状态，重建即与库一致）
        await this._store.MarkCompactedAsync(toCompact, cancellationToken);
        var after = await this._store.LoadUncompactedAsync(sessionId, cancellationToken);
        state.Messages = ChatMessageMapper.ToAgentMessages(after).ToList();
        this._sessionState.SaveState(context.Session, state);

        Logger.Information("裁剪旧消息 Session={SessionId} CompressRows={Count}",
            sessionId, toCompact.Count);
        sw.Stop();
        Logger.Debug("StoreChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, allMessages.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Run 后兜底补标轮次终点（spec「Run 后兜底补标 is_final」）——转发到存储。
    /// 补标写入口收敛在本方法（评审 Y3），Agent 不直接操作存储。
    /// </summary>
    public async Task MarkRoundFinalAsync(Guid runId, CancellationToken cancellationToken = default)
        => await this._store.MarkRoundFinalAsync(runId, cancellationToken);

    /// <summary>
    /// 会话状态：承载会话身份（<see cref="ConversationId"/>）与会话内消息缓存（<see cref="Messages"/>）。
    /// 对应官方 Provider 的 State 模式。
    /// </summary>
    public sealed class State
    {
        [JsonConstructor]
        public State(string conversationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            this.ConversationId = conversationId;
        }

        /// <summary>获取会话唯一标识（对应 chat_messages.SessionId）。</summary>
        [JsonPropertyName("conversationId")]
        public string ConversationId { get; }

        /// <summary>
        /// 会话内内存缓存：首次 Provide 查库回填，会话内命中缓存免查库；
        /// Store 追加新消息，压缩发生时重建以保证与数据库一致。
        /// </summary>
        [JsonPropertyName("messages")]
        public List<AgentChatMessage> Messages { get; set; } = [];
    }
}
