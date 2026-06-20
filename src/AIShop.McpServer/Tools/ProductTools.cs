using System.ComponentModel;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Services;
using ModelContextProtocol.Server;

namespace AIShop.McpServer.Tools;

[McpServerToolType]
public static class ProductTools
{
    [McpServerTool, Description("根据中文关键词匹配商品。传入中文关键词列表（如 [\"运动\", \"户外\", \"咖啡\"]），返回按相关度排序的最多 6 条匹配商品。")]
    public static MatchProductDto[] MatchProducts(
        [Description("中文关键词列表，例如 [\"运动\", \"户外\", \"咖啡\"]。支持 23 个预定义关键词，自动扩展匹配标签。")]
        string[] keywords,
        CancellationToken cancellationToken = default)
    {
        var products = ProductCatalog.MatchProducts(keywords);
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
