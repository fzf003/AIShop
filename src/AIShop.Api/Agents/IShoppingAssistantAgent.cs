using AIShop.Api.Features.Chat;
using Microsoft.Agents.AI;

namespace AIShop.Api.Agents;

public interface IShoppingAssistantAgent
{
    Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, string username, CancellationToken ct = default);
}
