namespace AIShop.Core.Entities;

/// <summary>
/// 商品实体。字段契约见 design 3.1：Id（主键，种子显式 1..18）/ Name / Category / Tags / Price / Emoji。
/// 属性使用 set 而非 init：EF Core materialization 无法给 init-only 属性赋值，
/// 否则从 DB 读回时 Tags/Price 会变成默认值（[] / 0m），导致播种、查询、匹配全错。
/// </summary>
public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public decimal Price { get; set; }
    public string Emoji { get; set; } = string.Empty;
}
