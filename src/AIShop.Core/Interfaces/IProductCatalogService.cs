using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface IProductCatalogService
{
    IReadOnlyList<Product> All { get; }
    IReadOnlyDictionary<string, string[]> KeywordMap { get; }

    Product[] ScoreProducts(IReadOnlyList<ChatMessage> history);
    Product[] MatchProducts(string[] preferences);
    (Product[] Recommended, Product[] Others) SplitProducts(string[] keywords);
}
