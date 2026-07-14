using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;

namespace AIShop.Infrastructure.Repositories;

internal sealed class ChatMessageRepository(AppDbContext db) : IChatMessageRepository
{
    public void Add(ChatMessage message) => db.ChatMessages.Add(message);

    public async Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(
        Guid sessionId, int? take = null, CancellationToken ct = default)
    {
        var query = db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp);

        if (take.HasValue)
        {
            var items = await query.Take(take.Value).ToListAsync(ct);
            return items;
        }

        return await query.ToListAsync(ct);
    }

    public async Task<ChatMessage?> GetLastUserMessageAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        return await db.ChatMessages
            .Where(m => m.SessionId == sessionId && m.Role == "user")
            .OrderByDescending(m => m.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);
}
