using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AIShop.AgentTelemetry;
using AIShop.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AIShop.Api.Tests;

/// <summary>
/// T26 — ServiceDefaults 按 AgentTelemetry:Debug 配置启用/关闭 Debug body 抓取（集成测试）。
/// 对应 spec「Debug 开关默认关闭」「Debug 开启时抓取 HTTP 请求/响应 body 写本地文件」
/// 「Debug 关闭时无额外开销」「body 只写本地文件不进 OTLP」「FileSpanExporter 输出人类可读文本块」。
///
/// 直接用 <see cref="Host.CreateApplicationBuilder"/> + <c>AddServiceDefaults()</c> 构建真实宿主
/// （ServiceDefaults 的规范用法，IHostApplicationBuilder 泛型），避免 WebApplicationFactory 的
/// 配置覆盖时序问题；出站调用用真实 HttpClient 打本地 TcpListener stub，经 HttpClientInstrumentation
/// 产生 System.Net.Http.HttpRequestOut span。
///
/// 三个断言维度：
///  1) Debug=false（默认）：不注册 FileSpanExporter / BodyRedactionProcessor，出站调用不产生 traces_*.log；
///  2) Debug=true：处理器链为 [SimpleActivityExportProcessor(FileSpanExporter), BodyRedactionProcessor]，
///     出站调用后 traces_*.log 含请求/响应 body；且 provider 上无 OTLP exporter（测试环境未配端点），
///     处理器顺序 FileSpanExporter 先落盘、BodyRedactionProcessor 后脱敏 → body 不进 OTLP；
///  3) BodyRedactionProcessor 单测：敏感 tag 被覆盖为占位符、标准 http.* 属性保留。
///
/// 日志目录沿用生产配置 AppContext.BaseDirectory（FileSpanExporter 默认路径），
/// 用「快照前后对比」定位新建 traces_*.log，并在 finally 删除（development-flow「测试资源清理」）。
/// </summary>
public sealed class ServiceDefaultsDebugTests
{
    private static readonly string TracesDir = AppContext.BaseDirectory;

    // =========================================================
    // Debug=false（默认）：不产生 traces_*.log、无 body 抓取
    // 对应 spec「Debug 开关默认关闭」+「Debug 关闭时无额外开销」。
    // 不配置 AgentTelemetry:Debug → ServiceDefaults 惰性读到 false，不配 EnrichWith、不注册 FileSpanExporter。
    // =========================================================
    [Fact]
    public async Task ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse()
    {
        var builder = Host.CreateApplicationBuilder();
        // 显式声明 Debug=false：WebApplicationFactory 引用 Api 项目时，其 appsettings.json（AgentTelemetry:Debug=true）
        // 会被复制到测试输出目录，Host.CreateApplicationBuilder() 会读到 true → 误注册 FileSpanExporter。
        // 测试意图是「Debug 关闭时零开销」，故显式覆盖前置条件，不依赖环境默认。
        builder.Configuration["AgentTelemetry:Debug"] = "false";
        builder.AddServiceDefaults();
        using var host = builder.Build();
        await host.StartAsync();

        var provider = host.Services.GetRequiredService<TracerProvider>();
        var processors = WalkProcessors(provider).ToList();

        // Debug=false：不注册 FileSpanExporter / BodyRedactionProcessor（零配置、零额外开销）
        Assert.DoesNotContain(processors, p => p is SimpleActivityExportProcessor);
        Assert.DoesNotContain(processors, p => p is BodyRedactionProcessor);
        Assert.Empty(FindExporters(provider).ToList());   // 无 FileSpanExporter，也无 OTLP（未配端点）

        var before = SnapshotTracesFiles();

        // 出站调用：真实 HttpClient 打本地 stub（经 HttpClientInstrumentation 产生 HttpRequestOut span，
        // 但 Debug=false 不读 body、不写本地文件）
        await using var stub = new HttpStubServer();
        using var client = new HttpClient();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{stub.Port}/chat",
            new StringContent("{\"q\":\"secret-request-body\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Task.Delay(200);   // 留出 span 处理缓冲
        provider.ForceFlush();

        var newFiles = SnapshotTracesFiles().Except(before).ToList();
        Assert.Empty(newFiles);

        await host.StopAsync();
    }

    // =========================================================
    // Debug=true：出站调用后本地日志含请求/响应 headers，且不抓 body、headers 不进 OTLP
    // 对应 spec「Debug 开启时抓取 HTTP 请求/响应头写本地文件」+「headers 只写本地文件不进 OTLP」。
    // 修复点：Enrich 回调不再抓 body（回调是 void 委托，async lambda 为 fire-and-forget 并发执行，
    // 抓 response body 会与 DeepSeek 直连路径冲突 → "The stream was already consumed"）；
    // HTTP body 由 Api 侧 DebugHandler 读取打印。
    // 配置 AgentTelemetry:Debug=true（ServiceDefaults 惰性读取 → 无需重编译仅改配置生效）。
    // =========================================================
    [Fact]
    public async Task ShouldWriteHeaderTagsToLocalLog_AndRedactFromOtlp_WhenDebugTrue()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["AgentTelemetry:Debug"] = "true";
        builder.AddServiceDefaults();
        using var host = builder.Build();
        await host.StartAsync();

        var provider = host.Services.GetRequiredService<TracerProvider>();

        // 处理器链：FileSpanExporter（SimpleActivityExportProcessor）先、BodyRedactionProcessor 后
        var processors = WalkProcessors(provider).ToList();
        var fileProcIndex = processors.FindIndex(p => p is SimpleActivityExportProcessor);
        var redactionIndex = processors.FindIndex(p => p is BodyRedactionProcessor);
        Assert.True(fileProcIndex >= 0, "Debug=true 应注册 SimpleActivityExportProcessor(FileSpanExporter)");
        Assert.True(redactionIndex > fileProcIndex,
            $"BodyRedactionProcessor 应在 FileSpanExporter 之后注册（先本地落盘、再脱敏）。实际索引 file={fileProcIndex}, redact={redactionIndex}");

        // provider 上无 OTLP exporter（测试环境未配 OTEL_EXPORTER_OTLP_ENDPOINT）→ body 不出进程
        var exporters = FindExporters(provider).ToList();
        Assert.Contains(exporters, e => e is FileSpanExporter);
        Assert.DoesNotContain(exporters, e => e.GetType().Name.Contains("Otlp", StringComparison.OrdinalIgnoreCase));

        var before = SnapshotTracesFiles();

        // 出站调用：触发 System.Net.Http.HttpRequestOut span，FileSpanExporter 同步落盘抓到的 headers tag
        const string requestBody = "{\"q\":\"你好\"}";
        await using var stub = new HttpStubServer();
        using var client = new HttpClient();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{stub.Port}/chat",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var logFile = Assert.Single(SnapshotTracesFiles().Except(before).ToList());
        try
        {
            var content = await WaitForFileContentAsync(logFile);
            Assert.Contains("System.Net.Http.HttpRequestOut", content);
            // Enrich 抓 request body + headers；不抓 response body（避免并发读同一响应流冲突 → already consumed）：
            Assert.Contains("http.request.headers", content);
            Assert.Contains("http.response.headers", content);
            Assert.Contains(requestBody, content);                        // request body 保留（body 放行进 OTLP）
            Assert.DoesNotContain(HttpStubServer.ResponseBody, content);  // response body 不抓
        }
        finally
        {
            File.Delete(logFile);   // development-flow「测试资源清理」
        }

        await host.StopAsync();
    }

