namespace AIShop.Core.Models;

/// <summary>
/// 聊天历史存储模型 — EF 实体（ChatMessageRecord）与 MAF 消息（AgentChatMessage）之间的防腐层。
/// 纯数据、无框架依赖；字段与 chat_messages 表业务列一一对应。
/// </summary>
public sealed record StoredMessage(
    long Id,
    Guid SessionId,
    Guid? RunId,
    string Role,
    string? Content,
    string? ToolCalls,
    string? ToolCallId,
    string? ToolName,
    string? Reasoning,
    bool IsFinal,
    bool IsCompacted,
    DateTime CreatedAt);
