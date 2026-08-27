using AIShop.Core.Models;
using AIShop.Core.Services;

namespace AIShop.Api.Tests;

/// <summary>
/// RoundBasedCompactionPolicy 单元测试 — 验证三段压缩语义（完整轮/未完成轮/硬上限）与 run_id 分组规则。
/// 逻辑从 SqliteChatHistoryProvider 迁移，输入输出均为纯内存数据，不依赖 EF。
/// </summary>
public sealed class RoundBasedCompactionPolicyTests
{
    private static StoredMessage Msg(long id, Guid? runId, bool isFinal = false) =>
        new(id, Guid.NewGuid(), runId, "user", "text", null, null, null, null, isFinal, false, DateTime.UtcNow);

    // ---------- 阶段 1：完整轮超上限 ----------

    [Fact]
    public void ShouldCompressOldestCompleteRounds_WhenExceedingMaxCompletedRounds()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 2, maxIncompleteRounds: 5, hardLimit: 100);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var run3 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, run1, isFinal: true), Msg(2, run1),
            Msg(3, run2, isFinal: true), Msg(4, run2),
            Msg(5, run3, isFinal: true), Msg(6, run3),
        };

        var toCompact = policy.SelectCompaction(messages);

        Assert.Equal([1L, 2L], toCompact.OrderBy(x => x));
    }

    [Fact]
    public void ShouldNotCompress_WhenWithinAllLimits()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 12, maxIncompleteRounds: 5, hardLimit: 4096);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, run1, isFinal: true), Msg(2, run1),
            Msg(3, run2, isFinal: true), Msg(4, run2),
        };

        Assert.Empty(policy.SelectCompaction(messages));
    }

    // ---------- 阶段 2：未完成轮超上限 ----------

    [Fact]
    public void ShouldCompressOldestIncompleteRounds_WhenExceedingMaxIncomplete()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 100, maxIncompleteRounds: 1, hardLimit: 100);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, run1), Msg(2, run1),  // 未完成轮 1（无 is_final，2 行）
            Msg(3, run2), Msg(4, run2),  // 未完成轮 2
        };

        var toCompact = policy.SelectCompaction(messages);

        Assert.Equal([1L, 2L], toCompact.OrderBy(x => x));
    }

    // ---------- 阶段 3：条数硬上限 ----------

    [Fact]
    public void ShouldCompressOldestWholeRounds_WhenExceedingHardLimit()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 12, maxIncompleteRounds: 5, hardLimit: 5);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var messages = new List<StoredMessage>();
        for (var i = 1; i <= 5; i++) messages.Add(Msg(i, run1, isFinal: i == 5));
        for (var i = 6; i <= 10; i++) messages.Add(Msg(i, run2, isFinal: i == 10));

        var toCompact = policy.SelectCompaction(messages);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], toCompact.OrderBy(x => x));
    }

    // ---------- run_id==null 历史行 ----------

    [Fact]
    public void ShouldTreatNullRunIdRowsAsCompressibleCompleteRounds()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 1, maxIncompleteRounds: 5, hardLimit: 100);
        var run1 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, null), Msg(2, null), Msg(3, null),  // 每行自成组，视为可压缩完整轮
            Msg(4, run1, isFinal: true), Msg(5, run1),
        };

        var toCompact = policy.SelectCompaction(messages);

        // 完整轮共 4 个（3 个 null 行组 + run1），超 1 → 压最旧 3 个 null 行
        Assert.Equal([1L, 2L, 3L], toCompact.OrderBy(x => x));
    }

    [Fact]
    public void ShouldNotCountNullRunIdRowsAsIncompleteRounds()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 100, maxIncompleteRounds: 1, hardLimit: 100);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, null), Msg(2, null),
            Msg(3, run1), Msg(4, run1),  // 未完成轮 1
            Msg(5, run2), Msg(6, run2),  // 未完成轮 2
        };

        var toCompact = policy.SelectCompaction(messages);

        // 未完成轮仅统计 run_id 非空且 ≥2 行：2 个超 1 → 压最旧 run1；null 行（视为完整轮）不参与阶段 2
        Assert.Equal([3L, 4L], toCompact.OrderBy(x => x));
    }

    // ---------- 确定性 ----------

    [Fact]
    public void ShouldProduceDeterministicResult_OnSameInput()
    {
        var policy = new RoundBasedCompactionPolicy(maxCompletedRounds: 1, maxIncompleteRounds: 1, hardLimit: 3);
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var messages = new[]
        {
            Msg(1, run1, isFinal: true), Msg(2, run1),
            Msg(3, run2, isFinal: true), Msg(4, run2),
        };

        var first = policy.SelectCompaction(messages);
        var second = policy.SelectCompaction(messages);

        Assert.Equal(first, second);
    }
}
