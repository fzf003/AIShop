namespace AIShop.AgentTelemetry;

/// <summary>
/// Agent 遥测采集级别，控制是否采集敏感内容（消息内容 / 工具参数 / 工具结果）。
/// 由 <see cref="AgentTelemetry"/> 的 Instrument 方法消费，
/// 内部映射到 MAF <c>OpenTelemetryAgent.EnableSensitiveData</c>。
/// </summary>
public enum AgentTelemetryLevel
{
    /// <summary>不采集，Instrument 裸返回原 Agent，不包装任何装饰器（一次性 / 超高吞吐 / 纯转发 Agent）。</summary>
    None,

    /// <summary>只采集元数据（token、耗时、链路、Agent 信息），生产安全默认。</summary>
    Metadata,

    /// <summary>元数据 + 敏感数据（消息内容、工具参数、结果），调试用。</summary>
    MetadataAndContent,
}
