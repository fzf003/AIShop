#pragma warning disable MAAI001 // CompactionStrategy / CompactionProvider 为 MAF [Experimental]（上下文压缩 API）
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 会话快照收敛的「轮归一」纯逻辑（agui-session-prod S2，design §4.3）。
/// 在 <see cref="IReadOnlyList{T}"/>（<see cref="ChatMessage"/>）上做纯函数变换，不触库、可脱离 store 单测。
/// </summary>
/// <remarks>
/// <para>
/// <strong>design §4.1 核实结论（顶层记录，算法动机）</strong>：官方 <see cref="ContextWindowCompactionStrategy"/>
/// 是「工具配对原子」但<strong>不是「轮原子（turn-atomic）」</strong>——
/// <see cref="CompactionMessageIndex"/> 把一轮 <c>[User][Assistant(FCC)][Tool(FRC)][AssistantText]</c>
/// 切成 <see cref="CompactionGroupKind.User"/> / <see cref="CompactionGroupKind.ToolCall"/> /
/// <see cref="CompactionGroupKind.AssistantText"/> 三个<strong>独立</strong>原子组，
/// <see cref="TruncationCompactionStrategy"/> 逐个排除「最旧的、非 System 的组」且只保证最近
/// <c>MinimumPreservedGroups=2</c> 个组 → 会产生「有回复无提问」的孤儿回复、并可能把当前轮切开。
/// 因此<strong>必须外包轮边界保护</strong>：把官方策略的输出当作「保留哪些轮」的信号，最终快照按
/// <strong>完整原始轮</strong>拼装，从结构上杜绝残缺轮（spec R2 硬约束）。
/// </para>
/// <para>
/// 算法（design §4.3 步骤 1-6，§4.2a 轮内折叠本期不实现）：
/// 快速路径 → 轮分区（preamble + 以 <see cref="ChatRole.User"/> 为界）→ 官方候选信号
/// （<see cref="CompactionProvider.CompactAsync"/>，用 <see cref="ReferenceEqualityComparer"/> 判定 included）
/// → 轮归一（任一原始消息命中即整轮保留，候选全丢才整轮丢弃；强制保护最后 <see cref="ProtectedRounds"/> 轮）
/// → MaxRounds 硬上限（从最旧整轮裁剪，不碰受保护轮）→ 拼装 preamble + 保留轮（原始消息）保原序。
/// </para>
/// <para>
/// 不变量（可测）：对每个原始轮，快照中要么包含该轮全部消息、要么一条都不含；任一
/// <see cref="FunctionCallContent.CallId"/> 都能在同一快照内找到对应 <see cref="FunctionResultContent.CallId"/>。
/// </para>
/// </remarks>
public static class SnapshotCompactor
{
    /// <summary>
    /// 强制保护的最近轮数（design §4.3 步骤 4、spec R3「至少最后 2 轮，含刚结束的末轮」）。
    /// 无论官方候选如何，最后 <see cref="ProtectedRounds"/> 轮一律整轮保留。
    /// </summary>
    public const int ProtectedRounds = 2;

