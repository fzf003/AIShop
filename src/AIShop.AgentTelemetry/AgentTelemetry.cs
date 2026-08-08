using Microsoft.Agents.AI;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Trace;

namespace AIShop.AgentTelemetry;

/// <summary>
/// Agent 遥测装配助手：复刻微软官方 <c>microsoft-agent-integration</c> 的 AgentTelemetry 模式，
/// 用 MAF 内建 <c>OpenTelemetryAgent</c> 装饰 Agent，开启内容采集（消息内容 / 工具参数 / 工具结果）；
/// 并提供 DebugTelemetry 扩展：按 <c>AgentTelemetry:Debug</c> 开关抓 HTTP 请求/响应头与 body 写本地 <c>traces_*.log</c>。
///
/// 裁剪原则：AIShop 已通过 <c>AddServiceDefaults()</c> 配置 OTLP 导出到 Aspire，
/// 故不引入官方 Sample 的 <c>CreateTracerProvider</c> OTLP 导出部分；仅引入 <c>FileSpanExporter</c> 用于
/// DebugTelemetry 的本地 body 日志（body 含敏感信息，只写本地文件，不进 OTLP / Aspire Dashboard）。
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

    /// <summary>
    /// DebugTelemetry 扩展：按 <paramref name="debug"/> 开关配置 HTTP 层调试遥测。
    /// <see langword="true"/> 时给 <see cref="HttpClientTraceInstrumentationOptions"/> 配置
    /// <see cref="HttpClientTraceInstrumentationOptions.EnrichWithHttpRequestMessage"/> /
    /// <see cref="HttpClientTraceInstrumentationOptions.EnrichWithHttpResponseMessage"/> 回调，
    /// 抓 <c>System.Net.Http.HttpRequestOut</c> span 的请求/响应 headers 与 body，并注册
    /// <see cref="SimpleActivityExportProcessor"/> + <see cref="FileSpanExporter"/> 写本地 <c>traces_*.log</c>。
    /// body 含敏感信息（Authorization / API key），仅写本地文件，不进 OTLP / Aspire Dashboard。
    /// <see langword="false"/>（默认）时直接返回：不设置任何回调、不注册 processor，无额外开销。
    /// </summary>
    /// <param name="builder"><c>WithTracing</c> 的 <c>tracing</c>（<see cref="TracerProviderBuilder"/>），用于注册导出 processor。</param>
    /// <param name="debug">调试开关；<see langword="false"/> 时零配置返回。</param>
    /// <param name="httpOptions"><c>AddHttpClientInstrumentation</c> 的 options，用于配置 EnrichWith 回调。</param>
    /// <param name="directory">本地日志目录；<see langword="null"/> 时用 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <returns>原 <paramref name="builder"/>，便于链式调用。</returns>
    public static TracerProviderBuilder ConfigureDebugTelemetry(
        this TracerProviderBuilder builder,
        bool debug,
        HttpClientTraceInstrumentationOptions httpOptions,
        string? directory = null)
    {
        if (!debug)
        {
            return builder;   // Debug 关闭：零配置，不设置 EnrichWith 回调、不注册 FileSpanExporter，无额外 body 读取开销
        }

        httpOptions.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            activity.SetTag("http.request.headers", request.Headers.ToString());
            if (request.Content is not null)
            {
                activity.SetTag("http.request.content.headers", request.Content.Headers.ToString());
                activity.SetTag("http.request.content.body", request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        };

        httpOptions.EnrichWithHttpResponseMessage = (activity, response) =>
        {
            activity.SetTag("http.response.headers", response.Headers.ToString());
            if (response.Content is not null)
            {
                activity.SetTag("http.response.content.headers", response.Content.Headers.ToString());
                activity.SetTag("http.response.content.body", response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        };

        builder.AddProcessor(new SimpleActivityExportProcessor(new FileSpanExporter(directory)));

        return builder;
    }
}
