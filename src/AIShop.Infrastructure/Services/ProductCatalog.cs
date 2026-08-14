using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 商品目录服务。构造注入 IProductRepository（不再注入 AppDbContext/直接查库），
/// All 统一走 ProductRepository 的 5 分钟缓存（单一数据源，消除快照 vs 缓存双轨不一致）。
/// </summary>
public sealed class ProductCatalog(IProductRepository repository) : IProductCatalogService
{
    public IReadOnlyList<Product> All => repository.GetAll();

    public IReadOnlyDictionary<string, string[]> KeywordMap => ProductKeywordMap.Entries;

    public Product[] MatchProducts(string[] preferences)
    {
        if (preferences.Length == 0) return [];

        var likes = new HashSet<string>(preferences, StringComparer.Ordinal);
        foreach (var kw in preferences)
        {
            if (KeywordMap.TryGetValue(kw, out var expansions))
                likes.UnionWith(expansions);
        }

        return ScoreByTags(likes, []);
    }

    public (Product[] Recommended, Product[] Others) SplitProducts(string[] keywords)
    {
        if (keywords.Length == 0)
            return ([], All.Take(6).ToArray());

        var orderedTags = new List<(int Index, string Tag)>();
        for (int i = 0; i < keywords.Length; i++)
        {
            orderedTags.Add((i, keywords[i]));
            if (KeywordMap.TryGetValue(keywords[i], out var expansions))
            {
                foreach (var tag in expansions)
                    orderedTags.Add((i, tag));
            }
        }

        var recommended = new List<(int Priority, Product Product)>();
        var others = new List<Product>();

        foreach (var product in All)
        {
            var searchable = new HashSet<string>(product.Tags, StringComparer.Ordinal)
            {
                product.Category
            };

            var earliestMatch = int.MaxValue;
            foreach (var (idx, tag) in orderedTags)
            {
                if (searchable.Contains(tag) && idx < earliestMatch)
                    earliestMatch = idx;
            }

            if (earliestMatch < int.MaxValue)
                recommended.Add((earliestMatch, product));
            else
                others.Add(product);
        }

        recommended.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return (recommended.Select(r => r.Product).ToArray(), others.ToArray());
    }

    private Product[] ScoreByTags(HashSet<string> likes, HashSet<string> dislikes)
    {
        var scored = new List<(int Score, Product Product)>();

        foreach (var product in All)
        {
            var searchable = new HashSet<string>(product.Tags, StringComparer.Ordinal)
            {
                product.Category
            };

            if (searchable.Overlaps(dislikes)) continue;

            var score = searchable.Count(t => likes.Contains(t));
            if (score > 0) scored.Add((score, product));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        return scored.Take(6).Select(s => s.Product).ToArray();
    }
}
