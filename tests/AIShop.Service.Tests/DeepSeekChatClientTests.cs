using AIShop.Service.Clients;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Net;

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
    /// 固定响应 handler：返回指定状态码 + body，供 DeepSeekChatClient 非 2xx 分支测试。
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }
}
