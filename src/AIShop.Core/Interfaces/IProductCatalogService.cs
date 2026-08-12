using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface IProductCatalogService
{
    IReadOnlyList<Product> All { get; }
    IReadOnlyDictionary<string, string[]> KeywordMap { get; }

    Product[] MatchProducts(string[] preferences);
    (Product[] Recommended, Product[] Others) SplitProducts(string[] keywords);
}
