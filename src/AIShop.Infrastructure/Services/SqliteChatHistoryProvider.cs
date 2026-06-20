using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Infrastructure.Services;

public class SqliteChatHistoryProvider(
    IDbContextFactory<AppDbContext> dbFactory) : ChatHistoryProvider()
{
    private const int MaxMessages = 20;

    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = GetSessionId(context.Session!);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var messages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Take(MaxMessages)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(cancellationToken);

        return messages.Select(m => new AgentChatMessage(
            m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            m.Content));
    }

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        // Messages are persisted in ChatEndpoints and kept in SQLite forever.
        // ProvideChatHistoryAsync loads only the latest N for the LLM.
        return default;
    }

    private static Guid GetSessionId(AgentSession session)
    {
        if (session.StateBag.TryGetValue<string>("SessionId", out var id, null) && id is not null)
            return Guid.Parse(id);
        throw new InvalidOperationException("SessionId not found in session StateBag.");
    }
}
