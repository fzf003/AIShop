using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface IChatMessageRepository
{
    void Add(ChatMessage message);

    Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(
        Guid sessionId, int? take = null, CancellationToken ct = default);

    Task<ChatMessage?> GetLastUserMessageAsync(
        Guid sessionId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
