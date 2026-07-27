namespace AIShop.Infrastructure.Entities;

/// <summary>
/// 映射到 chat_messages 表的新实体。
/// 列式存储替代旧的 ContentsJson 单列存储。
/// </summary>
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
