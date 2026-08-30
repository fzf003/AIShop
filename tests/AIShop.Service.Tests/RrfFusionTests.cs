using AIShop.Core.Services;

namespace AIShop.Service.Tests;

/// <summary>
/// RrfFusion 纯函数测试（spec AR-4 互补性 / AI-2 确定性）。
/// 测试直接用字符串元素 + 自身作 key，验证 RRF 融合的排序契约，不依赖任何基础设施。
/// </summary>
public sealed class RrfFusionTests
{
    [Fact]
    public void Fuse_WithSingleRankedList_ReturnsOriginalOrderAndTruncatesToTop()
    {
        // 单路输入：rank 越靠前得分越高（score = 1/(k+rank) 递减），输出序应等于输入序；
        // top 参数截断前 N 个（AI-2 确定性）。
        IReadOnlyList<string> ranked = ["a", "b", "c", "d", "e"];

        var truncated = RrfFusion.Fuse([ranked], key => key, top: 3);

        Assert.Equal(["a", "b", "c"], truncated);

        // top 足够大时全量返回且顺序不变
        var full = RrfFusion.Fuse([ranked], key => key, top: 5);
        Assert.Equal(["a", "b", "c", "d", "e"], full);
    }

    [Fact]
    public void Fuse_WithTwoComplementaryLists_RecallsItemsEachRouteMisses()
    {
        // 关键词路仅命中 A、C，向量路仅命中 B、D：任取单路都会漏掉另一路的结果，
        // 融合后同时召回全部四项（AR-4 互补性）。
        IReadOnlyList<string> keywordRoute = ["A", "C"];
        IReadOnlyList<string> vectorRoute = ["B", "D"];

        var fused = RrfFusion.Fuse([keywordRoute, vectorRoute], key => key, top: 4);

        // A/B 均 rank1（同分）、C/D 均 rank2（同分）；同分按 key Ordinal 升序 → A、B、C、D
        Assert.Equal(["A", "B", "C", "D"], fused);
    }

    [Fact]
    public void Fuse_WithEqualScores_OrdersByKeyOrdinalAscending()
    {
        // X 与 Y 得分数学相等：X = 1/62 + 1/61、Y = 1/61 + 1/62（累加顺序无关，double 加法可交换）。
        // 两路输入互为反序——若按输入顺序/字典插入序稳定排序会得 [Y, X]；
        // 契约要求平分按 key Ordinal 升序决胜（X 在 Y 前），结果必须为 [X, Y]（AI-2 确定性）。
        IReadOnlyList<string> first = ["Y", "X"];
        IReadOnlyList<string> second = ["X", "Y"];

        var fused = RrfFusion.Fuse([first, second], key => key, top: 2);

        Assert.Equal(["X", "Y"], fused);
    }

    [Fact]
    public void Fuse_AccumulatesScoresAcrossLists_ByInverseRankFormula()
    {
        // 得分公式 score(item) = Σ 1/(k + rank)：A 仅在路1 rank1（1/61 ≈ 0.0164），
        // B 在路1 rank2 + 路2 rank1（1/62 + 1/61 ≈ 0.0325）——跨路得分累加使 B 反超单路 rank1 的 A，
        // 证明融合按公式累加而非只取最优单路（若只取最优路，A 与 B 同分会由 key 决胜出 A）。
        IReadOnlyList<string> route1 = ["A", "B"];
        IReadOnlyList<string> route2 = ["B"];

        var fused = RrfFusion.Fuse([route1, route2], key => key, top: 2);

        Assert.Equal(["B", "A"], fused);
    }
}
