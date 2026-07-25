namespace AIShop.Infrastructure.Entities;

public sealed class ChatMessageRecord
{
    public long Id { get; init; }
    public Guid SessionId { get; init; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? Reasoning { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsCompacted { get; set; }
}
