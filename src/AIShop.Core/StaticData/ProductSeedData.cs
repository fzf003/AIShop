using AIShop.Core.Entities;

namespace AIShop.Core.StaticData;

/// <summary>
/// 商品种子数据（18 条），从 ProductCatalog.All 迁出的静态常量。
/// 供 Program.cs 幂等播种与测试兜底复用；Id 显式固定为 1..18，保证与购物车/工具引用的 productId 一致。
/// </summary>
public static class ProductSeedData
{
    public static IReadOnlyList<Product> Products { get; } =
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
}
