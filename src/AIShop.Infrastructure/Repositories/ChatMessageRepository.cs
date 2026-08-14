using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;

namespace AIShop.Infrastructure.Repositories;

internal sealed class ChatMessageRepository(AppDbContext db) : IChatMessageRepository
{
    public async Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(
        Guid sessionId, int? take = null, CancellationToken ct = default)
    {
        var query = db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .Where(p => p.Role != "tool" && !string.IsNullOrWhiteSpace(p.Content))
            .OrderBy(m => m.Id);

        if (take.HasValue)
            query = (IOrderedQueryable<ChatMessageRecord>)query.Take(take.Value);

        var rows = await query.ToListAsync(ct);

        return rows.Select(r => new ChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = r.SessionId,
            Role = r.Role,
            Content = r.Content,
            SequentialNumber = r.Id,
            Timestamp = r.CreatedAt,
        }).ToList();
    }

    public async Task<ChatMessage?> GetLastUserMessageAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        var row = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && m.Role == "user")
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new ChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = row.SessionId,
            Role = row.Role,
            Content = row.Content,
            SequentialNumber = row.Id,
            Timestamp = row.CreatedAt,
        };
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct);
}
