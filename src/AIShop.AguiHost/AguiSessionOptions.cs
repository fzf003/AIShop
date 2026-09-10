namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 会话存储配置（agui-session-prod S3）。绑定 "Agui" 配置节
/// （见 <see cref="AguiServiceCollectionExtensions.AddAguiSessionStore"/> 内 <c>Configure&lt;AguiSessionOptions&gt;</c>）：
/// <c>Agui:SessionTtlDays</c> / <c>Agui:SessionCleanupIntervalHours</c> / <c>Agui:SessionMaxRounds</c>。
/// 未提供的键回退类默认 30 / 12 / 12（spec R7「配置生效与缺省回退」）。
/// </summary>
/// <remarks>
/// 语义辅助：<see cref="IsTtlEnabled"/>（<c>SessionTtlDays &lt;= 0</c> = 禁用 TTL，既不后台删除也不惰性过期）与
/// <see cref="EffectiveCleanupInterval"/>（<c>SessionCleanupIntervalHours &lt;= 0</c> 时回退默认 12h，避免误设为 0 忙循环）。
/// 属性名与配置键（去掉节前缀后）同名，供 ConfigurationBinder 按名绑定。
/// </remarks>
internal sealed class AguiSessionOptions
{
    /// <summary>会话闲置生存天数默认值（30 天）。</summary>
    internal const int DefaultSessionTtlDays = 30;

    /// <summary>后台清理周期默认值（12 小时）。</summary>
    internal const int DefaultSessionCleanupIntervalHours = 12;

    /// <summary>收敛快照保留轮数默认硬上限（12 轮，口径对齐老 RoundBasedCompactionPolicy K=12）。</summary>
    internal const int DefaultSessionMaxRounds = 12;

    /// <summary>
    /// <c>updated_at</c> 闲置超过该天数即视为过期；<c>&lt;= 0</c> = <b>禁用 TTL</b>（不后台删除、不惰性过期）。
    /// 绑定键 <c>Agui:SessionTtlDays</c>。
    /// </summary>
    public int SessionTtlDays { get; set; } = DefaultSessionTtlDays;

    /// <summary>
    /// 后台扫描周期（小时）；<c>&lt;= 0</c> 时由 <see cref="EffectiveCleanupInterval"/> 回退默认 12h。
    /// 绑定键 <c>Agui:SessionCleanupIntervalHours</c>。
    /// </summary>
    public int SessionCleanupIntervalHours { get; set; } = DefaultSessionCleanupIntervalHours;

    /// <summary>
    /// 收敛快照保留轮数硬上限；超出时从最旧轮整轮丢弃（受保护的最近轮除外）。
    /// 绑定键 <c>Agui:SessionMaxRounds</c>。
    /// </summary>
    public int SessionMaxRounds { get; set; } = DefaultSessionMaxRounds;

    /// <summary>TTL 是否启用（<see cref="SessionTtlDays"/> &gt; 0）。</summary>
    internal bool IsTtlEnabled => SessionTtlDays > 0;

    /// <summary>生效的清理周期：<see cref="SessionCleanupIntervalHours"/> &lt;= 0 回退默认 12h。</summary>
    internal TimeSpan EffectiveCleanupInterval => TimeSpan.FromHours(
        SessionCleanupIntervalHours > 0 ? SessionCleanupIntervalHours : DefaultSessionCleanupIntervalHours);
}
