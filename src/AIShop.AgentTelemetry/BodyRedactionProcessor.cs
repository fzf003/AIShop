using System.Diagnostics;
using OpenTelemetry;

namespace AIShop.AgentTelemetry;

/// <summary>
/// DebugTelemetry 扩展：在 OTLP 导出前把 span 上 Debug 抓取的 HTTP 请求/响应 headers tag
/// 值替换为占位符，保证含 Authorization / API key 的 header 不出进程。
///
/// 安全约束：Debug=true 时 EnrichWith 回调把含敏感信息的请求/响应 headers 写入
/// <c>System.Net.Http.HttpRequestOut</c> span。同一 span 会流经所有 processor，若不脱敏，
/// 这些 tag 会随 OTLP 导出到 Aspire Dashboard——违反「header 只写本地文件不进 OTLP」。（body tag 按需求放行进 OTLP / Aspire Dashboard 用于排查：request body 为对话内容、无 API key）
///
/// .NET 的 <see cref="Activity"/> 不支持移除 tag（一旦 SetTag 只能覆盖），故用
/// <see cref="Activity.SetTag(string, object?)"/> 把敏感 tag 值覆盖为 <see cref="Redacted"/> 占位符。
/// 本 processor 必须注册在 <see cref="FileSpanExporter"/>（SimpleActivityExportProcessor）<b>之后</b>、
/// OTLP exporter <b>之前</b>（见 ServiceDefaults 接线）：FileSpanExporter 先同步落盘真实 headers，
/// 本 processor 再覆盖值，OTLP 拿到的只有占位符。
/// 仅覆盖 Debug 抓取的 4 个 headers tag（body 放行），保留 HttpClientInstrumentation 产出的标准 <c>http.*</c> 属性
/// （method / status_code 等，不含敏感信息）。
/// </summary>
public sealed class BodyRedactionProcessor : BaseProcessor<Activity>
{
    /// <summary>敏感 tag 被覆盖后的占位符值。</summary>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// Debug 抓取的敏感 tag：请求/响应 headers（含 Authorization / API key）需要脱敏。
    /// body tag（http.request.content.body / http.response.content.body）按需求【放行】——
    /// 保留原文进 OTLP / Aspire Dashboard 用于排查（request body 为对话内容、无 API key）。
    /// </summary>
    private static readonly string[] SensitiveTags =
    [
        "http.request.headers",
        "http.request.content.headers",
        "http.response.headers",
        "http.response.content.headers",
    ];

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        foreach (var tag in SensitiveTags)
        {
            activity.SetTag(tag, Redacted);
        }
    }
}
