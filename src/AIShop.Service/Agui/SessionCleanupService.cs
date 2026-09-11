using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIShop.Service.Agui;

/// <summary>
/// 会话过期 + 聊天历史过期轮的后台周期清理（agui-session-prod S6 + design-sql-chat-history-provider §5 接线）。
/// <see cref="BackgroundService"/> 循环：启动后<b>立即清一次</b> → 按
/// <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 等一个周期 → 再清，直至 <c>stoppingToken</c> 取消。
/// 每轮依次调用：<see cref="SqliteAgentSessionStore.CleanupExpiredAsync"/>（分批 DELETE 过期会话快照）与
/// <see cref="IChatHistoryCleaner.CleanupExpiredRoundsAsync"/>（整轮软删除过期聊天历史，启用 Sql provider 时为真实实现、
/// 否则为 no-op）——两者与惰性 TTL（读路径即时兜底）互补，保证过期数据最终被清、不无限堆积。
/// </summary>
/// <remarks>
/// <para>
/// <strong>容错语义（spec R5）</strong>：两类清理<b>各自独立 try/catch</b>，单次清理抛出的非取消异常仅记
/// <see cref="LogLevel.Warning"/>、<b>不退出循环</b>——对齐 <c>PreferenceWriteHostedService</c> / RAG 预热「失败不致命」语义。
/// 存储暂时不可用（SQLite 写锁 / 路径瞬时不可写等）时服务保持存活，下一周期自动重试；若异常导致
/// <see cref="BackgroundService"/> 退出，宿主不会自动重启它，此后过期清理将永久静默失效，故必须「失败不退出」。
/// <see cref="OperationCanceledException"/>（<c>stoppingToken</c> 取消）不视为错误，正常退出 <see cref="ExecuteAsync"/>。
/// </para>
/// <para>
/// <strong>周期取值</strong>：只读 <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 派生属性
/// （<c>SessionCleanupIntervalHours &lt;= 0</c> 已在选项类归一为默认 12h，避免误配 0 致忙循环），
/// 服务内<b>不再自行判空 / 判 0</b>，避免多处消费行为分叉（handoff-S3 决策 1）。聊天历史清理<b>共用同一循环节奏</b>：
/// <see cref="SqlChatHistoryOptions"/> 不再保留独立的 <c>CleanupIntervalHours</c>（避免死配置 / 两个节奏源）。
/// </para>
/// <para>
/// <strong>注册点</strong>：<see cref="AguiServiceCollectionExtensions.AddAguiSessionStore"/> 内
/// <c>services.AddHostedService&lt;SessionCleanupService&gt;()</c>（与 store/options 同一扩展、同生）；
/// <see cref="IChatHistoryCleaner"/> 在该扩展 <c>TryAdd</c> no-op 默认、<c>AddAguiChatHistoryProvider</c> 启用时 Replace 为真实实现。
/// 裸 <c>ServiceCollection</c> 未启动 host 时仅完成注册、无任何副作用（不会触发 DB 访问）；
/// 仅当宿主 <c>StartAsync</c> 时才开始后台循环。
/// </para>
/// </remarks>
public sealed class SessionCleanupService : BackgroundService
{
    /// <summary>单批删除上限（对齐 store 方法默认）；小批量短事务，避免一次性删海量行长时间持 SQLite 锁阻塞会话写入。</summary>
    private const int CleanupBatchSize = 500;

    private readonly SqliteAgentSessionStore _store;
    private readonly IChatHistoryCleaner _chatHistoryCleaner;
    private readonly IOptions<AguiSessionOptions> _options;
    private readonly ILogger<SessionCleanupService> _logger;

    /// <summary>初始化 <see cref="SessionCleanupService"/>。</summary>
    /// <param name="store">会话持久化 store（提供 <see cref="SqliteAgentSessionStore.CleanupExpiredAsync"/>）。</param>
    /// <param name="chatHistoryCleaner">聊天历史轮级清理（未启用 Sql provider 时为 no-op 实现，恒可解析）。</param>
    /// <param name="options">会话配置（读 <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 与
    /// <see cref="AguiSessionOptions.SessionTtlDays"/>）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 null。</exception>
    public SessionCleanupService(
        SqliteAgentSessionStore store,
        IChatHistoryCleaner chatHistoryCleaner,
        IOptions<AguiSessionOptions> options,
        ILogger<SessionCleanupService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _chatHistoryCleaner = chatHistoryCleaner ?? throw new ArgumentNullException(nameof(chatHistoryCleaner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 后台清理循环：启动即清一次 → 等一个周期 → 再清，循环至 <paramref name="stoppingToken"/> 取消。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌；取消时干净退出（不抛异常）。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 周期与 TTL 一次性读取：选项为容器构建时绑定完成的静态配置，循环内无需重复读。
        TimeSpan interval = _options.Value.EffectiveCleanupInterval;
        int ttlDays = _options.Value.SessionTtlDays;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 启动后【立即清一次】（无需等首个周期）。ttlDays <= 0 时 store 方法内直接返回 0（禁用 TTL）。
                    await _store.CleanupExpiredAsync(ttlDays, CleanupBatchSize, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单次清理失败仅告警、【不退出循环】（spec R5 场景 2）：服务保持存活，下一周期自动重试。
                    _logger.LogWarning(ex, "会话过期清理失败，服务保持存活、将在下一周期重试（{Interval}）", interval);
                }

                try
                {
                    // 聊天历史轮级 TTL 软删除（design-sql-chat-history-provider §5）：启用 Sql provider 时为真实实现，
                    // 否则 no-op 返回 0。仅在有实际删除时打一条 Information，避免每周期噪音（对齐会话清理的静默语义）。
                    int deletedRounds = await _chatHistoryCleaner.CleanupExpiredRoundsAsync(stoppingToken).ConfigureAwait(false);
                    if (deletedRounds > 0)
                        _logger.LogInformation("聊天历史过期轮清理完成，共软删除 {DeletedRounds} 轮", deletedRounds);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 与上方会话清理同样的「失败不退出」：聊天历史清理异常独立捕获，不掀掉整个服务、也不影响会话清理。
                    _logger.LogWarning(ex, "聊天历史过期轮清理失败，服务保持存活、将在下一周期重试（{Interval}）", interval);
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stoppingToken 取消（清理途中或等待周期中）→ 正常退出，不视为错误（对齐 PreferenceWriteHostedService）。
        }
    }
}
