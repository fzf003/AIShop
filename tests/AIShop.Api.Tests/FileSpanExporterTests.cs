using System.Diagnostics;
using System.Text.RegularExpressions;
using AIShop.AgentTelemetry;
using OpenTelemetry;

namespace AIShop.Api.Tests;

/// <summary>
/// T24 — FileSpanExporter 输出格式与追加写。
/// 对应 spec「FileSpanExporter 输出人类可读文本块」：时间戳 / 操作名 / 耗时 ms / 状态 / tag/event 列表，追加写。
///
/// 驱动方式：FileSpanExporter.Export 为 protected internal（继承自 BaseExporter<Activity>），
/// 测试经 SimpleActivityExportProcessor 的公开入口 OnEnd / ForceFlush 驱动落盘；
/// 构造函数注入临时目录便于断言与清理（T18 的决策点）。
/// 测试结束删除生成的日志文件与临时目录（development-flow「测试资源清理」）。
/// </summary>
public sealed class FileSpanExporterTests : IDisposable
{
    private readonly string _tempDir;

    public FileSpanExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fses_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // 清理临时目录（含其中生成的 traces_*.log）：失败不遮蔽测试结论
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败（如文件被占用）不遮蔽测试结论，仅残留临时目录
            }
        }
    }

    [Fact]
    public void ShouldWriteHumanReadableTextBlock_WithAllFields()
    {
        using var exporter = new FileSpanExporter(_tempDir);
        using var processor = new SimpleActivityExportProcessor(exporter);

        // 固定起止时间使耗时完全确定（250ms → "250.0ms"），避免时钟波动导致断言抖动
        var start = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        using var activity = CreateStartedActivity("test.http.op", start, start.AddMilliseconds(250));
        activity.DisplayName = "Test Display Name";
        activity.AddTag("http.request.content.body", "{\"q\":\"你好\"}");
        activity.AddEvent(new ActivityEvent("exception",
            new DateTimeOffset(start.AddMilliseconds(200)),
            new ActivityTagsCollection { ["exception.type"] = "HttpRequestException" }));
        activity.SetStatus(ActivityStatusCode.Error, "HTTP 429 Too Many Requests");

        // Act：OnEnd（simple processor 每次同步导出）+ ForceFlush 确保落盘
        processor.OnEnd(activity);
        processor.ForceFlush();

        // Assert：目录下生成命名匹配 traces_{yyyyMMdd_HHmmss}_{Guid}.log 的日志文件，且与本实例 FilePath 一致
        var logFile = Assert.Single(Directory.GetFiles(_tempDir, "traces_*.log"));
        Assert.Matches(@"traces_\d{8}_\d{6}_[0-9a-f]{32}\.log", Path.GetFileName(logFile));
        Assert.Equal(exporter.FilePath, logFile);

        var content = File.ReadAllText(logFile);

        // 文本块表头：时间戳 / 操作名 / 耗时 ms / 状态（\r? 兼容 Windows CRLF 行尾）
        Assert.True(Regex.IsMatch(content,
            @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] test\.http\.op \(250\.0ms\) \[Error\]\r?$",
            RegexOptions.Multiline),
            $"表头格式不符。实际内容：\n{content}");

        // DisplayName（与操作名不同时输出）
        Assert.Contains("DisplayName: Test Display Name", content);
        // tag
        Assert.Contains("http.request.content.body: {\"q\":\"你好\"}", content);
        // event 及 event tag
        Assert.Contains("Event: exception", content);
        Assert.Contains("exception.type: HttpRequestException", content);
    }

    [Fact]
    public void ShouldAppendMultipleSpans_WhenMultipleOnEnd()
    {
        using var exporter = new FileSpanExporter(_tempDir);
        using var processor = new SimpleActivityExportProcessor(exporter);

        var baseTime = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        using var first = CreateStartedActivity("span.first", baseTime, baseTime.AddMilliseconds(100));
        using var second = CreateStartedActivity("span.second", baseTime.AddMilliseconds(1000), baseTime.AddMilliseconds(1200));

        processor.OnEnd(first);
        processor.OnEnd(second);
        processor.ForceFlush();

        var logFile = Assert.Single(Directory.GetFiles(_tempDir, "traces_*.log"));
        var content = File.ReadAllText(logFile);

        // 追加写：第二个 span 的文本块跟在第一个之后（非覆盖）
        var firstIndex = content.IndexOf("span.first", StringComparison.Ordinal);
        var secondIndex = content.IndexOf("span.second", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"应写入第一个 span。实际内容：\n{content}");
        Assert.True(secondIndex > firstIndex, $"第二个 span 应追加在第一个之后（非覆盖）。实际内容：\n{content}");

        // 两个 span 两个文本块：以时间戳开头的表头行出现两次
        var blockCount = Regex.Matches(
            content,
            @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] span\.(first|second)",
            RegexOptions.Multiline).Count;
        Assert.Equal(2, blockCount);
    }

    /// <summary>
    /// 通过 ActivitySource + ActivityListener 启动一个已采样（Recorded）的 Activity。
    /// 关键点：SimpleActivityExportProcessor 只导出 <c>Activity.Recorded == true</c> 的 span
    /// （BaseExportProcessor.OnEnd 按采样结果过滤），裸 <c>new Activity()</c> 不带 Recorded 标记会被丢弃，
    /// 因此必须经 ActivityListener 以 AllDataAndRecorded 采样启动。
    /// 固定起止时间使 Duration 确定，便于断言耗时 ms。
    /// </summary>
    private static Activity CreateStartedActivity(string operationName, DateTime start, DateTime end)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("AIShop.FileSpanExporterTests");
        var activity = source.StartActivity(operationName, ActivityKind.Client)!;
        activity.SetStartTime(start);
        activity.SetEndTime(end);
        return activity;
    }
}
