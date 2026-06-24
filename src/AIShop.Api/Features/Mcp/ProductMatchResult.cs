namespace AIShop.Api.Features.Mcp;

public sealed record ProductMatchResult(
    int Id,
    string Name,
    string Category,
    string[] Tags,
    decimal Price,
    string Emoji);
