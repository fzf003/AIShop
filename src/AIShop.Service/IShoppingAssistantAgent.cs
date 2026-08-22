using Microsoft.Agents.AI;

namespace AIShop.Service;

public interface IShoppingAssistantAgent
{
    Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, string username,
        string? preferences = null, CancellationToken ct = default);

    // 新增：流式方法（返回文本增量流 + 完整结果）
    IAsyncEnumerable<ChatStreamChunk> RunChatStreamAsync(
        Guid sessionId, string userMessage, string username,
        string? preferences = null, CancellationToken ct = default);
}
