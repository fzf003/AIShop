using AIShop.Core.Models;

namespace AIShop.Core.Interfaces;

/// <summary>
/// 聊天历史存储接口 — 只负责 EF/DB 读写，不做压缩决策。
/// 输入输出统一以 <see cref="StoredMessage"/>（Core 防腐层模型）承载。
/// </summary>
public interface IChatHistoryStore
{
    /// <summary>加载指定会话的全部未压缩消息（IsCompacted==false），按 Id 升序。</summary>
    Task<IReadOnlyList<StoredMessage>> LoadUncompactedAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>追加新消息并提交；消息的 Id 由存储生成。</summary>
    Task AppendAsync(Guid sessionId, IReadOnlyList<StoredMessage> messages, CancellationToken ct = default);

    /// <summary>将指定行标记 IsCompacted=true（非物理删除，保留原始数据可追溯）。</summary>
    Task MarkCompactedAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>Run 后兜底补标轮次终点：该轮已有 is_final 行则跳过，否则末条（max id）补标。</summary>
    Task MarkRoundFinalAsync(Guid runId, CancellationToken ct = default);
}
