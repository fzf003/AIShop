namespace AIShop.Service.Agui;

/// <summary>
/// <see cref="SqlChatHistoryProvider"/> 配置（design §6）。默认值即 design 约定：
/// 连接串 <c>Data Source=agui.chat.db</c>、加载上限 2 轮、TTL 30 天、清理批 10 轮、清理周期 12 小时。
/// </summary>
/// <remarks>
/// 与 <see cref="AguiSessionOptions"/> 同层同风格（public sealed + 属性默认值）；design §6 原文写 <c>internal sealed</c>，
/// 本仓库 <c>src/AIShop.Service/Agui/</c> 新约定一律 public（与 <see cref="SqliteAgentSessionStore"/> / <see cref="AguiSessionOptions"/> 一致），
/// 故此处取 public。属性名 = 配置键（去节前缀后），供 ConfigurationBinder 按名绑定。
/// </remarks>
public sealed class SqlChatHistoryOptions
{
    /// <summary>缺省连接串（AguiHost 独立聊天历史库，不得为老 aishop.db）。</summary>
    public const string DefaultConnectionString = "Data Source=agui.chat.db";

    /// <summary>SQLite 连接串（独立聊天历史库）。</summary>
    public string ConnectionString { get; set; } = DefaultConnectionString;

    /// <summary>加载时最多返回几轮（最近 N 个非删除轮）；<c>&lt; 1</c> 时按 1 处理（design §9）。默认 2。</summary>
    public int MaxRoundsToLoad { get; set; } = 2;

    /// <summary>TTL 天数；<c>&lt;= 0</c> 时禁用清理（<see cref="SqlChatHistoryProvider.CleanupExpiredRoundsAsync"/> 直接返回 0）。默认 30。</summary>
    public int TtlDays { get; set; } = 30;

    /// <summary>TTL 清理每批处理的轮数（避免长事务锁表）。默认 10。</summary>
    public int CleanupBatchSize { get; set; } = 10;

    /// <summary>TTL 清理周期（小时）。默认 12。</summary>
    public int CleanupIntervalHours { get; set; } = 12;
}
