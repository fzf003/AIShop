namespace AIShop.Core.Services;

/// <summary>
/// Reciprocal Rank Fusion（RRF）混合融合纯函数。
/// 混合检索时把「关键词路」与「向量路」两份排名列表融合为单一排序结果，使两路互补召回（AR-4）。
/// 放 Core 层以脱离任何基础设施做纯单元测试（官方 SqliteCollection 未实现 HybridSearch，R3）。
/// </summary>
public static class RrfFusion
{
    /// <summary>
    /// RRF 标准常数。避免 rank=1 的绝对优势——k 越大，排名靠前项的相对优势越弱。
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    /// 融合多路排名列表：score(item) = Σ 1/(k + rank)，rank 从 1 起。
    /// 相同 key 出现在多路时得分累加（如同一商品在关键词路与向量路同时命中）；
    /// 输出按得分降序取前 top 个，平分时按 key 的 Ordinal 升序决胜，保证结果确定性（AI-2）。
    /// </summary>
    /// <param name="rankedLists">多路排名列表，每路的 index+1 即该元素在路内的 rank。</param>
    /// <param name="keySelector">元素的融合键（如商品 ProductId.ToString()），跨路相同键视为同一实体。</param>
    /// <param name="top">返回的融合结果条数上限。</param>
    /// <param name="k">RRF 常数，默认 60。</param>
    public static IReadOnlyList<T> Fuse<T>(
        IReadOnlyList<IReadOnlyList<T>> rankedLists,
        Func<T, string> keySelector,
        int top = 5,
        int k = DefaultK)
    {
        // key → 累计得分 与 key → 代表元素：跨路同 key 视为同一实体，得分累加；
        // 代表元素取首次出现的那路（排序已由得分决定，代表元素只供输出，取哪个不影响语义）。
        var scores = new Dictionary<string, double>();
        var representatives = new Dictionary<string, T>();

        for (var listIndex = 0; listIndex < rankedLists.Count; listIndex++)
        {
            var rankedList = rankedLists[listIndex];
            for (var i = 0; i < rankedList.Count; i++)
            {
                var item = rankedList[i];
                var key = keySelector(item);
                var contribution = 1.0 / (k + (i + 1)); // rank 从 1 起，此处 i+1 即 rank

                if (scores.TryGetValue(key, out var existing))
                {
                    scores[key] = existing + contribution;
                }
                else
                {
                    scores[key] = contribution;
                    representatives[key] = item;
                }
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value)            // 得分降序
            .ThenBy(pair => pair.Key, StringComparer.Ordinal) // 平分按 key Ordinal 升序决胜（AI-2 确定性）
            .Take(top)
            .Select(pair => representatives[pair.Key])
            .ToList();
    }
}
