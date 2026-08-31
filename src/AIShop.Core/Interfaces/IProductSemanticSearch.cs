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
    /// <param name="top">返回条数</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<ProductSearchHit>> SearchAsync(string query, int top = 5, CancellationToken ct = default);
}
