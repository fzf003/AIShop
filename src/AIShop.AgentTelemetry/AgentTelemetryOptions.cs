namespace AIShop.AgentTelemetry;

/// <summary>
/// Agent 遥测配置绑定 POCO，对应 appsettings.json 的 "AgentTelemetry" 配置节。
/// </summary>
public sealed class AgentTelemetryOptions
{
    /// <summary>采集级别，默认 <see cref="AgentTelemetryLevel.Metadata"/>（生产安全默认，不含消息内容）。</summary>
    public AgentTelemetryLevel Level { get; set; } = AgentTelemetryLevel.Metadata;

    /// <summary>OTel source name；为 null 时使用 MAF 框架默认 <c>Experimental.Microsoft.Agents.AI</c>。</summary>
    public string? SourceName { get; set; }

    /// <summary>调试模式开关，默认 <see langword="false"/>（生产安全）。开启时抓 HTTP 请求/响应头与 body 写本地 <c>traces_*.log</c>（仅本地文件，不进 OTLP / Aspire Dashboard）。</summary>
    public bool Debug { get; set; } = false;
}
