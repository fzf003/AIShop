#pragma warning disable MAAI001 // CompactionStrategy / CompactionMessageIndex 等为 MAF [Experimental]（上下文压缩 API）
using AIShop.AguiHost;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S2 <see cref="SnapshotCompactor"/> 轮归一测试（design §4.3）。覆盖 spec R2（整轮为最小保留单位、
/// 工具配对完整，硬约束）与 R3（保留轮数硬上限 + 最后 ≥2 轮含末轮强制保护），以及 R8 快速路径零回归。
/// </summary>
/// <remarks>
/// 策略一律复用 <see cref="AguiCompaction.CreateStrategy"/>（AguiHost 压缩阈值唯一来源，S1），
/// 不在测试内重复声明 128000/16384/0.5/0.8——否则阈值又回到两处声明（handoff-S1 交接约定）。
/// </remarks>
public sealed class SnapshotCompactorTests
{
    /// <summary>默认保留轮数上限（Agui:SessionMaxRounds 默认 12），仅用于表达测试意图。</summary>
    private const int DefaultMaxRounds = 12;

    [Fact]
    public async Task ShouldReturnSameInstanceAndReferences_WhenRoundsWithinProtectedRounds()
    {
        // 快速路径（R8、design §4.3 步骤 1）：短会话原样返回，消息引用逐一相等（零回归）。
        var strategy = AguiCompaction.CreateStrategy();
        var (history, _) = BuildPlainRounds(2); // 轮数 == ProtectedRounds(2)

        var result = await SnapshotCompactor.CompactAsync(strategy, history, DefaultMaxRounds);

        Assert.Same(history, result); // 同一实例（不是拷贝）
        Assert.Equal(history.Count, result.Count);
        for (int i = 0; i < history.Count; i++)
        {
            Assert.Same(history[i], result[i]); // 逐条引用相等
        }

        // 单轮亦为快速路径
        var (singleRoundHistory, _) = BuildPlainRounds(1);
        var singleResult = await SnapshotCompactor.CompactAsync(strategy, singleRoundHistory, DefaultMaxRounds);
        Assert.Same(singleRoundHistory, singleResult);
    }

    [Fact]
    public async Task ShouldKeepEachRoundWholeOrNotAtAll_WhenRoundsExceedMaxRounds()
    {
        // R2：轮数超上限确定性触发收敛后，无残缺轮——每个原轮要么全部消息在快照、要么一条不含。
        var strategy = AguiCompaction.CreateStrategy();
        var (history, rounds) = BuildPlainRounds(15);
        const int maxRounds = 5;

        var result = await SnapshotCompactor.CompactAsync(strategy, history, maxRounds);

        Assert.True(result.Count < history.Count, "长会话应被收敛");

        foreach (var (user, assistant) in rounds)
        {
            bool hasUser = ContainsRef(result, user);
            bool hasAssistant = ContainsRef(result, assistant);
            Assert.Equal(hasUser, hasAssistant); // 无「有提问无回复/有回复无提问」
        }

        // 官方策略在低 token 短历史上不触发 → 候选全保留 → 上限从最旧整轮裁剪：保留最后 5 轮。
        for (int i = 0; i < 10; i++)
        {
            Assert.False(ContainsRef(result, rounds[i].User), $"最旧轮 {i + 1} 应被整轮丢弃");
            Assert.False(ContainsRef(result, rounds[i].Assistant));
        }

        for (int i = 10; i < 15; i++)
        {
            Assert.True(ContainsRef(result, rounds[i].User), $"较新轮 {i + 1} 应整轮保留");
            Assert.True(ContainsRef(result, rounds[i].Assistant));
        }
    }

