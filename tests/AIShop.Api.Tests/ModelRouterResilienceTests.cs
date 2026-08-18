using System.Net;
using System.Net.Sockets;
using System.Text;
using AIShop.Api.Agents;
using Microsoft.Extensions.Http.Resilience;

namespace AIShop.Api.Tests;

/// <summary>
/// T11T — ModelRouter.BuildChatHttpPipeline 标准弹性策略测试。
/// 对应方案 C「HTTP 层弹性策略」：自建 HttpClient 接入标准弹性（429/5xx/网络抖动自动重试 + 熔断），
/// DebugHandler 仍包在 ResilienceHandler 内层、包装链不破坏。
///
/// 两个断言维度：
///  1) 行为：本地 stub 首次返回 429（带 Retry-After: 0）、第二次返回 200 → 经 BuildChatHttpPipeline
///     缝构建的 HttpClient 请求成功且 stub 恰好收到 2 次（429 被自动重试，重试后成功）；
///  2) 结构：BuildChatHttpPipeline 返回的 handler 为 ResilienceHandler 且其 InnerHandler 为 DebugHandler
///     （DebugHandler 包装链保留）。
///
/// 本地 stub 复用 ServiceDefaultsDebugTests.HttpStubServer 的 TcpListener 模式，但支持按序返回状态码
/// （首 429 次 200），供真实 HTTP 出站调用验证重试，无需真实 LLM。
/// </summary>
public sealed class ModelRouterResilienceTests
{
    // =========================================================
    // 行为：429 → 自动重试 → 200（对应方案 C「mock 429 → 重试后成功」）
    // =========================================================
    [Fact]
    public async Task ShouldAutoRetry429AndSucceed_WhenStubReturns429Then200()
    {
        await using var stub = new SequenceStubServer(
            (HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}"),
            (HttpStatusCode.OK, "{\"ok\":true}"));

        // 经测试缝构建与生产 CreateChatClient 相同的管线（DebugHandler 默认关闭，不打印）
        using var client = new HttpClient(
            ModelRouter.BuildChatHttpPipeline(new HttpClientHandler(), enableDebugHandler: false))
        {
            Timeout = TimeSpan.FromSeconds(120)   // 生产外层硬天花板同样保留
        };

        using var response = await client.PostAsync(
            $"http://127.0.0.1:{stub.Port}/chat",
            new StringContent("{\"q\":\"hello\"}", Encoding.UTF8, "application/json"));

        // 429 被标准弹性自动重试：最终成功，stub 恰好收到 2 次请求
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.RequestCount);
        Assert.Equal(SequenceStubServer.OkBody, await response.Content.ReadAsStringAsync());
    }

    // =========================================================
    // 结构：ResilienceHandler 外层 + DebugHandler 内层（对应方案 C「不能破坏 DebugHandler 链」）
    // =========================================================
    [Fact]
    public void ShouldReturnResilienceHandlerWithDebugHandlerInner_WhenBuildingPipeline()
    {
        var pipelineHandler = ModelRouter.BuildChatHttpPipeline(new HttpClientHandler(), enableDebugHandler: false);

        // 外层必须是 ResilienceHandler（承载标准弹性策略）
        var resilienceHandler = Assert.IsType<ResilienceHandler>(pipelineHandler);
        // DebugHandler 仍在其内层：每次重试尝试都可见，既有包装链不被破坏
        Assert.IsType<DebugHandler>(resilienceHandler.InnerHandler);
    }

    /// <summary>
    /// 本地 HTTP stub：TcpListener 监听 loopback:0（动态端口），按序返回配置的状态码序列（T11T 用）。
    /// 首请求返回 429（带 Retry-After: 0，立即重试），后续按序返回；供标准弹性重试验证，无需真实 LLM。
    /// 复用 ServiceDefaultsDebugTests.HttpStubServer 的 TcpListener 模式（fire-and-forget 每连接独立处理）。
    /// </summary>
    private sealed class SequenceStubServer : IAsyncDisposable
    {
        public const string OkBody = "{\"ok\":true}";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly (HttpStatusCode Status, string Body)[] _responses;
        private int _requestCount;

        public SequenceStubServer(params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = responses;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        public int Port { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client, _cts.Token);   // 每个连接独立处理（重试会开新连接）
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

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var c = client;
                await using var stream = c.GetStream();
                await ReadRequestAsync(stream, ct);

                // 请求串行（客户端收到响应后才重试开新连接），序号即请求次序
                var index = Interlocked.Increment(ref _requestCount) - 1;
                var (status, body) = _responses[index % _responses.Length];

                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {(int)status} {GetReasonPhrase(status)}\r\n" +
                             $"Content-Type: application/json\r\n" +
                             (status == HttpStatusCode.TooManyRequests ? "Retry-After: 0\r\n" : "") +
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

        private static string GetReasonPhrase(HttpStatusCode status) => status switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            _ => status.ToString(),
        };

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
