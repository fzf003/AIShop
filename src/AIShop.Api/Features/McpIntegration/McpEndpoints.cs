using AIShop.Api.Features.Mcp;
using Microsoft.AspNetCore.Mvc;

namespace AIShop.Api.Features.McpIntegration;

/// <summary>
/// Standalone endpoints that use MCP Server for product matching.
/// These bypass the LLM-based ShoppingAssistantAgent entirely.
/// </summary>
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mcp").WithTags("Mcp");

        group.MapPost("/match", async (
            [FromBody] McpMatchRequest request,
            McpProductClient mcpClient,
            CancellationToken ct) =>
        {
            var results = await mcpClient.MatchProductsAsync(request.Keywords, ct);
            return Results.Ok(new { products = results, count = results.Length });
        })
        .WithName("McpMatchProducts")
        .WithDescription("通过 MCP 协议调用 match_products 进行关键词商品匹配");
    }
}

public sealed record McpMatchRequest(string[] Keywords);
