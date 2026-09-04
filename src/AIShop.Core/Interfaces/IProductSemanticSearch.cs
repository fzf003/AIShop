using AIShop.Core.Models;

namespace AIShop.Core.Interfaces;

/// <summary>
/// 商品语义搜索：输入自然语言，返回语义相关的商品（bge 向量检索，DB 端 KNN，不读全量）。
/// </summary>
public interface IProductSemanticSearch
{
    /// <summary>
    /// 语义检索商品：查询文本 → 向量 → 向量库 top-k 召回。
    /// </summary>
    /// <param name="query">用户自然语言查询（如「适合送礼的咖啡机」）</param>
    /// <param name="domain">领域过滤（扩展缝）：非空时仅检索该领域记录（当前仅 "product"）；未来加订单/FAQ 传对应 domain</param>
    /// <param name="top">返回条数</param>
    /// <param name="category">可选商品类别过滤（如「厨房用品」）：非空时向量 KNN 仅在该类别子集内排序，
    /// 供用户明确指定类别时的精确召回，避免泛类排前</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<ProductSearchHit>> SearchAsync(
        string query, string? domain = null, int top = 5, string? category = null, CancellationToken ct = default);

    /// <summary>
    /// 确保索引已构建（幂等）。供启动预热调用（加载模型 + 建索引在启动时完成，
    /// 避免首次检索卡顿）；未预热时 <see cref="SearchAsync"/> 内部懒构建兜底。
    /// </summary>
    Task EnsureIndexedAsync(CancellationToken ct = default);
}
