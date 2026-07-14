using AIShop.Core.Entities;
using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

public sealed class ProductCatalog : IProductCatalogService
{
    public IReadOnlyList<Product> All { get; } =
    [
        new() { Id = 1,  Name = "经典皮夹克",         Category = "服装",      Tags = ["皮衣", "外套", "时尚", "潮流", "夹克"],  Price = 189.99m, Emoji = "🧥" },
        new() { Id = 2,  Name = "有机棉T恤",           Category = "服装",      Tags = ["有机", "休闲", "环保", "基础款"],         Price = 29.99m,  Emoji = "👕" },
        new() { Id = 3,  Name = "专业跑鞋",             Category = "鞋类",      Tags = ["跑步", "运动", "健身", "体育", "鞋子"],    Price = 129.99m, Emoji = "👟" },
        new() { Id = 4,  Name = "无线降噪耳机",         Category = "电子产品",  Tags = ["音频", "音乐", "科技", "无线"],                 Price = 249.99m, Emoji = "🎧" },
        new() { Id = 5,  Name = "意式浓缩咖啡机",       Category = "厨房用品",  Tags = ["咖啡", "浓缩", "厨房", "早晨"],           Price = 349.99m, Emoji = "☕" },
        new() { Id = 6,  Name = "高级瑜伽垫",            Category = "健身",      Tags = ["瑜伽", "健身", "健康", "运动"],            Price = 59.99m,  Emoji = "🧘" },
        new() { Id = 7,  Name = "复古黑胶唱片机",       Category = "电子产品",  Tags = ["音乐", "复古", "唱片", "音频"],                   Price = 199.99m, Emoji = "🎵" },
        new() { Id = 8,  Name = "不锈钢保温水瓶",       Category = "配件",      Tags = ["环保", "可持续", "补水", "健身"],         Price = 24.99m,  Emoji = "💧" },
        new() { Id = 9,  Name = "畅销悬疑小说",         Category = "书籍",      Tags = ["阅读", "悬疑", "惊悚", "小说"],           Price = 14.99m,  Emoji = "📚" },
        new() { Id = 10, Name = "智能运动手表",         Category = "电子产品",  Tags = ["健身", "科技", "健康", "穿戴"],              Price = 199.99m, Emoji = "⌚" },
        new() { Id = 11, Name = "铸铁煎锅",             Category = "厨房用品",  Tags = ["烹饪", "厨房", "耐用", "经典"],            Price = 44.99m,  Emoji = "🍳" },
        new() { Id = 12, Name = "香薰蜡烛套装",         Category = "家居",      Tags = ["放松", "家居", "香薰", "礼物"],          Price = 34.99m,  Emoji = "🕯️" },
        new() { Id = 13, Name = "户外徒步靴",           Category = "鞋类",      Tags = ["徒步", "户外", "冒险", "自然", "靴子"],    Price = 159.99m, Emoji = "🥾" },
        new() { Id = 14, Name = "植物蛋白粉",           Category = "健康",      Tags = ["健身", "营养", "素食", "健康"],             Price = 39.99m,  Emoji = "💪" },
        new() { Id = 15, Name = "无线充电板",           Category = "电子产品",  Tags = ["科技", "充电", "无线", "数码"],             Price = 29.99m,  Emoji = "🔋" },
        new() { Id = 16, Name = "真丝枕套套装",         Category = "家居",      Tags = ["奢华", "睡眠", "护肤", "家居"],                 Price = 49.99m,  Emoji = "🛏️" },
        new() { Id = 17, Name = "园艺工具套装",         Category = "户外",      Tags = ["园艺", "户外", "自然", "爱好"],             Price = 54.99m,  Emoji = "🌱" },
        new() { Id = 18, Name = "手工巧克力礼盒",       Category = "食品",      Tags = ["巧克力", "礼物", "美食", "甜品"],               Price = 27.99m,  Emoji = "🍫" },
    ];

    public IReadOnlyDictionary<string, string[]> KeywordMap { get; } = new Dictionary<string, string[]>
    {
        ["夹克"] = ["皮衣", "外套", "夹克", "服装"],
        ["鞋子"] = ["鞋子", "跑鞋", "靴子", "鞋类"],
        ["靴子"] = ["靴子", "徒步", "鞋类"],
        ["音乐"] = ["音乐", "音频", "唱片", "复古"],
        ["咖啡"] = ["咖啡", "浓缩", "早晨", "厨房"],
        ["健身"] = ["健身", "运动", "体育", "锻炼", "跑步", "瑜伽", "健康"],
        ["瑜伽"] = ["瑜伽", "健身", "健康"],
        ["烹饪"] = ["烹饪", "厨房", "做饭", "耐用"],
        ["科技"] = ["科技", "电子", "数码", "无线", "充电", "穿戴"],
        ["阅读"] = ["阅读", "看书", "书", "悬疑", "小说"],
        ["户外"] = ["户外", "徒步", "自然", "冒险", "园艺"],
        ["时尚"] = ["时尚", "潮流", "服装", "皮衣", "奢华"],
        ["环保"] = ["环保", "可持续", "有机", "素食"],
        ["巧克力"] = ["巧克力", "美食", "甜品", "礼物"],
        ["跑步"] = ["跑步", "运动", "健身", "体育", "跑鞋"],
        ["家居"] = ["家居", "放松", "睡眠", "香薰", "护肤"],
        ["送礼"] = ["礼物", "美食", "香薰", "巧克力"],
        ["爱好"] = ["爱好", "园艺", "阅读", "音乐"],
        ["耳机"] = ["耳机", "音频", "音乐", "科技", "无线", "降噪"],
        ["手表"] = ["手表", "穿戴", "健身", "科技", "健康"],
        ["运动"] = ["运动", "健身", "体育", "跑步", "瑜伽", "健康"],
        ["音频"] = ["音频", "音乐", "耳机", "唱片"],
        ["数码"] = ["数码", "科技", "电子", "无线", "充电", "穿戴"],
    };

    private static readonly string[] NegativePatterns =
        ["不", "不喜欢", "不要", "讨厌", "别", "没兴趣", "不需要"];

    public Product[] ScoreProducts(IReadOnlyList<ChatMessage> history)
    {
        var likes = new HashSet<string>();
        var dislikes = new HashSet<string>();

        foreach (var text in history.Where(m => m.Role == "user").Select(m => m.Content))
        {
            foreach (var (keyword, expansions) in KeywordMap)
            {
                if (!text.Contains(keyword, StringComparison.Ordinal)) continue;

                var isNegative = NegativePatterns.Any(neg =>
                {
                    var idx = text.IndexOf(neg, StringComparison.Ordinal);
                    if (idx < 0) return false;
                    var after = text[(idx + neg.Length)..];
                    return after.Contains(keyword, StringComparison.Ordinal);
                });

                if (isNegative) dislikes.UnionWith(expansions);
                else likes.UnionWith(expansions);
            }
        }

        likes.ExceptWith(dislikes);

        return ScoreByTags(likes, dislikes);
    }

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
