using Microsoft.Agents.AI;

namespace AIShop.AgentTelemetry;

/// <summary>
/// Agent 遥测装配助手：复刻微软官方 <c>microsoft-agent-integration</c> 的 AgentTelemetry 模式，
/// 用 MAF 内建 <c>OpenTelemetryAgent</c> 装饰 Agent，开启内容采集（消息内容 / 工具参数 / 工具结果）。
///
/// 裁剪原则：AIShop 已通过 <c>AddServiceDefaults()</c> 配置 OTLP 导出到 Aspire，
/// 故不引入官方 Sample 的 <c>CreateTracerProvider</c> / <c>FileSpanExporter</c> 导出层。
/// 不自建 ActivitySource、不自写 span 属性，全部可观测数据来自 MAF 内建埋点。
/// </summary>
public static class AgentTelemetry
{
    /// <summary>默认 source name，与 MAF 内建 <c>OpenTelemetryAgent</c> 默认一致。</summary>
    public const string DefaultSourceName = "Experimental.Microsoft.Agents.AI";

    /// <summary>
    /// 按指定级别给 Agent 加 OpenTelemetry 装饰器。
    /// </summary>
    /// <param name="agent">裸 Agent 实例。</param>
    /// <param name="sourceName">OTel source name；<see langword="null"/> 时用框架默认。</param>
    /// <param name="level">采集级别。</param>
    /// <returns>装配好遥测的 Agent。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> 为 <see langword="null"/>。</exception>
    public static AIAgent Instrument(AIAgent agent, string? sourceName, AgentTelemetryLevel level)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (level == AgentTelemetryLevel.None)
        {
            return agent;   // 不包装饰器，裸返回
        }

        return agent.AsBuilder()
            .UseOpenTelemetry(
                sourceName: sourceName,
                configure: cfg => cfg.EnableSensitiveData = level == AgentTelemetryLevel.MetadataAndContent)
            .Build();
    }
}
