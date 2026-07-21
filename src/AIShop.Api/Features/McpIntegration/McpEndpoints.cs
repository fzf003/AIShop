// Copyright (c) AIShop. All rights reserved.
// MCP Server 暂不启用，代码保留不动。
#pragma warning disable S125
using AIShop.Api.Features.Mcp;
using Microsoft.AspNetCore.Mvc;
/*
namespace AIShop.Api.Features.McpIntegration;

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
*/
#pragma warning restore S125