    /// <summary>
    /// 对会话历史做轮归一收敛（design §4.3）。纯逻辑，不触库。
    /// </summary>
    /// <param name="strategy">装配共享的压缩策略实例（生产 = DI 单例 <c>ContextWindowCompactionStrategy</c>），
    /// 仅用作「保留哪些轮」的候选信号来源。</param>
    /// <param name="history">原始消息历史（保序）。</param>
    /// <param name="maxRounds">保留轮数硬上限（<c>Agui:SessionMaxRounds</c>，默认 12）；小于
    /// <see cref="ProtectedRounds"/> 时按 <see cref="ProtectedRounds"/> 兜底（受保护轮不可被上限裁剪）。</param>
    /// <param name="logger">可选日志（透传给官方 <see cref="CompactionProvider.CompactAsync"/>）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>收敛后的消息列表（保原序）。快速路径返回<strong>原历史同一实例</strong>（消息引用逐一相等，短会话零回归）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="strategy"/> 或 <paramref name="history"/> 为 null。</exception>
    public static async Task<IReadOnlyList<ChatMessage>> CompactAsync(
        CompactionStrategy strategy,
        IReadOnlyList<ChatMessage> history,
        int maxRounds,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(history);

        // 上限不得低于受保护轮数：保护是硬约束，maxRounds 配置异常时以保护为准（spec R3）。
        int effectiveMaxRounds = Math.Max(maxRounds, ProtectedRounds);

        var (preamble, rounds) = PartitionRounds(history);

        // 快速路径（design §4.3 步骤 1，spec R8）：短会话原样返回，保证消息引用逐一相等。
        // 轮数 ≤ ProtectedRounds 即短会话；消息数 ≤ ProtectedRounds 时每轮至少一条消息 → 轮数必 ≤ ProtectedRounds，
        // 且轮数 ≤ ProtectedRounds ≤ effectiveMaxRounds，故两条件均不会掩盖任何裁剪/收敛。
        if (rounds.Count <= ProtectedRounds || history.Count <= ProtectedRounds)
            return history;

        // 官方候选信号（design §4.3 步骤 3）：官方策略在低 token 的短历史上触发器不生效，候选 = 原消息全量引用；
        // 长历史上可能折叠/排除，故仅取其「保留信号」，最终以原始消息重建完整轮。
        IEnumerable<ChatMessage> includedMessages = await CompactionProvider
            .CompactAsync(strategy, history, logger, cancellationToken)
            .ConfigureAwait(false);

        // GetIncludedMessages 返回原消息引用，故按引用相等建集合（design §4.3 步骤 3）。
        var included = new HashSet<ChatMessage>(includedMessages, ReferenceEqualityComparer.Instance);

        // 轮归一（design §4.3 步骤 4）：候选命中的整轮保留（用原始消息重建，恢复官方可能折叠的组）；
        // 候选全丢的整轮丢弃；最后 ProtectedRounds 轮强制保留。
        int firstProtectedIndex = rounds.Count - ProtectedRounds;
        var kept = new List<Round>(rounds.Count);
        for (int i = 0; i < rounds.Count; i++)
        {
            bool isProtected = i >= firstProtectedIndex;
            if (isProtected || rounds[i].Messages.Any(included.Contains))
                kept.Add(rounds[i]);
        }

        // 轮数硬上限（design §4.3 步骤 5）：从最旧保留轮整轮丢弃至 ≤ 上限。
        // 受保护轮恒在 kept 尾部且数量 ≤ effectiveMaxRounds，故从头部 RemoveRange 不会触及它们。
        if (kept.Count > effectiveMaxRounds)
            kept.RemoveRange(0, kept.Count - effectiveMaxRounds);

        // 拼装（design §4.3 步骤 6）：preamble + 各保留轮（原始消息），保原序。
        var result = new List<ChatMessage>(history.Count);
        result.AddRange(preamble);
        foreach (Round round in kept)
            result.AddRange(round.Messages);

        return result;
    }

    /// <summary>
    /// 轮分区（design §4.3 步骤 2，口径对齐 <c>CompactionMessageIndex</c> 的 turn 划分）：
    /// <paramref name="preamble"/> = 首个 <see cref="ChatRole.User"/> 之前的全部消息（System 及可能的 assistant/tool
    /// 前缀），<strong>始终保留</strong>；第 i 轮 = 一条 User 起、至（不含）下一条 User 前的全部消息。
    /// </summary>
    /// <param name="history">原始消息历史。</param>
    /// <returns>preamble 与按出现顺序排列的各轮（保留原始消息引用）。</returns>
    private static (List<ChatMessage> Preamble, List<Round> Rounds) PartitionRounds(IReadOnlyList<ChatMessage> history)
    {
        var preamble = new List<ChatMessage>();
        var rounds = new List<Round>();
        Round? current = null;

        foreach (ChatMessage message in history)
        {
            if (message.Role == ChatRole.User)
            {
                current = new Round();
                rounds.Add(current);
            }

            if (current is null)
                preamble.Add(message);
            else
                current.Messages.Add(message);
        }

        return (preamble, rounds);
    }

    /// <summary>一轮消息（一条 User 起，至下一条 User 前），内部仅按序累积原始消息引用。</summary>
    private sealed class Round
    {
        internal List<ChatMessage> Messages { get; } = [];
    }
}
