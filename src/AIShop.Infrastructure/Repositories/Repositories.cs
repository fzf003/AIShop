using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;

namespace AIShop.Infrastructure.Repositories;

internal sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<User> CreateAsync(string username, string displayName, CancellationToken ct = default)
    {
        var user = new User { Username = username, DisplayName = displayName };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }
}

internal sealed class SessionRepository(AppDbContext db) : ISessionRepository
{
    public async Task<string> GetOrCreateSessionIdAsync(Guid userId, CancellationToken ct = default)
    {
        var session = await db.Sessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastActivityAt)
            .FirstOrDefaultAsync(ct);

        string sessionId;
        if (session is not null)
        {
            session.LastActivityAt = DateTime.UtcNow;
            sessionId = session.Id.ToString();
        }
        else
        {
            var newSession = new Session { UserId = userId, LastActivityAt = DateTime.UtcNow };
            db.Sessions.Add(newSession);
            sessionId = newSession.Id.ToString();
        }

        await db.SaveChangesAsync(ct);
        return sessionId;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(Guid sessionId, CancellationToken ct = default) =>
        await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(ct);
}