    // =========================================================
    // BodyRedactionProcessor 单测：敏感 tag 覆盖为占位符、标准 http.* 属性保留
    // 对应 spec「body 只写本地文件不进 OTLP」的脱敏机制。
    // 注意：Activity.SetTag 仅保留 string 值（int 等非 string 值枚举时被丢弃），
    // 而 Debug 抓取的 body/headers 均为 string，覆盖为占位符后枚举可见。
    // =========================================================
    [Fact]
    public void ShouldRedactHeaderTags_ButKeepBodyTags_WhenOnEnd()
    {
        using var activity = new Activity("System.Net.Http.HttpRequestOut");
        // 标准 OTel http 属性（不含敏感信息）：必须保留
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.response.status_code", "429");
        // Debug 抓取的敏感 headers（含 Authorization / API key）：必须被覆盖为占位符
        activity.SetTag("http.request.headers", "Authorization: Bearer secret-key");
        activity.SetTag("http.request.content.headers", "Content-Type: application/json");
        activity.SetTag("http.response.headers", "X-Request-Id: req-123");
        activity.SetTag("http.response.content.headers", "Content-Type: application/json");
        // body tag 按需求放行（用于排查；request body 为对话内容、无 API key）：
        activity.SetTag("http.request.content.body", "{\"q\":\"你好\"}");
        activity.SetTag("http.response.content.body", "{\"error\":\"rate limit\"}");

        new BodyRedactionProcessor().OnEnd(activity);

        var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
        // headers（含 Authorization / API key）被覆盖为占位符，真实 key 不出进程
        Assert.Equal(BodyRedactionProcessor.Redacted, tags["http.request.headers"]);
        Assert.Equal(BodyRedactionProcessor.Redacted, tags["http.request.content.headers"]);
        Assert.Equal(BodyRedactionProcessor.Redacted, tags["http.response.headers"]);
        Assert.Equal(BodyRedactionProcessor.Redacted, tags["http.response.content.headers"]);
        // body 放行：保留原文进 OTLP / Aspire Dashboard（用于排查）
        Assert.Equal("{\"q\":\"你好\"}", tags["http.request.content.body"]);
        Assert.Equal("{\"error\":\"rate limit\"}", tags["http.response.content.body"]);
        // 标准 http.* 属性保留原值
        Assert.Equal("POST", tags["http.request.method"]);
        Assert.Equal("429", tags["http.response.status_code"]);
    }

