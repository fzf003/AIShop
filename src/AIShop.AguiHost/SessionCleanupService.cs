using AIShop.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIShop.AguiHost;

/// <summary>
/// 会话过期后台周期清理（agui-session-prod S6，design §5.3 / spec R5）。
/// <see cref="BackgroundService"/> 循环：启动后<b>立即清一次</b> → 按
/// <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 等一个周期 → 再清，直至 <c>stoppingToken</c> 取消。
/// 每轮调用 <see cref="SqliteAgentSessionStore.CleanupExpiredAsync"/>（分批 DELETE）收敛闲置超期的会话快照，
/// 与惰性 TTL（读路径即时兜底）互补，保证过期行最终被清、不无限堆积。
/// </summary>
/// <remarks>
/// <para>
/// <strong>容错语义（spec R5）</strong>：单次清理抛出的非取消异常仅记 <see cref="LogLevel.Warning"/>、
/// <b>不退出循环</b>——对齐 <c>PreferenceWriteHostedService</c> / RAG 预热「失败不致命」语义。存储暂时不可用
/// （SQLite 写锁 / 路径瞬时不可写等）时服务保持存活，下一周期自动重试；若异常导致 <see cref="BackgroundService"/>
/// 退出，宿主不会自动重启它，此后过期清理将永久静默失效，故必须「失败不退出」。
/// <see cref="OperationCanceledException"/>（<c>stoppingToken</c> 取消）不视为错误，正常退出 <see cref="ExecuteAsync"/>。
/// </para>
/// <para>
/// <strong>周期取值</strong>：只读 <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 派生属性
/// （<c>SessionCleanupIntervalHours &lt;= 0</c> 已在选项类归一为默认 12h，避免误配 0 致忙循环），
/// 服务内<b>不再自行判空 / 判 0</b>，避免多处消费行为分叉（handoff-S3 决策 1）。
/// </para>
/// <para>
/// <strong>注册点</strong>：<see cref="AguiServiceCollectionExtensions.AddAguiSessionStore"/> 内
/// <c>services.AddHostedService&lt;SessionCleanupService&gt;()</c>（与 store/options 同一扩展、同生）。
/// 裸 <c>ServiceCollection</c> 未启动 host 时仅完成注册、无任何副作用（不会触发 DB 访问）；
/// 仅当宿主 <c>StartAsync</c> 时才开始后台循环。
/// </para>
/// </remarks>
internal sealed class SessionCleanupService : BackgroundService
{
    /// <summary>单批删除上限（对齐 store 方法默认）；小批量短事务，避免一次性删海量行长时间持 SQLite 锁阻塞会话写入。</summary>
    private const int CleanupBatchSize = 500;

    private readonly SqliteAgentSessionStore _store;
    private readonly IOptions<AguiSessionOptions> _options;
    private readonly ILogger<SessionCleanupService> _logger;

    /// <summary>初始化 <see cref="SessionCleanupService"/>。</summary>
    /// <param name="store">会话持久化 store（提供 <see cref="SqliteAgentSessionStore.CleanupExpiredAsync"/>）。</param>
    /// <param name="options">会话配置（读 <see cref="AguiSessionOptions.EffectiveCleanupInterval"/> 与
    /// <see cref="AguiSessionOptions.SessionTtlDays"/>）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 null。</exception>
    public SessionCleanupService(
        SqliteAgentSessionStore store,
        IOptions<AguiSessionOptions> options,
        ILogger<SessionCleanupService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stoppingToken 取消（清理途中或等待周期中）→ 正常退出，不视为错误（对齐 PreferenceWriteHostedService）。
        }
    }
}
