using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<User> CreateAsync(string username, string displayName, CancellationToken ct = default);
}

public interface ISessionRepository
{
    Task<string> GetOrCreateSessionIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetSessionHistoryAsync(Guid sessionId, CancellationToken ct = default);
}

public interface IProductRepository
{
    IReadOnlyList<Product> GetAll();
}
