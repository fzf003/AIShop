using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AIShop.Core.Interfaces;
using Serilog;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Agents;

public sealed class SqliteChatHistoryProvider(
    IDbContextFactory<AppDbContext> dbFactory) : ChatHistoryProvider()
{
    private const int MaxMessages = 20;

    private static readonly Serilog.ILogger Logger = Log.ForContext<SqliteChatHistoryProvider>();

    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = GetSessionId(context.Session!);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var messages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Take(MaxMessages)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(cancellationToken);

        sw.Stop();
        Logger.Information("[Diagnose] 历史加载耗时 HistoryLoad={ElapsedMs}ms SessionId={SessionId} Count={Count}",
            sw.ElapsedMilliseconds, sessionId, messages.Count);

        return messages.Select(m => new AgentChatMessage(
            m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            m.Content));
    }

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        return default;
    }

    private static Guid GetSessionId(AgentSession session)
    {
        if (session.StateBag.TryGetValue<string>("SessionId", out var id, null) && id is not null)
            return Guid.Parse(id);
        throw new InvalidOperationException("SessionId not found in session StateBag.");
    }
}
