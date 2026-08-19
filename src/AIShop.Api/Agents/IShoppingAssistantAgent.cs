using AIShop.Service;
using Microsoft.Agents.AI;

namespace AIShop.Api.Agents;

public interface IShoppingAssistantAgent
{
    Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, string username,
        string? preferences = null, CancellationToken ct = default);
}