    [Fact]
    public async Task ShouldKeepToolCallAndResultPaired_WhenRoundsContainToolCalls()
    {
        // R2 专项：含工具轮收敛后，每个 FunctionCallContent.CallId 都能在同一快照内找到相同 CallId 的
        // FunctionResultContent，且整轮不被拆散（配对结构性不分离）。
        var strategy = AguiCompaction.CreateStrategy();
        var (history, rounds) = BuildToolRounds(5);
        const int maxRounds = 3;

        var result = await SnapshotCompactor.CompactAsync(strategy, history, maxRounds);

        Assert.True(result.Count < history.Count, "长工具链会话应被收敛");

        foreach (var (user, call, toolResult, assistant) in rounds)
        {
            bool hasAny = ContainsRef(result, user) || ContainsRef(result, call)
                || ContainsRef(result, toolResult) || ContainsRef(result, assistant);
            bool hasAll = ContainsRef(result, user) && ContainsRef(result, call)
                && ContainsRef(result, toolResult) && ContainsRef(result, assistant);
            Assert.True(!hasAny || hasAll, "工具轮出现残缺");
        }

        IReadOnlyList<string> callIds = FunctionCallIds(result);
        IReadOnlyList<string> resultIds = FunctionResultIds(result);

        Assert.NotEmpty(callIds);
        foreach (string callId in callIds)
        {
            Assert.Contains(callId, resultIds); // 每个 FCC 都有同 CallId 的 FRC
        }

        // 被丢弃的是最旧轮（call_1/call_2），保留轮的配对完整且保序
        Assert.DoesNotContain("call_1", callIds);
        Assert.DoesNotContain("call_2", callIds);
        Assert.Equal(new[] { "call_3", "call_4", "call_5" }, callIds);
    }

    [Fact]
    public async Task ShouldAlwaysKeepSystemPreambleAtFront_RegardlessOfCandidate()
    {
        // R2：preamble（首个 User 之前全部消息）始终保留、位置在最前——即便官方候选把所有组都排除。
        var preamble = new[]
        {
            new ChatMessage(ChatRole.System, "系统提示一"),
            new ChatMessage(ChatRole.System, "系统提示二"),
        };
        var (roundMessages, rounds) = BuildPlainRounds(5);
        var history = new List<ChatMessage>(preamble);
        history.AddRange(roundMessages);

        var strategy = new ExcludeAllGroupsStrategy();
        var result = await SnapshotCompactor.CompactAsync(strategy, history, DefaultMaxRounds);

        Assert.True(strategy.ExcludedGroupCount > 0, "自定义策略应确实排除了候选组");
        Assert.True(result.Count >= preamble.Length, "preamble 应在结果中");
        Assert.Same(preamble[0], result[0]);
        Assert.Same(preamble[1], result[1]);
        Assert.Contains(preamble[0], result);
        Assert.Contains(preamble[1], result);

        // 非受保护的最旧轮被整轮丢弃；受保护的最后一轮保留
        Assert.False(ContainsRef(result, rounds[0].User));
        Assert.True(ContainsRef(result, rounds[4].User));
    }

    [Fact]
    public async Task ShouldAlwaysKeepLastRound_RegardlessOfCandidate()
    {
        // R2/R3 末轮保护：候选全丢也无法移除最后一轮（刚结束/未完成轮）与倒数第二轮。
        var (history, rounds) = BuildPlainRounds(4);
        var strategy = new ExcludeAllGroupsStrategy();

        var result = await SnapshotCompactor.CompactAsync(strategy, history, DefaultMaxRounds);

        Assert.True(strategy.ExcludedGroupCount > 0, "自定义策略应确实排除了候选组");

        // 最后两轮（含末轮）强制保护，整轮完整
        var last = rounds[3];
        var secondLast = rounds[2];
        Assert.True(ContainsRef(result, last.User) && ContainsRef(result, last.Assistant));
        Assert.True(ContainsRef(result, secondLast.User) && ContainsRef(result, secondLast.Assistant));

        // 更旧的轮候选全丢 → 整轮丢弃（未被强制保护）
        Assert.False(ContainsRef(result, rounds[0].User));
        Assert.False(ContainsRef(result, rounds[1].User));
    }

