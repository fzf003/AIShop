using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIShop.Core.ValueObjects;

/// <summary>
/// 用户偏好配置文件（强类型，替代散落的 KeywordsJson 字符串解析）。
/// 序列化（FromKeywordsJson/ToKeywordsJson）只在调用边界（仓储、Provider、后台 worker）进行，
/// Core 内部统一以 <see cref="KeywordWeights"/> 字典承载。
/// </summary>
public sealed record PreferenceProfile(
    Guid UserId,
    IReadOnlyDictionary<string, int> KeywordWeights,
    DateTime UpdatedAt)
{
    /// <summary>Top-N 权重保留上限（对齐 PreferenceWriteHostedService 现有 Top-20 截断语义）。</summary>
    public const int MaxKeywords = 20;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        // 中文不转义，与存量 KeywordsJson 格式兼容（{"咖啡":2} 而非 {"咖啡":2}）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 从存储 JSON（{词: 权重}）构造。null/空/空白视为无偏好返回空集；
    /// 非空但非法 JSON 视为存储损坏，抛 <see cref="JsonException"/>，由调用方决定降级策略
    /// （读取侧容错为空集，写入 worker 侧跳过该条保留原值）。
    /// </summary>
    public static PreferenceProfile FromKeywordsJson(Guid userId, string? keywordsJson, DateTime updatedAt)
    {
        if (string.IsNullOrWhiteSpace(keywordsJson))
            return new PreferenceProfile(userId, new Dictionary<string, int>(), updatedAt);

        var weights = JsonSerializer.Deserialize<Dictionary<string, int>>(keywordsJson) ?? [];
        return new PreferenceProfile(userId, weights, updatedAt);
    }

    /// <summary>序列化为 {词: 权重} JSON（存储格式，与 DB KeywordsJson 列兼容）。</summary>
    public string ToKeywordsJson() => JsonSerializer.Serialize(KeywordWeights, SerializeOptions);

    /// <summary>累加新关键词：逐词权重 +1，返回新实例（不修改原实例）。</summary>
    public PreferenceProfile Merge(IEnumerable<string> newKeywords)
    {
        var dict = KeywordWeights.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kw in newKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            dict[kw] = dict.GetValueOrDefault(kw) + 1;
        }

        return this with { KeywordWeights = dict, UpdatedAt = DateTime.UtcNow };
    }

    /// <summary>按权重降序（并列按 Key 序数序）截断到前 N，结果确定可复现。</summary>
    public PreferenceProfile TrimToTop(int max = MaxKeywords) => this with
    {
        KeywordWeights = KeywordWeights
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(max)
            .ToDictionary(kv => kv.Key, kv => kv.Value)
    };

    /// <summary>取权重最高的 max 个关键词（供推荐上下文注入）。</summary>
    public string[] TopKeywords(int max) =>
        KeywordWeights.OrderByDescending(kv => kv.Value)
                      .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                      .Take(max)
                      .Select(kv => kv.Key)
                      .ToArray();
}
