using AIShop.Core.Interfaces;
using AIShop.Core.Models;

namespace AIShop.Core.Services;

/// <summary>
/// 基于轮次（run_id）的压缩策略 — 从 SqliteChatHistoryProvider 原样迁移的三段式压缩判断。
/// 语义（design §5.3）：按 run_id 整轮整切不拆轮，配对保护天然成立（FCC↔tool 结构性不分离）。
/// </summary>
/// <param name="maxCompletedRounds">完整轮保留上限（K=12 默认，容量约等于旧 50 条硬切）。</param>
/// <param name="maxIncompleteRounds">未完成轮上限（T7 安全阀 1，spec #11）。</param>
/// <param name="hardLimit">未压缩条数硬上限（T7 安全阀 2，spec #10）。</param>
public sealed class RoundBasedCompactionPolicy(
    int maxCompletedRounds = 12,
    int maxIncompleteRounds = 5,
    int hardLimit = 4096) : IChatCompactionPolicy
{
    /// <inheritdoc />
    public IReadOnlyList<long> SelectCompaction(IReadOnlyList<StoredMessage> messages)
    {
        // 仅考虑未压缩消息（防御：调用方应已过滤 IsCompacted==false）
        var rows = messages.Where(m => !m.IsCompacted).ToList();

        // 按 run_id 分组识别轮次；run_id==null 的历史行每行自成一组（用 Id 作组键），走旧行为视为可压缩完整轮
        var rounds = rows
            .GroupBy(r => new { RunId = r.RunId, NullRowKey = r.RunId is null ? r.Id : (long?)null })
            .Select(g => new
            {
                RunId = g.Key.RunId,
                // 完整轮判定：组内有 is_final=true 行；run_id==null 历史行视为可压缩
                IsComplete = g.Key.RunId is null || g.Any(r => r.IsFinal),
                MaxId = g.Max(r => r.Id),
                Ids = g.Select(r => r.Id).ToList(),
            })
            .ToList();

        var compressIds = new HashSet<long>();

        // 阶段 1：完整轮超上限 → 从最旧完整轮开始整轮压缩，直到剩余完整轮数 ≤ K
        var completeRounds = rounds
            .Where(r => r.IsComplete)
            .OrderBy(r => r.MaxId)
            .ToList();
        var completeToCompress = completeRounds.Count - maxCompletedRounds;
        if (completeToCompress > 0)
            compressIds.UnionWith(completeRounds.Take(completeToCompress).SelectMany(r => r.Ids));

        // 阶段 2：未完成轮上限（T7 安全阀 1，spec #11/#12）——只统计 run_id 非空且组内行数 ≥2 的组；
        // run_id==null 历史行不计入未完成轮（否则历史库瞬间超上限触发误压缩）
        var incompleteRounds = rounds
            .Where(r => !r.IsComplete && r.RunId is not null && r.Ids.Count >= 2)
            .OrderBy(r => r.MaxId)
            .ToList();
        var incompleteToCompress = incompleteRounds.Count - maxIncompleteRounds;
        if (incompleteToCompress > 0)
            compressIds.UnionWith(incompleteRounds.Take(incompleteToCompress).SelectMany(r => r.Ids));

        // 阶段 3：条数硬上限（T7 安全阀 2，spec #10）——保留区超限时压最旧整轮至硬上限内
        var remainingCount = rows.Count - compressIds.Count;
        if (remainingCount > hardLimit)
        {
            var overflow = remainingCount - hardLimit;
            var candidates = rounds
                .Where(r => !r.Ids.Any(compressIds.Contains))
                .OrderBy(r => r.MaxId)
                .ToList();

            // 累计最旧整轮行数，取超过溢出量所需的最少整轮前缀（整轮整切，末轮可能多压到硬上限以内）
            var takeCount = 0;
            var accumulatedRows = 0L;
            while (takeCount < candidates.Count && accumulatedRows < overflow)
            {
                accumulatedRows += candidates[takeCount].Ids.Count;
                takeCount++;
            }

            compressIds.UnionWith(candidates.Take(takeCount).SelectMany(r => r.Ids));
        }

        return compressIds.ToList();
    }
}