    [Fact]
    public async Task ShouldDropOldestRoundsAndKeepLastTwoIntact_WhenRoundsExceedMaxRounds()
    {
        // R3：轮数 > maxRounds → 保留轮数 ≤ 上限，被丢弃者是最旧轮，最后 2 轮（含末轮）完整。
        var strategy = AguiCompaction.CreateStrategy();
        var (history, rounds) = BuildPlainRounds(15);

        var result = await SnapshotCompactor.CompactAsync(strategy, history, DefaultMaxRounds);

        int keptRounds = result.Count(m => m.Role == ChatRole.User);
        Assert.Equal(DefaultMaxRounds, keptRounds); // 恰好裁到上限
        Assert.True(result.Count < history.Count);

        // 丢弃最旧 3 轮
        for (int i = 0; i < 3; i++)
        {
            Assert.False(ContainsRef(result, rounds[i].User), $"最旧轮 {i + 1} 应被丢弃");
        }

        // 最后 2 轮完整
        foreach (int index in new[] { 13, 14 })
        {
            Assert.True(ContainsRef(result, rounds[index].User));
            Assert.True(ContainsRef(result, rounds[index].Assistant));
        }

        // 每个原轮整轮保留或整轮丢弃
        foreach (var (user, assistant) in rounds)
        {
            Assert.Equal(ContainsRef(result, user), ContainsRef(result, assistant));
        }
    }

    private static bool ContainsRef(IReadOnlyList<ChatMessage> messages, ChatMessage message) =>
        messages.Any(candidate => ReferenceEquals(candidate, message));

    private static IReadOnlyList<string> FunctionCallIds(IReadOnlyList<ChatMessage> messages) =>
        [.. messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId)];

    private static IReadOnlyList<string> FunctionResultIds(IReadOnlyList<ChatMessage> messages) =>
        [.. messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId)];

    private static (List<ChatMessage> History, List<(ChatMessage User, ChatMessage Assistant)> Rounds)
        BuildPlainRounds(int count)
    {
        var history = new List<ChatMessage>();
        var rounds = new List<(ChatMessage User, ChatMessage Assistant)>();
        for (int i = 1; i <= count; i++)
        {
            var user = new ChatMessage(ChatRole.User, $"用户{i}");
            var assistant = new ChatMessage(ChatRole.Assistant, $"回复{i}");
            history.Add(user);
            history.Add(assistant);
            rounds.Add((user, assistant));
        }

        return (history, rounds);
    }

    private static (List<ChatMessage> History, List<ToolRound> Rounds) BuildToolRounds(int count)
    {
        var history = new List<ChatMessage>();
        var rounds = new List<ToolRound>();
        for (int i = 1; i <= count; i++)
        {
            var user = new ChatMessage(ChatRole.User, $"用户{i}");
            var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{i}", "search_product", null)]);
            var toolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call_{i}", $"结果{i}")]);
            var assistant = new ChatMessage(ChatRole.Assistant, $"回复{i}");
            history.Add(user);
            history.Add(call);
            history.Add(toolResult);
            history.Add(assistant);
            rounds.Add(new ToolRound(user, call, toolResult, assistant));
        }

        return (history, rounds);
    }

    private sealed record ToolRound(ChatMessage User, ChatMessage Call, ChatMessage ToolResult, ChatMessage Assistant);

    /// <summary>
    /// 测试用确定性策略：排除<strong>所有</strong>组（含 System），使官方候选为空——
    /// 用于验证「preamble 始终保留」与「受保护轮无论候选如何整轮保留」。
    /// </summary>
    private sealed class ExcludeAllGroupsStrategy : CompactionStrategy
    {
        internal ExcludeAllGroupsStrategy()
            : base(CompactionTriggers.Always)
        {
        }

        internal int ExcludedGroupCount { get; private set; }

        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            int excluded = 0;
            foreach (CompactionMessageGroup group in index.Groups)
            {
                group.IsExcluded = true;
                excluded++;
            }

            ExcludedGroupCount = excluded;
            return ValueTask.FromResult(excluded > 0);
        }
    }
}
