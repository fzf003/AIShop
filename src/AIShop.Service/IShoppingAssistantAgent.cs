using Microsoft.Agents.AI;

namespace AIShop.Service;

public interface IShoppingAssistantAgent
{
    Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, string username,
        string? preferences = null, CancellationToken ct = default);
}