    // =========================================================
    // 辅助
    // =========================================================

    /// <summary>快照当前 traces_*.log 集合（前后对比定位本测试新建文件）。</summary>
    private static HashSet<string> SnapshotTracesFiles()
        => Directory.EnumerateFiles(TracesDir, "traces_*.log")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>等待日志文件生成并返回内容（FileSpanExporter 同步落盘，留小缓冲避免时序抖动）。</summary>
    private static async Task<string> WaitForFileContentAsync(string path, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    return await File.ReadAllTextAsync(path);
                }
                catch (IOException)
                {
                    // 文件被写出进程短暂占用，重试
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"traces_*.log 未在 {timeoutMs}ms 内生成：{path}");
    }

    /// <summary>
    /// 遍历 TracerProvider 处理器链（CompositeProcessor 的 DoublyLinkedList）。
    /// 依赖 OTel 1.15.2 内部结构（&lt;Processor&gt;k__BackingField / Head / Value / &lt;Next&gt;k__BackingField），
    /// 版本已由 csproj 锁定；若结构变动测试会显式失败而非静默通过。
    /// </summary>
    private static IEnumerable<BaseProcessor<Activity>> WalkProcessors(TracerProvider provider)
    {
        var processorField = provider.GetType()
            .GetField("<Processor>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"TracerProviderSdk.Processor 字段不存在：{provider.GetType()}");
        if (processorField.GetValue(provider) is not BaseProcessor<Activity> root)
        {
            yield break;
        }

        if (root is not CompositeProcessor<Activity> composite)
        {
            yield return root;
            yield break;
        }

        var headField = composite.GetType()
            .GetField("Head", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("CompositeProcessor.Head 字段不存在");
        var node = headField.GetValue(composite);
        var seen = new HashSet<object>();
        while (node is not null && seen.Add(node))
        {
            var nodeType = node.GetType();
            var valueField = nodeType.GetField("Value", BindingFlags.Instance | BindingFlags.Public);
            if (valueField?.GetValue(node) is BaseProcessor<Activity> processor)
            {
                yield return processor;
            }
            var nextField = nodeType.GetField("<Next>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? nodeType.GetField("Next", BindingFlags.Instance | BindingFlags.Public);
            node = nextField?.GetValue(node);
        }
    }

    /// <summary>收集 TracerProvider 上所有 exporter（从各 processor 的 exporter 字段读取）。</summary>
    private static IEnumerable<BaseExporter<Activity>> FindExporters(TracerProvider provider)
    {
        foreach (var processor in WalkProcessors(provider))
        {
            var exporterField = processor.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(f => typeof(BaseExporter<Activity>).IsAssignableFrom(f.FieldType));
            if (exporterField?.GetValue(processor) is BaseExporter<Activity> exporter)
            {
                yield return exporter;
            }
        }
    }

    /// <summary>
    /// 本地 HTTP stub 服务：TcpListener 监听 loopback:0（动态端口），读取完整请求后返回固定 JSON 响应。
    /// 供「真实 HTTP 出站调用」使用——经真实 HttpClientHandler 产生 HttpRequestOut span，无需真实 LLM。
    /// </summary>
    private sealed class HttpStubServer : IAsyncDisposable
    {
        public const string ResponseBody = "{\"ok\":true}";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public HttpStubServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client, _cts.Token);   // 每个连接独立处理
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    client?.Dispose();   // 瞬时 accept 错误：忽略继续
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var c = client;
                await using var stream = c.GetStream();
                await ReadRequestAsync(stream, ct);

                var bodyBytes = Encoding.UTF8.GetBytes(ResponseBody);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                             $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                byte[] payload = new byte[headerBytes.Length + bodyBytes.Length];
                Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
                Buffer.BlockCopy(bodyBytes, 0, payload, headerBytes.Length, bodyBytes.Length);
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
            }
            catch
            {
                // 客户端中断：忽略
            }
        }

        /// <summary>读取完整 HTTP 请求（headers + Content-Length 指定的 body），确保请求体被消费。</summary>
        private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var received = new MemoryStream();
            while (true)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    return;
                }
                await received.WriteAsync(buffer.AsMemory(0, n), ct);

                var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    continue;
                }

                int headerLength = headerEnd + 4;
                int contentLength = ParseContentLength(text[..headerEnd]);
                int bodyRead = (int)received.Length - headerLength;
                while (contentLength > 0 && bodyRead < contentLength)
                {
                    int m = await stream.ReadAsync(buffer, ct);
                    if (m == 0)
                    {
                        return;
                    }
                    await received.WriteAsync(buffer.AsMemory(0, m), ct);
                    bodyRead += m;
                }
                return;
            }
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                int idx = line.IndexOf(':');
                if (idx > 0 && line[..idx].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(idx + 1)..].Trim(), out int length))
                {
                    return length;
                }
            }
            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // accept 循环已停止
            }
            _cts.Dispose();
        }
    }
}
