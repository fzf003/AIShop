using AIShop.Core.Entities;
using AIShop.Core.StaticData;

namespace AIShop.Service.Agui;

/// <summary>
/// **AguiHost 专用**的商品种子数据（26 条 = 共享的 18 条 + 本宿主追加的 8 条）。
///
/// <para><b>为什么单独一份而不是直接扩充共享的那份</b>：<see cref="ProductSeedData"/> 同时被
/// <c>AIShop.Api</c> 与 <c>AIShop.AguiHost</c> 使用（两宿主各播各的库，但读同一份静态常量）。
/// 往它里面加商品，会连带改变 **Api 侧**的商品集 —— 而 Api 侧有「18 件」的播种与端点断言，
/// 且 Api 属本批次禁改范围。故改在这里**追加**：共享那份维持 18 条不变，AguiHost 用自己的这份。</para>
///
/// <para><b>为什么在共享数据上追加、而不是另写一份完整的</b>：两宿主的商品**应当同源** ——
/// 同一件「专业跑鞋」在两处必须是同样的名称/价格/标签，否则同一个用户在两条链路看到的商品会不一致。
/// 故这里 = 共享的 18 条 **+** 本宿主追加的 8 条（<c>[.. a, .. b]</c> 集合表达式拼接），
/// 追加部分照抄共享的书写风格（显式 Id、四要素：Name/Category/Tags/Price/Emoji）。</para>
///
/// <para><b>Id 约定</b>：续接共享数据的 1..18，追加部分从 <b>19</b> 起，保证与购物车/工具
/// 引用的 <c>productId</c> 不冲突（购物车按 Id 引用商品）。</para>
///
/// <para><b>追加这 8 条的用途</b>：原 18 条覆盖面偏窄（服装仅 2 件），扩到 26 条后
/// 语义推荐（工单 B1/D 的检索路径）更容易看出效果，也覆盖了「月饼/围巾/音箱」这类
/// 原有词表未收录的品类。</para>
/// </summary>
internal static class AguiProductSeedData
{
    /// <summary>
    /// 本宿主相对共享数据**追加**的 8 条（Id 19..26）。
    /// 新增商品请续在此处追加（保持 Id 递增），不要改共享那份。
    /// </summary>
    private static IReadOnlyList<Product> Additional { get; } =
    [
        new() { Id = 19, Name = "中秋月饼礼盒",   Category = "食品",     Tags = ["月饼", "中秋", "送礼", "传统", "甜品"], Price = 168.00m, Emoji = "🥮" },
        new() { Id = 20, Name = "精品手冲咖啡豆", Category = "食品",     Tags = ["咖啡", "手冲", "早晨", "美食"],         Price = 89.00m,  Emoji = "☕" },
        new() { Id = 21, Name = "羊绒保暖围巾",   Category = "服装",     Tags = ["围巾", "保暖", "冬季", "时尚"],         Price = 259.00m, Emoji = "🧣" },
        new() { Id = 22, Name = "经典直筒牛仔裤", Category = "服装",     Tags = ["牛仔", "休闲", "百搭", "时尚"],         Price = 199.00m, Emoji = "👖" },
        new() { Id = 23, Name = "客厅懒人沙发",   Category = "家居",     Tags = ["沙发", "家居", "放松", "舒适"],         Price = 599.00m, Emoji = "🛋️" },
        new() { Id = 24, Name = "便携蓝牙音箱",   Category = "电子产品", Tags = ["音箱", "音乐", "无线", "便携"],         Price = 299.00m, Emoji = "🔊" },
        new() { Id = 25, Name = "养生茶礼盒",     Category = "健康",     Tags = ["茶", "养生", "健康", "送礼"],           Price = 128.00m, Emoji = "🍵" },
        new() { Id = 26, Name = "商务正装皮鞋",   Category = "鞋类",     Tags = ["皮鞋", "商务", "正式", "鞋子"],         Price = 459.00m, Emoji = "👞" },
    ];

    /// <summary>
    /// AguiHost 的完整商品种子 = 共享的 18 条 + 本宿主追加的 8 条。
    /// 播种与「按 Id 补缺失」都以本属性为唯一来源。
    /// </summary>
    public static IReadOnlyList<Product> Products { get; } =
        [.. ProductSeedData.Products, .. Additional];
}
