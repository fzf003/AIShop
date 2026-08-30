using AIShop.Core.Entities;

namespace AIShop.Core.Interfaces;

/// <summary>
/// RAG 索引维护契约（design §5.1 / §5.5）。
/// 索引是派生数据，权威在业务库；本接口只保证「索引最终一致」，不阻塞业务写路径。
/// </summary>
public interface IRagIndexer
{
    /// <summary>
    /// 确保索引已构建（幂等，AI-1）：已构建则短路；检测到索引脏标记时执行全量重建兜底（最终一致，AI-5）。
    /// 失败不置位成功标志，下次调用重试。
    /// </summary>
    Task EnsureIndexedAsync(CancellationToken ct = default);

    /// <summary>
    /// 全量重建：建表 → 从商品数据源构建 ProductDocument → 批量 embedding → upsert（按 key REPLACE）。
    /// </summary>
    Task RebuildAsync(CancellationToken ct = default);

    /// <summary>
    /// 增量新增/更新：按 collection key（"product-{id}"）对单条商品 upsert（REPLACE），
    /// 重复调用安全（幂等，AI-5）。
    /// </summary>
    Task UpsertProductAsync(Product product, CancellationToken ct = default);

    /// <summary>
    /// 增量删除：按商品 Id 移除对应 collection key；删除不存在 key 无副作用（幂等，AI-5）。
    /// </summary>
    Task RemoveProductAsync(int productId, CancellationToken ct = default);
}
