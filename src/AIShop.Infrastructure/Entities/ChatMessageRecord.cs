namespace AIShop.Infrastructure.Entities;

/// <summary>
/// 映射到 chat_messages 表的新实体。
/// 列式存储替代旧的 ContentsJson 单列存储。
/// </summary>
public sealed class ChatMessageRecord
{
    public long Id { get; init; }
    public Guid SessionId { get; init; }
    public Guid? RunId { get; set; }   // 轮次分组标识，可空兼容存量（同一轮所有消息共享）
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? Reasoning { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsCompacted { get; set; }
    public bool IsFinal { get; set; }  // 轮次终点标记，默认 false（常规为末条纯文本 assistant 行）
}
