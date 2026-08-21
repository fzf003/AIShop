using AIShop.Service.Clients;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace AIShop.Service.Tests;

/// <summary>
/// R10/R10.1 — DeepSeekChatClient 实现 IChatClient.Metadata 并经 GetService 暴露：
/// 修 DeepSeek gen_ai 遥测属性（gen_ai.provider.name / gen_ai.request.model）为空——
/// 旧主构造函数未实现 Metadata，接口默认 Metadata 为空对象；R10 实现 Metadata，
/// R10.1 经 GetService(typeof(ChatClientMetadata)) 暴露（MEAI 埋点从 GetService 读 provider.name）。
/// 注：10.8.3 的属性名为 DefaultModelId（非 ModelId），断言按实际契约。
/// </summary>
public sealed class DeepSeekChatClientTests
{
    [Fact]
    public void Metadata_ReturnsProviderAndDefaultModelId()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        Assert.Equal("DeepSeek", client.Metadata.ProviderName);
        Assert.Equal("deepseek-v4-flash", client.Metadata.DefaultModelId);
    }

    [Fact]
    public void GetService_ReturnsMetadata_ForChatClientMetadata()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        Assert.NotNull(metadata);
        Assert.Equal("DeepSeek", metadata!.ProviderName);
        Assert.Equal("deepseek-v4-flash", metadata.DefaultModelId);
    }

    [Fact]
    public void GetService_ReturnsNull_ForOtherServiceTypes()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }

    /// <summary>
    /// R11 — DeepSeekChatClient 非 2xx（HTTP 400）：被吞的 API 错误进 OTel span——
    /// Activity 置 Error 状态、StatusDescription 含 "DeepSeek API 400"、产生 "exception" 事件，
    /// 兜底回复保留（不抛异常）。
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_OnHttp400_SetsActivityErrorAndExceptionEvent()
    {
        // DeepSeekChatClient 用 PostAsync("") 依赖 BaseAddress；测试须显式设置（否则空 URI 抛异常）
        using var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.BadRequest, "bad request detail"))
        {
            BaseAddress = new Uri("https://api.deepseek.example"),
        };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var source = new ActivitySource("R11.DeepSeekChatClientTests");
            using var activity = source.StartActivity("deepseek.request", ActivityKind.Client);

            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

            // 兜底回复保留（被吞异常不抛给调用方）
            Assert.Equal("抱歉，暂时无法处理您的请求，请重试。", response.Messages[0].Text);
            // span 置 Error + StatusDescription 含状态码与截断详情
            Assert.Equal(ActivityStatusCode.Error, activity!.Status);
            Assert.Contains("DeepSeek API 400", activity.StatusDescription);
            // "exception" 事件带 type/message tag
            var excEvent = Assert.Single(activity.Events, e => e.Name == "exception");
            Assert.Equal("HttpRequestException", (string)excEvent.Tags.First(t => t.Key == "exception.type").Value!);
            Assert.Contains("bad request detail",
                (string)excEvent.Tags.First(t => t.Key == "exception.message").Value!);
        }
        finally
        {
            listener.Dispose();
        }
    }

    /// <summary>
    /// T1 — 测试设施冒烟：分块延迟流核心行为——
    /// 首块写入后 ReadAsync 立即返回（无需等待 Complete，模拟首 token 及时到达），
    /// 后续块未下发时 ReadAsync 挂起，WriteChunk 信号到达后恢复，Complete 后返回 EOF。
    /// </summary>
    [Fact]
    public async Task ChunkedDelayedStream_FirstChunkImmediatelyReadable_SubsequentWaitsForSignal()
    {
        using var stream = new ChunkedDelayedStream();
        stream.WriteChunk("data: {\"first\":true}\n\n");

        // 首块立即可读，无需等待流结束
        var buffer = new byte[128];
        var firstRead = await stream.ReadAsync(buffer.AsMemory());
        Assert.Contains("\"first\":true", Encoding.UTF8.GetString(buffer, 0, firstRead));

        // 后续块未下发前，第二次读取应挂起（模拟 LLM 尚未生成剩余内容）
        var pendingRead = stream.ReadAsync(buffer.AsMemory()).AsTask();
        Assert.False(pendingRead.IsCompleted, "后续分块未下发时 ReadAsync 应挂起等待信号");

        // 下发后续块后，挂起的读取恢复
        stream.WriteChunk("data: {\"second\":true}\n\n");
        var secondRead = await pendingRead;
        Assert.Contains("\"second\":true", Encoding.UTF8.GetString(buffer, 0, secondRead));

        // Complete 后返回 0（EOF）
        stream.Complete();
        Assert.Equal(0, await stream.ReadAsync(buffer.AsMemory()));
    }

    /// <summary>
    /// T1 — 测试设施冒烟：ChunkedDelayedHttpMessageHandler 返回 200，
    /// response.Content 经 ReadAsStreamAsync 直接暴露分块延迟流
    /// （对应 DeepSeekChatClient.GetStreamingResponseAsync 的读流路径）。
    /// </summary>
    [Fact]
    public async Task ChunkedDelayedHttpMessageHandler_ReturnsOkAndExposesChunkedStream()
    {
        using var stream = new ChunkedDelayedStream();
        using var handler = new ChunkedDelayedHttpMessageHandler(stream);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };

        // ResponseHeadersRead：不缓冲响应体（PostAsync 默认会缓冲并触发 ChunkedDelayedHttpContent.SerializeToStreamAsync 抛异常）
        var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = new StringContent("{}") };
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        stream.WriteChunk("data: {\"hello\":\"world\"}\n\n");
        using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[128];
        var read = await body.ReadAsync(buffer.AsMemory());
        Assert.Contains("\"hello\":\"world\"", Encoding.UTF8.GetString(buffer, 0, read));
    }

    /// <summary>
    /// 固定响应 handler：返回指定状态码 + body，供 DeepSeekChatClient 非 2xx 分支测试。
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }

    /// <summary>
    /// T1 — 分块延迟下发流：支持分批追加 SSE 数据（WriteChunk），
    /// 后续分块可暂不写就（ReadAsync 挂起等待信号），用于模拟 LLM 增量生成时序——
    /// 分块以换行结尾时，消费方（StreamReader.ReadLineAsync）无需等完整 body 即可返回，
    /// 从而验证真流式 TTFB（首块到达即产生首个 token）。
    /// </summary>
    private sealed class ChunkedDelayedStream : Stream
    {
        // 基于 System.IO.Pipelines.Pipe：写入方 Write+Flush 后读取方立即可读，
        // 未写入时 ReadAsync 挂起等待（Pipe 内部管理），无自定义信号竞态。
        private readonly Pipe _pipe = new();
        private bool _completed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>追加一个 SSE 分块；分块以换行结尾时，正在等待的 ReadAsync 将立即返回。</summary>
        public void WriteChunk(string chunk)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk);
            _pipe.Writer.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
            _pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        /// <summary>标记流结束；此后 ReadAsync 返回 0（EOF）。</summary>
        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            _pipe.Writer.Complete();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _pipe.Reader.ReadAsync(cancellationToken);
            if (result.Buffer.IsEmpty)
                return 0; // writer 已完成且无剩余数据 → EOF
            var copyLength = (int)Math.Min(buffer.Length, result.Buffer.Length);
            var slice = result.Buffer.Slice(0, copyLength);
            var written = 0;
            foreach (var segment in slice)
            {
                segment.Span.CopyTo(buffer.Span[written..]);
                written += segment.Span.Length;
            }
            _pipe.Reader.AdvanceTo(result.Buffer.GetPosition(copyLength));
            return copyLength;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_completed) _pipe.Writer.Complete(); // 唤醒可能存在的等待者
                _pipe.Reader.Complete();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// T1 — 分块延迟 HttpContent：把 ChunkedDelayedStream 经 CreateContentReadStreamAsync 直接暴露，
    /// 使 HttpContent.ReadAsStreamAsync 不做整响应缓冲（保持流式），
    /// 配合 DeepSeekChatClient 的 response.Content.ReadAsStreamAsync 逐行读取。
    /// </summary>
    private sealed class ChunkedDelayedHttpContent : HttpContent
    {
        private readonly Stream _stream;

        public ChunkedDelayedHttpContent(Stream stream) => _stream = stream;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException("分块延迟流不参与序列化，仅经 CreateContentReadStreamAsync 直接读取");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => throw new NotSupportedException("分块延迟流不参与序列化，仅经 CreateContentReadStreamAsync 直接读取");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // 长度未知，强制流式读取
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(_stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stream);
    }

    /// <summary>
    /// T1 — 分块延迟 handler：返回 HTTP 200，其 Content 为分块延迟流
    /// （首个 SSE 分块立即写就，后续分块等待 WriteChunk/TCS 信号），供流式 TTFB 测试使用。
    /// </summary>
    private sealed class ChunkedDelayedHttpMessageHandler(ChunkedDelayedStream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ChunkedDelayedHttpContent(stream) });
    }
}
