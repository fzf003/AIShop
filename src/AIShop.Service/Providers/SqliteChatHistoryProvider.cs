using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Serilog;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Service.Providers;

/// <summary>
/// 基于 EF Core + SQLite 的 ChatHistoryProvider。
///
/// 设计原则：
/// - **增量追加 + 后台裁剪**：Store 时先追加本轮消息，然后裁剪该 session 超出上限的旧消息
///   → 既保障跨 FICC 轮次的消息完整性（不丢 tool_calls 配对），又控制存储不无限膨胀
/// - **行列化存储**：Store 时解包 MEAI ChatMessage.Contents，按角色分列写入 chat_messages 表
///   → 不再依赖 AIContentListConverter 的 ContentsJson 序列化
/// - **reasoning 独立列**：TextReasoningContent 仅存于 reasoning 列，Provide 时不重建
/// - **会话内内存缓存**：<see cref="State.Messages"/> 作为会话内缓存——首次 Provide 查库回填，
///   会话内命中缓存免重复查库；Store 追加新消息，触发压缩时重建缓存保证与数据库一致
///
/// 框架集成层对齐官方 Provider 最佳实践：
/// - State 承载会话身份（<see cref="State.ConversationId"/>）
/// - 压缩参数 / StateKey / 过滤器通过 <see cref="SqliteChatHistoryProviderOptions"/> 配置
/// - 结构化日志使用 Serilog（与项目一致）
/// </summary>
public sealed class SqliteChatHistoryProvider : ChatHistoryProvider
{
    private readonly ProviderSessionState<State> _sessionState;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly int _maxCompletedRounds;
    private readonly int _maxIncompleteRounds;
    private readonly int _maxMessagesHardLimit;
    private IReadOnlyList<string>? _stateKeys;

    private static readonly Serilog.ILogger Logger = Log.ForContext<SqliteChatHistoryProvider>();

    /// <summary>压缩保留的最大完整轮数默认值（K=12，偏保守，容量约等于旧 50 条硬切）。</summary>
    private const int DefaultMaxCompletedRounds = 12;

    /// <summary>未完成轮上限默认值（5 个，T7 安全阀 1，spec #11）。</summary>
    private const int DefaultMaxIncompleteRounds = 5;

    /// <summary>条数硬上限默认值（4K 条，T7 安全阀 2，spec #10）。</summary>
    private const int DefaultMaxMessagesHardLimit = 4096;

    /// <summary>
    /// tool_calls JSON 的序列化选项（小驼峰，无缩进）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// 初始化 <see cref="SqliteChatHistoryProvider"/>。
    /// </summary>
    /// <param name="dbFactory">EF Core DbContext 工厂。</param>
    /// <param name="stateInitializer">会话状态初始化器；未提供时默认从 Session.StateBag 读取 "SessionId" 构造 State。</param>
    /// <param name="options">可选配置（压缩参数、StateKey、过滤器）。</param>
    public SqliteChatHistoryProvider(
        IDbContextFactory<AppDbContext> dbFactory,
        Func<AgentSession?, State>? stateInitializer = null,
        SqliteChatHistoryProviderOptions? options = null)
        : base(
            options?.ProvideOutputMessageFilter,
            options?.StoreInputRequestMessageFilter,
            options?.StoreInputResponseMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);

        this._sessionState = new ProviderSessionState<State>(
            stateInitializer ?? DefaultStateInitializer,
            options?.StateKey ?? this.GetType().Name,
            options?.JsonSerializerOptions);

        this._dbFactory = dbFactory;
        this._maxCompletedRounds = options?.MaxCompletedRounds ?? DefaultMaxCompletedRounds;
        this._maxIncompleteRounds = options?.MaxIncompleteRounds ?? DefaultMaxIncompleteRounds;
        this._maxMessagesHardLimit = options?.MaxMessagesHardLimit ?? DefaultMaxMessagesHardLimit;
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

        // 缓存未命中：查库重建并回填到 State.Messages（会话内内存缓存）
        await using var db = await this._dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await LoadAndRebuildMessagesAsync(db, sessionId, cancellationToken);
        state.Messages = messages.ToList();
        this._sessionState.SaveState(context.Session, state);

