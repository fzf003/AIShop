using Microsoft.EntityFrameworkCore;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 聊天历史存储实现：EF Core + SQLite 读写，仅负责持久化，不做压缩/转换决策。
/// 短生命周期 DbContext（IDbContextFactory 按需创建），可被长生命周期 Provider 安全持有。
/// </summary>
public sealed class ChatHistoryStore(IDbContextFactory<AppDbContext> dbFactory) : IChatHistoryStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredMessage>> LoadUncompactedAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        return rows.Select(ToStored).ToList();
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        Guid sessionId, IReadOnlyList<StoredMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ChatMessageRecords.AddRange(messages.Select(ToRecord));
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkCompactedAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ChatMessageRecords
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCompacted, true), ct);
    }

    /// <inheritdoc />
    public async Task MarkRoundFinalAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 该轮已有 is_final=true 终点行 → 正常完成，不重复补标
        if (await db.ChatMessageRecords.AnyAsync(m => m.RunId == runId && m.IsFinal, ct))
            return;

        // 该轮最后一条（max id）即为轮次终点；runId 无对应行时返回 null，静默返回不抛异常
        var last = await db.ChatMessageRecords
            .Where(m => m.RunId == runId)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);
        if (last is null) return;

        last.IsFinal = true;
        await db.SaveChangesAsync(ct);
    }

    private static StoredMessage ToStored(ChatMessageRecord r) => new(
        r.Id, r.SessionId, r.RunId, r.Role, r.Content, r.ToolCalls, r.ToolCallId, r.ToolName, r.Reasoning,
        r.IsFinal, r.IsCompacted, r.CreatedAt);

    private static ChatMessageRecord ToRecord(StoredMessage m) => new()
    {
        SessionId = m.SessionId,
        RunId = m.RunId,
        Role = m.Role,
        Content = m.Content ?? "",
        ToolCalls = m.ToolCalls,
        ToolCallId = m.ToolCallId,
        ToolName = m.ToolName,
        Reasoning = m.Reasoning,
        IsFinal = m.IsFinal,
        IsCompacted = m.IsCompacted,
        CreatedAt = m.CreatedAt,
    };
}
