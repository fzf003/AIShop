using System.ComponentModel;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using ModelContextProtocol.Server;

namespace AIShop.McpServer.Tools;

[McpServerToolType]
public sealed class ProductTools(IProductCatalogService catalog)
{
    [McpServerTool, Description("根据中文关键词匹配商品。传入中文关键词列表（如 [\"运动\", \"户外\", \"咖啡\"]），返回按相关度排序的最多 6 条匹配商品。")]
    public MatchProductDto[] MatchProducts(
        [Description("中文关键词列表，例如 [\"运动\", \"户外\", \"咖啡\"]。支持 23 个预定义关键词，自动扩展匹配标签。")]
        string[] keywords,
        CancellationToken cancellationToken = default)
    {
        var products = catalog.MatchProducts(keywords);
        return products.Select(MatchProductDto.FromEntity).ToArray();
    }
}

public sealed record MatchProductDto(
    int Id,
    string Name,
    string Category,
    string[] Tags,
    decimal Price,
    string Emoji)
{
    public static MatchProductDto FromEntity(Product product) => new(
        product.Id,
        product.Name,
        product.Category,
        product.Tags,
        product.Price,
        product.Emoji);
}
