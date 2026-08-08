using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;

namespace AIShop.AgentTelemetry;

/// <summary>
/// 把 span（<see cref="Activity"/>）追加写入本地文本文件的 OpenTelemetry 导出器。
/// 每个 span 输出为人类可读文本块：时间戳、操作名、耗时（ms）、状态、tag/event 列表。
/// 用于 DebugTelemetry 扩展：抓 HTTP 请求/响应 body 写本地 <c>traces_*.log</c>。
/// 安全约束：body 含敏感信息（Authorization / API key），仅写本地文件，不进 OTLP / Aspire Dashboard。
/// </summary>
public sealed class FileSpanExporter : BaseExporter<Activity>
{
    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>
    /// 初始化 <see cref="FileSpanExporter"/>，日志文件写入 <paramref name="directoryPath"/> 目录下，
    /// 文件命名形如 <c>traces_{yyyyMMdd_HHmmss}_{Guid}.log</c>（同目录多次实例化生成不同文件名，追加互不干扰）。
    /// </summary>
    /// <param name="directoryPath">日志输出目录；为 <see langword="null"/> 时默认 <see cref="AppContext.BaseDirectory"/>。
    /// 测试可注入临时目录便于断言与清理。</param>
    public FileSpanExporter(string? directoryPath = null)
    {
        var dir = directoryPath ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"traces_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.log");
    }

    /// <summary>本实例实际写入的日志文件完整路径（测试断言 / 排查看点）。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 把 batch 中的每个 span 追加写入日志文件。
    /// </summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        // 空 batch 直接成功，不触碰文件（Debug 关闭 / 无 span 时零开销）
        if (batch.Count == 0)
        {
            return ExportResult.Success;
        }

        try
        {
            lock (_lock)
            {
                // 每次打开即追加关闭，无长期持有的文件句柄，也不会跨 batch 截断
                using var writer = new StreamWriter(_filePath, append: true);
                foreach (var activity in batch)
                {
                    WriteActivity(writer, activity);
                }
            }

            return ExportResult.Success;
        }
        catch (Exception)
        {
            // 写盘失败不上抛，避免影响 OTel 导出线程；返回 Failure 由导出器框架兜底
            return ExportResult.Failure;
        }
    }

    private static void WriteActivity(StreamWriter writer, Activity activity)
    {
        var start = activity.StartTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var duration = activity.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

        writer.WriteLine($"[{start}] {activity.OperationName} ({duration}ms) [{activity.Status}]");

        if (!string.IsNullOrEmpty(activity.DisplayName) && activity.DisplayName != activity.OperationName)
        {
            writer.WriteLine($"  DisplayName: {activity.DisplayName}");
        }

        foreach (var tag in activity.Tags)
        {
            writer.WriteLine($"  {tag.Key}: {tag.Value}");
        }

        foreach (var ev in activity.Events)
        {
            writer.WriteLine($"  Event: {ev.Name} @ {ev.Timestamp:HH:mm:ss.fff}");
            foreach (var tag in ev.Tags)
            {
                writer.WriteLine($"    {tag.Key}: {tag.Value}");
            }
        }

        writer.WriteLine();
    }
}
