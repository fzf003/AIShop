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
    /// DebugTelemetry 扩展：按 <paramref name="debug"/> 开关给 HTTP 层配置 EnrichWith 回调。
    /// <see langword="true"/> 时给 <see cref="HttpClientTraceInstrumentationOptions"/> 配置
    /// <see cref="HttpClientTraceInstrumentationOptions.EnrichWithHttpRequestMessage"/> /
    /// <see cref="HttpClientTraceInstrumentationOptions.EnrichWithHttpResponseMessage"/> 回调，
    /// 抓 <c>System.Net.Http.HttpRequestOut</c> span 的请求/响应 headers 与 body。
    /// body 含敏感信息（Authorization / API key），仅写本地文件，不进 OTLP / Aspire Dashboard。
    /// <see langword="false"/>（默认）时直接返回：不设置任何回调，无额外开销。
    ///
    /// 注意：本方法<b>只设置 EnrichWith 回调，不注册导出 processor</b>——它设计为在
    /// <c>AddHttpClientInstrumentation</c> 的 options 配置闭包内调用，该闭包在 TracerProvider
    /// 构造期间执行，此时调用 <c>builder.AddProcessor</c> 会抛
    /// <see cref="NotSupportedException"/>（"Builder cannot be configured during TracerProvider construction"）。
    /// processor 注册须经 <see cref="AddFileSpanExporter"/> 在 DI 服务注册层（<c>ConfigureOpenTelemetryTracerProvider</c>）完成。
    /// </summary>
    /// <param name="builder"><c>WithTracing</c> 的 <c>tracing</c>（<see cref="TracerProviderBuilder"/>），仅链式透传。</param>
    /// <param name="debug">调试开关；<see langword="false"/> 时零配置返回。</param>
    /// <param name="httpOptions"><c>AddHttpClientInstrumentation</c> 的 options，用于配置 EnrichWith 回调。</param>
    /// <returns>原 <paramref name="builder"/>，便于链式调用。</returns>
    public static TracerProviderBuilder ConfigureDebugTelemetry(
        this TracerProviderBuilder builder,
        bool debug,
        HttpClientTraceInstrumentationOptions httpOptions)
    {
        if (!debug)
        {
            return builder;   // Debug 关闭：零配置，不设置 EnrichWith 回调，无额外 body 读取开销
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

        return builder;
    }

    /// <summary>
    /// DebugTelemetry 扩展：按 <paramref name="debug"/> 开关注册本地文件导出 processor。
    /// <see langword="true"/> 时在 <paramref name="builder"/> 上注册
    /// <see cref="SimpleActivityExportProcessor"/> + <see cref="FileSpanExporter"/>，把抓到的
    /// <c>System.Net.Http.HttpRequestOut</c> span（含请求/响应 body）追加写本地 <c>traces_*.log</c>。
    /// body 含敏感信息（Authorization / API key），仅写本地文件，不进 OTLP / Aspire Dashboard。
    /// <see langword="false"/>（默认）时直接返回：不注册 processor，无额外开销。
    ///
    /// 注意：本方法必须在 <b>DI 服务注册层</b>调用（如 ServiceDefaults 的
    /// <c>builder.Services.ConfigureOpenTelemetryTracerProvider(t => AgentTelemetry.AddFileSpanExporter(t, debug, dir))</c>），
    /// 不能放进 <c>AddHttpClientInstrumentation</c> 的 options 闭包（provider 构造期间禁止二次配置，见
    /// <see cref="ConfigureDebugTelemetry"/>）。
    /// </summary>
    /// <param name="builder">TracerProvider builder（DI 服务注册层的 deferred builder 或 <c>Sdk.CreateTracerProviderBuilder</c>）。</param>
    /// <param name="debug">调试开关；<see langword="false"/> 时零配置返回。</param>
    /// <param name="directory">本地日志目录；<see langword="null"/> 时用 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <returns>原 <paramref name="builder"/>，便于链式调用。</returns>
    public static TracerProviderBuilder AddFileSpanExporter(
        this TracerProviderBuilder builder,
        bool debug,
        string? directory = null)
    {
        if (!debug)
        {
            return builder;   // Debug 关闭：不注册 FileSpanExporter，无额外开销
        }

        builder.AddProcessor(new SimpleActivityExportProcessor(new FileSpanExporter(directory)));

        return builder;
    }
}
