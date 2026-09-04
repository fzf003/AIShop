using AIShop.Core.Entities;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Rag;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// 诊断：查询「保温水瓶」与各记录的余弦相似度。
/// 目的：定位为什么语义检索中真正该命中的「不锈钢保温水瓶」分数反而低（0.198），无关记录反而高（0.6）。
/// 对照组：短名 vs 完整索引 Text（验证长 Text/CLS 池化是否稀释语义）。
/// </summary>
public sealed class EmbeddingDiagnosisTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public EmbeddingDiagnosisTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Diagnose_WarmBottle_QueryVsRecords()
    {
        var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "bge-small-zh-v1.5");
        var gen = new EmbeddingGenerator(
            Path.Combine(modelDir, "model.onnx"), Path.Combine(modelDir, "vocab.txt"));

        // 与 ProductSemanticSearch.EnsureIndexed 相同的 Text 拼接规则
        string Rec(string name, string cat, string tags, string price) =>
            $"{name}。类别：{cat}。标签：{tags}。价格：¥{price}";

        var query = "保温水瓶";
        var records = new (string Label, string Text)[]
        {
            ("保温瓶-短名", "不锈钢保温水瓶"),
            ("保温瓶-完整Text", Rec("不锈钢保温水瓶", "配件", "保温、水瓶、水杯", "29.99")),
            ("保温瓶-名+标签", "不锈钢保温水瓶。标签：保温、水瓶、水杯。"),
            ("香薰-完整Text", Rec("香薰蜡烛套装", "家居", "香薰、蜡烛", "39.99")),
            ("咖啡机-完整Text", Rec("意式浓缩咖啡机", "厨房用品", "咖啡、浓缩、厨房、早晨", "349.99")),
            ("保温水瓶-单词", "保温水瓶"),
        };

        var texts = new[] { query }.Concat(records.Select(r => r.Text)).ToArray();
        var embs = await gen.GenerateAsync(texts, null, CancellationToken.None);
        var q = embs[0].Vector;

        _output.WriteLine($"query='{query}' 与以下记录的余弦相似度：");
        for (int i = 0; i < records.Length; i++)
        {
            var cos = Cosine(q.Span, embs[i + 1].Vector.Span);
            Assert.InRange(cos, -1f, 1f);
            _output.WriteLine($"  {records[i].Label,-22} cos={cos:F4}  文本={records[i].Text}");
        }
    }

    /// <summary>
    /// 离线召回率评估（ground truth 由商品自身派生）：
    /// 1) 商品名自召回 Recall@1：搜商品名，自己是否排第一
    /// 2) 标签召回 Recall@5：搜 tag 词，含该 tag 的所有商品被覆盖多少
    /// 3) 类别召回 Recall@5：搜类别，该类商品被覆盖多少
    /// 余弦与 SqliteVec cosine distance 排序一致（distance=1-cos），评估可直接用余弦模拟检索。
    /// </summary>
    [Fact]
    public async Task Recall_Evaluation()
    {
        var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "bge-small-zh-v1.5");
        var gen = new EmbeddingGenerator(
            Path.Combine(modelDir, "model.onnx"), Path.Combine(modelDir, "vocab.txt"));
        string Text(Product p) => $"{p.Name}。类别：{p.Category}。标签：{string.Join("、", p.Tags)}。价格：¥{p.Price}";

        var products = ProductSeedData.Products.ToList();
        var docTexts = products.Select(Text).ToList();
        var docEmbs = await gen.GenerateAsync(docTexts, null, CancellationToken.None);

        // 预生成所有商品向量，供查询与 docs 余弦
        ReadOnlyMemory<float>[] docs = docEmbs.Select(e => e.Vector).ToArray();

        // 1) 商品名自召回（Recall@1 自，Top3 内算命中，容忍近义）
        int selfTop1 = 0, selfTop3 = 0;
        for (int i = 0; i < products.Count; i++)
        {
            var q = (await gen.GenerateAsync([products[i].Name], null, CancellationToken.None))[0].Vector;
            var ranks = Ranked(docs, q); // 余弦降序索引
            if (ranks[0] == i) selfTop1++;
            if (ranks.Take(3).Contains(i)) selfTop3++;
        }
        _output.WriteLine($"① 商品名自召回: Recall@1={selfTop1}/{products.Count}={selfTop1 / (double)products.Count:F2}  Top3={selfTop3}/{products.Count}");
        // 诊断断言：数据完整性（18 种子商品）；召回数值看 output
        Assert.Equal(18, products.Count);

        // 2) tag 召回 Recall@5（按含 tag 商品数过滤合理 tag）
        var tagQueries = new List<(string Q, HashSet<int> G)>();
        foreach (var p in products)
            foreach (var t in p.Tags)
            {
                var set = products.Where(x => x.Tags.Contains(t)).Select(x => products.IndexOf(x)).ToHashSet();
                if (!tagQueries.Any(tq => tq.Q == t))
                    tagQueries.Add((t, set));
            }
        await ReportRecallAsync(gen, docs, tagQueries, "② 标签召回 Recall@5");

        // 3) 类别召回 Recall@5
        var catQueries = products.GroupBy(p => p.Category)
            .Select(g => (g.Key, g.Select(x => products.IndexOf(x)).ToHashSet())).ToList();
        await ReportRecallAsync(gen, docs, catQueries, "③ 类别召回 Recall@5");
    }

    private async Task ReportRecallAsync(EmbeddingGenerator gen, ReadOnlyMemory<float>[] docs,
        List<(string Q, HashSet<int> G)> queries, string label)
    {
        double recallSum = 0; int count = 0;
        foreach (var (q, g) in queries)
        {
            if (g.Count == 0) continue;
            var qv = (await gen.GenerateAsync([q], null, CancellationToken.None))[0].Vector;
            var top5 = Ranked(docs, qv).Take(5).ToHashSet();
            var hits = top5.Intersect(g).Count();
            recallSum += hits / (double)g.Count;
            count++;
        }
        if (count > 0)
            _output.WriteLine($"{label}: {queries.Count} 个查询 平均 Recall@5={recallSum / count:F3}");
    }

    /// <summary>返回 doc 索引按余弦相似度降序。</summary>
    private static int[] Ranked(ReadOnlyMemory<float>[] docs, ReadOnlyMemory<float> query)
    {
        return Enumerable.Range(0, docs.Length)
            .OrderByDescending(i => Cosine(docs[i].Span, query.Span))
            .ToArray();
    }

    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