        Logger.Debug("ProvideChatHistory: 缓存回填 Session={SessionId} Count={Count}",
            sessionId, state.Messages.Count);
        return state.Messages;
    }

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

        await using var db = await this._dbFactory.CreateDbContextAsync(cancellationToken);

        // 读取本轮 run_id：同一 StateBag RunId 为所有 FICC 迭代打同一轮次标记；
        // StateBag 无 RunId 或值非法时兜底生成独立 run_id，该批自成独立轮次，不抛异常（退化现状）
        var runId = context.Session!.StateBag.TryGetValue<string>("RunId", out var runIdText, null)
            && Guid.TryParse(runIdText, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        // T5：判定批内最后一条是否为「纯文本 assistant」（无 FunctionCallContent / tool_calls）——
        // 这是 FICC 迭代结束信号，若是则将该行 IsFinal=true 落库，作为本轮轮次终点标记；
        // 末条非纯文本（assistant(FCC) 或 tool 行）则本批不落 is_final，该轮视为未完成
        // （正常场景该批消息即本轮全部响应，最后一条为最终回复，覆盖绝大多数完成场景）
        var lastMessage = allMessages[^1];
        var isFinalLastMessage = lastMessage.Role == ChatRole.Assistant
            && !lastMessage.Contents.OfType<FunctionCallContent>().Any();

        // 步骤 1：追加本轮增量消息
        // 注意：绝不先删再插。FICC 在第 2 轮只传了 [ToolMessage] 进来，
        // 如果先删历史再插，第 1 轮的 UserMessage + Assistant{tool_calls} 会丢失，
        // 下次 Provide 就凑不出完整的消息配对，导致 400。
        for (var i = 0; i < allMessages.Count; i++)
        {
            var msg = allMessages[i];
            var role = msg.Role.ToString() ?? "user";

            // 提取纯文本内容
            var textContents = msg.Contents.OfType<TextContent>()
                .Select(t => t.Text);
            var rawText = string.Join(Environment.NewLine, textContents);

            string? toolCalls = null;
            string? toolCallId = null;
            string? reasoning = null;

            // 提取 TextReasoningContent
            if (msg.Role == ChatRole.Assistant)
            {
                reasoning = string.Join(Environment.NewLine,
                    msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (string.IsNullOrEmpty(reasoning))
                    reasoning = null;
            }

            // 清理 <think> 标签（部分模型在 TextContent 中返回思维链）
            // <think> 本质是思维链，应存入 reasoning 列而不是 content 列
            if (!string.IsNullOrEmpty(rawText))
            {
                var thinkStart = rawText.IndexOf("<think>");
                var thinkEnd = rawText.IndexOf("</think>");
                if (thinkStart >= 0 && thinkEnd > thinkStart)
                {
                    var thinkContent = rawText[(thinkStart + 7)..thinkEnd];
                    if (string.IsNullOrEmpty(reasoning))
                        reasoning = thinkContent.Trim();
                    rawText = (rawText[..thinkStart] + rawText[(thinkEnd + 8)..]).Trim();
                }
                else if (thinkStart >= 0)
                {
                    // 只有 <think> 没有 </think>（截断），内容移到 reasoning
                    var thinkContent = rawText[(thinkStart + 7)..];
                    if (string.IsNullOrEmpty(reasoning))
                        reasoning = thinkContent.Trim();
                    rawText = "";
                }
            }

            // 对 assistant 回复：如果 TextContent 是 {"Reply":"...",...}，提取 Reply 字段
            var textContent = msg.Role == ChatRole.Assistant
                ? StripAgentReplyJson(rawText)
                : rawText;

            if (msg.Role == ChatRole.Assistant)
            {
                // 提取 FunctionCallContent 序列化为 tool_calls JSON
                var fccList = msg.Contents.OfType<FunctionCallContent>().ToList();
                if (fccList.Count > 0)
                {
                    var serializedCalls = fccList.Select(fcc => new
                    {
                        id = fcc.CallId,
                        type = "function",
                        function = new
                        {
                            name = fcc.Name,
                            arguments = fcc.Arguments is not null
                                ? JsonSerializer.Serialize(fcc.Arguments, JsonOptions)
                                : null
                        }
                    }).ToList();

                    toolCalls = JsonSerializer.Serialize(serializedCalls, JsonOptions);
                }

                // 提取 TextReasoningContent（仅当尚未通过第一次提取或 <think> 设置时才赋值）
                var secondReasoning = string.Join(Environment.NewLine,
                    msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrEmpty(secondReasoning))
                    reasoning = secondReasoning;
            }
            else if (msg.Role == ChatRole.Tool)
            {
                // 按 FRC 数量分流：单 FRC 保持旧格式（ToolCallId + Content），多 FRC 序列化为 JSON 数组存 ToolCalls 列
                var frcs = msg.Contents.OfType<FunctionResultContent>().ToList();
                if (frcs.Count == 1)
                {
                    // 单 FRC：保持旧格式（ToolCallId + Content），兼容存量数据
                    var frc = frcs[0];
                    toolCallId = frc.CallId;
                    // 如果 TextContent 为空，从 FRC.Result 提取文本
                    if (string.IsNullOrEmpty(textContent) && frc.Result is not null)
                        textContent = frc.Result?.ToString() ?? "";
                }
                else if (frcs.Count > 1)
                {
                    // 多 FRC：序列化成 JSON 数组存入 ToolCalls 列（[{id, result}, ...]），ToolCallId 置空
                    var results = frcs
                        .Select(frc => new { id = frc.CallId, result = frc.Result?.ToString() ?? "" })
                        .ToList();
                    toolCalls = JsonSerializer.Serialize(results, JsonOptions);
                    toolCallId = null;
                }
            }

            db.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = sessionId,
                RunId = runId,
                Role = role,
                Content = textContent,
                ToolCalls = toolCalls,
                ToolCallId = toolCallId,
                Reasoning = reasoning,
                // 批内最后一条为纯文本 assistant → 本轮轮次终点标记；其余行恒为 false
                IsFinal = isFinalLastMessage && i == allMessages.Count - 1,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // 先提交新消息，确保它们有 Id 且对后续查询可见
        await db.SaveChangesAsync(cancellationToken);

        // 步骤 2：裁剪旧消息，控制存储大小 —— 按 run_id 整轮整切（不拆轮）。
        // 设计（design.md §6 / spec #8「压缩按 run_id 整轮整切不拆轮」+ #10/#11/#12 安全阀）：
        //   1. 未压缩消息按 run_id 分组（run_id=NULL 的历史行每行自成一组，走旧行为，视为可压缩完整轮）
        //   2. 从最旧完整轮开始，整轮整轮标记 is_compacted=true，直到剩余完整轮数 ≤ K
        //   3. 未完成轮（组内无 is_final=true 行）整组保留，即使它很旧；但受「未完成轮上限」兜底，
        //      超限强制压最旧未完成轮并记 Warning（T7 安全阀 1，spec #11）
        //   4. 保留区未压缩条数超「硬上限」时强制压最旧整轮并记 Warning（T7 安全阀 2，spec #10，
        //      防单轮极大 / 异常堆积导致存储膨胀）
        //   5. 未完成轮计数口径（spec #12）：只统计 run_id 非空且组内行数 ≥2 的组；
        //      run_id=NULL 历史行不计入未完成轮，防止历史库瞬间超上限触发误压缩
        // 配对保护天然成立：整轮一起压，FCC↔tool 结构性不分离，因此删除原 compressIds 的 ±1 相邻推断逻辑
        var rows = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .Select(m => new { m.Id, m.RunId, m.IsFinal })
            .ToListAsync(cancellationToken);

        // 按 run_id 分组识别轮次；run_id=NULL 的历史行每行自成一组（用唯一 Id 作分组键，走旧行为）
        var rounds = rows
            .GroupBy(r => new { RunId = r.RunId, NullRowKey = r.RunId is null ? r.Id : (long?)null })
            .Select(g => new
            {
                RunId = g.Key.RunId,
                // 完整轮判定：组内有 is_final=true 行；NULL 历史行（RunId 为空）走旧行为，视为可压缩
                IsComplete = g.Key.RunId is null || g.Any(r => r.IsFinal),
                MaxId = g.Max(r => r.Id),
                Ids = g.Select(r => r.Id).ToList(),
            })
            .ToList();

        var compressIds = new HashSet<long>();
        var compressedRoundCount = 0;

        // 阶段 1（T6 既有）：未完成轮整组保留、不参与本阶段；只对完整轮按新旧排序（组内最大 id，最旧在前），
        // 从最旧完整轮开始整轮压缩，直到剩余完整轮数 ≤ K
        var completeRounds = rounds
            .Where(r => r.IsComplete)
            .OrderBy(r => r.MaxId)
            .ToList();
        var completeToCompress = completeRounds.Count - this._maxCompletedRounds;
        if (completeToCompress > 0)
        {
            compressIds.UnionWith(completeRounds.Take(completeToCompress).SelectMany(r => r.Ids));
            compressedRoundCount += completeToCompress;
        }

        // 阶段 2（T7 安全阀 1）：未完成轮上限 —— 超限时强制压最旧未完成轮并记 Warning。
        // 未完成轮计数口径（spec #12）：只统计 run_id 非空且组内行数 ≥2 的组；
        // run_id=NULL 历史行不计入未完成轮（否则历史库瞬间超上限触发误压缩），它们走旧行为由阶段 1 处理
        var incompleteRounds = rounds
            .Where(r => !r.IsComplete && r.RunId is not null && r.Ids.Count >= 2)
            .OrderBy(r => r.MaxId)
            .ToList();
        var incompleteToCompress = incompleteRounds.Count - this._maxIncompleteRounds;
        if (incompleteToCompress > 0)
        {
            Logger.Warning(
                "压缩安全阀：未完成轮超上限 Session={SessionId} IncompleteRounds={Count} Max={Max}，强制压最旧 {Compress} 个未完成轮",
                sessionId, incompleteRounds.Count, this._maxIncompleteRounds, incompleteToCompress);
            compressIds.UnionWith(incompleteRounds.Take(incompleteToCompress).SelectMany(r => r.Ids));
            compressedRoundCount += incompleteToCompress;
        }

        // 阶段 3（T7 安全阀 2）：条数硬上限 —— 单轮极大或异常堆积导致保留区未压缩条数超硬上限时，
        // 强制压最旧整轮（组内最大 id 最旧在前）直到 ≤ 硬上限并记 Warning。存储安全阀优先于「未完成轮整组保留」，
        // 未完成轮在阶段 2 已压到上限内，此处作为最后兜底亦可被压
        var remainingCount = rows.Count - compressIds.Count;
        if (remainingCount > this._maxMessagesHardLimit)
        {
            Logger.Warning(
                "压缩安全阀：保留区条数超硬上限 Session={SessionId} Count={Count} HardLimit={HardLimit}，强制压最旧整轮",
                sessionId, remainingCount, this._maxMessagesHardLimit);

            // 按最旧顺序累计行数，取累计超过溢出量所需的最少整轮前缀（整轮整切，末轮可能多压到硬上限以内）
            var overflow = remainingCount - this._maxMessagesHardLimit;
            var candidates = rounds
                .Where(r => !r.Ids.Any(compressIds.Contains))
                .OrderBy(r => r.MaxId)
                .ToList();
            var takeCount = 0;
            var accumulatedRows = 0L;
            while (takeCount < candidates.Count && accumulatedRows < overflow)
            {
                accumulatedRows += candidates[takeCount].Ids.Count;
                takeCount++;
            }

            compressIds.UnionWith(candidates.Take(takeCount).SelectMany(r => r.Ids));
            compressedRoundCount += takeCount;
        }

        // 步骤 3：同步会话内内存缓存（无压缩时追加本轮，随后 SaveState）
        state.Messages.AddRange(allMessages);
        this._sessionState.SaveState(context.Session, state);

        if (compressIds.Count == 0)
        {
            Logger.Debug("裁剪旧消息 Session={SessionId} 无需裁剪 Rounds={RoundCount}",
                sessionId, completeRounds.Count);
            sw.Stop();
            Logger.Debug("StoreChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
                sessionId, allMessages.Count, sw.ElapsedMilliseconds);
            return;
        }

        // 执行压缩：整轮标记 is_compacted=true（非物理删除，保留原始数据——历史可追溯、可查询）
        await db.ChatMessageRecords
            .Where(m => compressIds.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCompacted, true), cancellationToken);

        Logger.Information("裁剪旧消息 Session={SessionId} CompressRounds={RoundCount} DeleteCount={DeleteCount}",
            sessionId, compressedRoundCount, compressIds.Count);

        // 压缩后重建缓存：数据库已是压缩后状态，重建即与库一致（含本轮新消息，无需 AddRange）
        state.Messages = (await LoadAndRebuildMessagesAsync(db, sessionId, cancellationToken)).ToList();
        this._sessionState.SaveState(context.Session, state);

        sw.Stop();
        Logger.Debug("StoreChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, allMessages.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Run 后兜底补标轮次终点（spec「Run 后兜底补标 is_final」#5 +「轮次完成判断基于 is_final 查询」#7）。
    ///
    /// 语义以「轮次终点标记」为准（评审 Y1）：当本轮尚无 <c>is_final=true</c> 行时（如仅调工具未输出文本、
    /// FICC 迭代结束信号漏判），把该轮最后一条消息（max id）补标为轮次终点——补标可能落在
    /// tool/FCC 行上，该行是轮次终点但不必是最终回复。已存在终点行则不动；runId 无对应行则静默返回。
    ///
    /// 职责边界（评审 Y3）：补标写入口收敛在本方法，不新增其他写入口；Agent 不直接操作 DbContext。
    /// </summary>
    /// <param name="runId">轮次标识（RunChatAsync 每轮开始生成的 Guid）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task MarkRoundFinalAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await this._dbFactory.CreateDbContextAsync(cancellationToken);

        // 该轮已有 is_final=true 终点行 → 正常完成，不重复补标
        if (await db.ChatMessageRecords.AnyAsync(m => m.RunId == runId && m.IsFinal, cancellationToken))
            return;

        // 该轮最后一条（max id）即为轮次终点；runId 无对应行时 FirstOrDefaultAsync 返回 null，静默返回不抛异常
        var last = await db.ChatMessageRecords
            .Where(m => m.RunId == runId)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
            return;

        last.IsFinal = true;
        await db.SaveChangesAsync(cancellationToken);

        Logger.Information("MarkRoundFinal: Run={RunId} RowId={RowId} 末条补标 is_final（轮次终点标记）",
            runId, last.Id);
    }

    /// <summary>
    /// 从数据库加载未压缩消息并重建为 <see cref="AgentChatMessage"/> 列表。
    /// 供 Provide 回填缓存及 Store 压缩后重建缓存共用。
    /// </summary>
    private static async Task<IReadOnlyList<AgentChatMessage>> LoadAndRebuildMessagesAsync(
        AppDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // 全量加载 is_compacted=0 的消息，按 id 升序
        var rows = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

        sw.Stop();
        Logger.Information("ProvideChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, rows.Count, sw.ElapsedMilliseconds);

        // T8：孤儿 tool 配对检查（spec「孤儿 tool 按同 run_id 组内配对过滤」）——
        // 预计算「含 assistant-FCC 行的 run_id 集合」与「assistant-FCC 行 id 集合」。
        // assistant-FCC 行 = Role=="assistant" 且 ToolCalls 非空（该轮发起工具调用的信号）。
        // 有 run_id 的行走组内配对；run_id=NULL 的历史行无组可查，走相邻 id 退化路径。
        var assistantFccRunIds = rows
            .Where(r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls) && r.RunId is not null)
            .Select(r => r.RunId!.Value)
            .ToHashSet();
        var assistantFccRowIds = rows
            .Where(r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls))
            .Select(r => r.Id)
            .ToHashSet();

        var result = new List<AgentChatMessage>(rows.Count);

        foreach (var row in rows)
        {
            var role = row.Role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                "system" => ChatRole.System,
                _ => ChatRole.User
            };

            var contents = new List<AIContent>();

            if (role == ChatRole.Tool)
            {
                // tool 消息：优先读 ToolCalls 列（多 FRC JSON [{id, result}, ...]）
                if (!string.IsNullOrEmpty(row.ToolCalls))
                {
                    try
                    {
                        var results = JsonSerializer.Deserialize<List<ToolResultJson>>(row.ToolCalls, JsonOptions);
                        if (results is not null)
                        {
                            foreach (var r in results)
                                contents.Add(new FunctionResultContent(r.Id, r.Result));
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning(ex, "tool 结果反序列化失败 Session={SessionId} RowId={RowId}",
                            sessionId, row.Id);
                    }
                }
                else if (!string.IsNullOrEmpty(row.Content))
                {
                    // 单 FRC 旧格式（ToolCallId + Content），兼容存量数据
                    contents.Add(new FunctionResultContent(row.ToolCallId ?? "", row.Content));
                }
            }
            else
            {
                // user/assistant: 文本内容
                if (!string.IsNullOrEmpty(row.Content))
                {
                    contents.Add(new TextContent(row.Content));
                }

                // assistant: 反序列化 tool_calls JSON 重建 FunctionCallContent
                if (role == ChatRole.Assistant && !string.IsNullOrEmpty(row.ToolCalls))
                {
                    try
                    {
                        var toolCalls = JsonSerializer.Deserialize<List<ToolCallJson>>(row.ToolCalls, JsonOptions);
                        if (toolCalls is not null)
                        {
                            foreach (var tc in toolCalls)
                            {
                                Dictionary<string, object?>? args = null;
                                if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                                {
                                    try
                                    {
                                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                            tc.Function.Arguments, JsonOptions);
                                    }
                                    catch (JsonException)
                                    {
                                        // arguments 不是合法 JSON 对象时降级为空
                                    }
                                }
                                contents.Add(new FunctionCallContent(tc.Id, tc.Function?.Name ?? "", args));
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning(ex, "tool_calls 反序列化失败 Session={SessionId} RowId={RowId}",
                            sessionId, row.Id);
                    }
                }

                // 重建 TextReasoningContent（供 DeepSeekChatClient 映射为 reasoning_content）
                if (role == ChatRole.Assistant && !string.IsNullOrEmpty(row.Reasoning))
                {
                    contents.Add(new TextReasoningContent(row.Reasoning));
                }
            }

            // 过滤完全空的 assistant 消息（无任何内容）
            // (NOSONAR: single-statement if with continue is intentional)
            if (role == ChatRole.Assistant && contents.Count == 0)
                continue;

            // 过滤无 FunctionResultContent 的孤儿 tool 消息（CallId 可能为空或不对齐）
            if (role == ChatRole.Tool && contents.Count == 0)
                continue;

            // T8：孤儿 tool 配对检查升级（spec「孤儿 tool 按同 run_id 组内配对过滤」）——
            // 有 FRC 内容的 tool 行还必须能在其轮次内找到对应的 assistant-FCC 行才进上下文：
            //   1. 有 run_id → 检查同 run_id 组内是否存在 assistant-FCC 行，不存在则过滤
            //      （比旧相邻 id 推断可靠，能吸收历史碎片，FCC 行丢失/错位时不留孤儿 tool）
            //   2. run_id=NULL 的历史 tool 行无组可查 → 退化为旧相邻 id 检查（id-1 为 assistant-FCC
            //      则保留，否则过滤，兼容存量数据）
            if (role == ChatRole.Tool)
            {
                var hasPairedFcc = row.RunId is { } rid
                    ? assistantFccRunIds.Contains(rid)
                    : assistantFccRowIds.Contains(row.Id - 1);
                if (!hasPairedFcc)
                    continue;
            }

            result.Add(new AgentChatMessage(role, contents));
        }

        Logger.Debug("ProvideChatHistory 返回: Count={Count} Roles=[{Roles}]",
            result.Count,
            string.Join(",", result.Select(m => $"{m.Role}({m.Contents.Count}个内容)")));

        return result;
    }

    /// <summary>
    /// 如果文本是 {"Reply":"...",...} 格式的 JSON，提取 Reply 字段的值。
    /// 用于防 AgentChatResult 的 JSON 序列化泄漏到对话历史。
    /// 非 JSON 或没有 Reply 字段则原样返回。
    /// </summary>
    private static string StripAgentReplyJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        // 查找文本中第一个 { 开始的位置（兼容自然语言 + JSON 混合输出）
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0)
            return raw;

        var jsonEnd = raw.LastIndexOf('}');
        if (jsonEnd <= jsonStart)
            return raw;

        var jsonCandidate = raw[jsonStart..(jsonEnd + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            if ((doc.RootElement.TryGetProperty("Reply", out var reply) ||
                 doc.RootElement.TryGetProperty("reply", out reply)) && reply.ValueKind == JsonValueKind.String)
                return reply.GetString() ?? raw;
        }
        catch (JsonException)
        {
            // 不是合法 JSON，原样返回
        }

        return raw;
    }

    /// <summary>
    /// tool_calls JSON 反序列化用模型。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolCallJson
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = "function";
        public ToolCallFunctionJson? Function { get; set; } = null!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolCallFunctionJson
    {
        public string Name { get; set; } = string.Empty;
        public string? Arguments { get; set; }
    }

    /// <summary>
    /// tool 结果 JSON 反序列化用模型（ToolCalls 列，[{id, result}, ...]）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolResultJson
    {
        public string Id { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }

    /// <summary>
    /// 会话状态：承载会话身份（<see cref="ConversationId"/>）与会话内消息缓存（<see cref="Messages"/>）。
    /// 对应官方 Provider 的 State 模式（如 Valkey 的 <c>State(string conversationId)</c>，
    /// InMemory 的 <c>State.Messages</c>）。
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
