using System.Reflection;
using System.Diagnostics;
using System.Net;
using AIShop.Service.Clients;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace AIShop.Service.Tests;

/// <summary>
/// T31/T32：DeepSeek 直发路径 BuildApiMessages 的并行工具结果修复测试。
/// 反射调用 private BuildApiMessages（BindingFlags.NonPublic | BindingFlags.Instance），
/// 断言 tool 消息逐 FRC 生成独立 API tool 消息（tool_call_id 一一对应）：
/// T31 多 FRC → 每条 FRC 一条 tool 消息、无 FRC 被丢弃；T32 单 FRC → 结构与修复前一致。
/// </summary>
public sealed class DeepSeekDelegatingChatClientTests
{
    private static readonly MethodInfo BuildApiMessagesMethod = typeof(DeepSeekDelegatingChatClient).GetMethod(
        "BuildApiMessages", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static List<Dictionary<string, object?>> InvokeBuildApiMessages(List<ChatMessage> messages)
    {
        using var sut = new DeepSeekDelegatingChatClient(Substitute.For<IChatClient>(), null, "deepseek-test");
        var result = (List<object>)BuildApiMessagesMethod.Invoke(sut, new object[] { messages })!;
        return result.OfType<Dictionary<string, object?>>().ToList();
    }

    [Fact]
    public void BuildApiMessages_WithMultipleFrcs_EmitsOneToolMessagePerFrc()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_a", "search_product", null),
                    new FunctionCallContent("call_b", "get_cart_summary", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents =
                [
                    new FunctionResultContent("call_a", "结果A"),
                    new FunctionResultContent("call_b", "结果B"),
                ]
            },
        };

        var apiMessages = InvokeBuildApiMessages(messages);

        // 恰好 2 条 tool 消息（每个 FRC 一条，无 FRC 被丢弃）
        var toolMessages = apiMessages.Where(m => Equals(m["role"], "tool")).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal(2, apiMessages.Count(m => m.TryGetValue("tool_call_id", out _)));

        // tool_call_id 与 assistant tool_calls 一一对应
        Assert.Equal("call_a", toolMessages[0]["tool_call_id"]);
        Assert.Equal("call_b", toolMessages[1]["tool_call_id"]);

        // content 分别等于对应 FRC 的 Result
        Assert.Equal("结果A", toolMessages[0]["content"]);
        Assert.Equal("结果B", toolMessages[1]["content"]);
    }

    /// <summary>
    /// T32：单 FRC tool 消息保持行为一致（输出恰好 1 条 tool 消息、结构与修复前一致）。
    /// </summary>
    [Fact]
    public void BuildApiMessages_WithSingleFrc_EmitsOneToolMessage()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_1", "search_product", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents =
                [
                    new FunctionResultContent("call_1", "结果1"),
                ]
            },
        };

        var apiMessages = InvokeBuildApiMessages(messages);

        // 恰好 1 条 tool 消息、1 个 tool_call_id
        var toolMessages = apiMessages.Where(m => Equals(m["role"], "tool")).ToList();
        Assert.Single(toolMessages);
        Assert.Single(apiMessages, m => m.TryGetValue("tool_call_id", out _));

        // tool_call_id / content 与唯一 FRC 对应（单 FRC 行为与修复前一致）
        Assert.Equal("call_1", toolMessages[0]["tool_call_id"]);
        Assert.Equal("结果1", toolMessages[0]["content"]);
    }

    /// <summary>
    /// R10.1 — gen_ai.request.model 由 MEAI 埋点从 request.ChatOptions.ModelId 读取（非 IChatClient.Metadata）。
    /// DeepSeekDelegatingChatClient.GetResponseAsync 开头填充 options.ModelId（所有模型统一，??= 不覆盖已显式值）。
    /// 走非 deepseek 分支（mock inner 捕获 options）验证修改逻辑；deepseek 分支在顶部同样生效（分支前设置）。
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PopulatesOptionsModelId_AndDoesNotOverrideExisting()
    {
        ChatOptions? captured = null;
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<ChatOptions?>();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        using var sut = new DeepSeekDelegatingChatClient(inner, null, "qwen3.8-max");
        var messages = new[] { new ChatMessage(ChatRole.User, "你好") };

        // 1) options.ModelId 为空 → 填充 _modelName
        var options = new ChatOptions();
        await sut.GetResponseAsync(messages, options);
        Assert.NotNull(captured);
        Assert.Equal("qwen3.8-max", captured!.ModelId);

        // 2) options.ModelId 非空 → 不被覆盖（??= 保留显式值）
        options.ModelId = "explicit-model";
        await sut.GetResponseAsync(messages, options);
        Assert.Equal("explicit-model", captured!.ModelId);
    }

    /// <summary>
    /// R10.1 — DelegatingChatClient.GetService 转发 inner：DeepSeekDelegatingChatClient(inner=DeepSeekChatClient)
    /// 调用 GetService(typeof(ChatClientMetadata)) 应返回 inner 的 Metadata（gen_ai.provider.name 遥测链路：
    /// 埋点从 GetService 读 provider.name，经 delegating 链转发到 DeepSeekChatClient）。
    /// </summary>
    [Fact]
    public void GetService_ForwardsChatClientMetadata_ToInner()
    {
        using var inner = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");
        using var wrapper = new DeepSeekDelegatingChatClient(inner, null, "deepseek-v4-flash");

        var metadata = wrapper.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        Assert.NotNull(metadata);
        Assert.Equal("DeepSeek", metadata!.ProviderName);
        Assert.Equal("deepseek-v4-flash", metadata.DefaultModelId);
    }

    /// <summary>
    /// R11 — DeepSeekDelegatingChatClient 非 2xx（HTTP 400）：被吞的 API 错误进 OTel span——
    /// DeepSeekDirectCallAsync 置 Activity Error + "exception" 事件，兜底回复保留。
    /// </summary>
    [Fact]
    public async Task DeepSeekDirectCall_OnHttp400_SetsActivityErrorAndExceptionEvent()
    {
        // DeepSeek 直发路径用 PostAsync("") 依赖 BaseAddress；测试须显式设置（否则空 URI 抛异常）
        using var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.BadRequest, "bad request detail"))
        {
            BaseAddress = new Uri("https://api.deepseek.example"),
        };
        using var sut = new DeepSeekDelegatingChatClient(Substitute.For<IChatClient>(), httpClient, "deepseek-v4-flash");

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var source = new ActivitySource("R11.DeepSeekDelegatingTests");
            using var activity = source.StartActivity("deepseek.request", ActivityKind.Client);

            var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

            Assert.Equal("抱歉，暂时无法处理您的请求，请重试。", response.Messages[0].Text);
            Assert.Equal(ActivityStatusCode.Error, activity!.Status);
            Assert.Contains("DeepSeek API 400", activity.StatusDescription);
            var excEvent = Assert.Single(activity.Events, e => e.Name == "exception");
            Assert.Equal("HttpRequestException", (string)excEvent.Tags.First(t => t.Key == "exception.type").Value!);
        }
        finally
        {
            listener.Dispose();
        }
    }

    /// <summary>
    /// 固定响应 handler：返回指定状态码 + body，供 DeepSeek 直发路径非 2xx 分支测试。
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }
}
