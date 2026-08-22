using AIShop.Service.Clients;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;

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
    /// T3 — spec Requirement 1「首块内容到达即产生首个 token」：真流式 TTFB 逻辑验证——
    /// 首个 SSE 分块（含部分 content）下发后，首个 MoveNextAsync 立即返回首个 ChatResponseUpdate，
    /// 不等完整响应生成（此时后续分块未下发、流未结束）。限时 2s 兜底：若实现退化为
    /// "收集完整响应后批量 yield"（伪流式），首个 MoveNextAsync 将挂起等待流结束 → 超时失败，
    /// 即本用例兼作伪流式回归守卫。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_FirstChunkImmediatelyYieldsFirstToken()
    {
        using var stream = new ChunkedDelayedStream();
        using var handler = new ChunkedDelayedHttpMessageHandler(stream);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 首块含部分 content；后续分块（含剩余 content）暂不写就、流未结束——模拟 LLM 首块已到、余块未出
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"你\"}}]}\n\n");

        await using var enumerator =
            client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).GetAsyncEnumerator();

        // 首个 MoveNextAsync 在首块下发后立即返回（此时流未结束、后续块未下发，即"不等完整响应"）
        try
        {
            var firstMove = await MoveNextWithinAsync(enumerator, TimeSpan.FromSeconds(2));
            Assert.True(firstMove, "首个 MoveNextAsync 应在首块 content 到达后立即返回首个 token（不等完整响应）");
            Assert.Equal("你", enumerator.Current.Text);
        }
        catch (TimeoutException)
        {
            Assert.Fail("首个 MoveNextAsync 超时未返回：疑似伪流式回归（收集完整响应后才批量 yield）");
        }

        // 下发剩余 content 分块，按到达顺序取得第二个 token
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"好\"}}]}\n\n");
        Assert.True(await MoveNextWithinAsync(enumerator, TimeSpan.FromSeconds(2)),
            "第二个 content 分块下发后应继续 yield 后续 token");
        Assert.Equal("好", enumerator.Current.Text);

        // 流结束：[DONE] 后迭代器正常结束（无更多 token）
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        Assert.False(await MoveNextWithinAsync(enumerator, TimeSpan.FromSeconds(2)),
            "流结束（[DONE]）后不应再产生 token");
    }

    /// <summary>
    /// 消费 MoveNextAsync 返回的 ValueTask&lt;bool&gt; 并限时等待（TTFB 回归守卫：
    /// 伪流式"收集完整响应后再 yield"时首个 MoveNextAsync 会挂起等待流结束 → 超时抛 TimeoutException）。
    /// 先取出 ValueTask 再调用 AsTask，规避 Sonar S5034 对 identifier.Call().AsTask() 链的已知误报；
    /// ValueTask 仍恰好消费一次（经 AsTask 转为 Task 后仅限时等待）。
    /// </summary>
    private static async Task<bool> MoveNextWithinAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator, TimeSpan timeout)
    {
        var move = enumerator.MoveNextAsync();
        return await move.AsTask().WaitAsync(timeout);
    }

    /// <summary>
    /// T3 — spec Requirement 1「多内容分块按到达顺序逐个 yield」：
    /// 多个 delta.content 分块按 SSE 到达顺序逐个 yield，文本片段顺序与 delta.content 一致。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_MultipleContentChunksYieldedInOrder()
    {
        using var stream = new ChunkedDelayedStream();
        using var handler = new ChunkedDelayedHttpMessageHandler(stream);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 多个 content 分块按到达顺序写入，随后结束流
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"你\"}}]}\n\n");
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"好\"}}]}\n\n");
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"世\"}}]}\n\n");
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"界\"}}]}\n\n");
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            texts.Add(update.Text);

        // yield 顺序与 SSE 到达顺序一致
        Assert.Equal(new[] { "你", "好", "世", "界" }, texts);
    }

    /// <summary>
    /// T5 — spec Requirement 2「分片 tool_calls 拼装为完整 FunctionCallContent」：
    /// 同一 tool_call 的多个分片（首分片带 id/type/function.name + arguments 首段，
    /// 后续分片带 arguments 续段）消费完整流后 yield 出完整 FunctionCallContent——
    /// CallId/Name/Arguments（反序列化后）拼装正确；工具轮 updates 文本为空（不推前端）、
    /// 仅含 FunctionCallContent。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_SplitToolCall_AssemblesCompleteFunctionCallContent()
    {
        using var stream = new ChunkedDelayedStream();
        using var handler = new ChunkedDelayedHttpMessageHandler(stream);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 同一 tool_call（index=0）分片下发：首分片带 id/name + arguments 首段，续分片带 arguments 续段
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 0, id = "call_add", type = "function", function = new { name = "add_to_cart", arguments = "{\"productId\":" } }
        }));
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 0, function = new { arguments = "4,\"quantity\":1}" } }
        }));
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        // 工具轮 updates 文本为空、仅含 FunctionCallContent
        var fccUpdate = Assert.Single(updates);
        Assert.True(string.IsNullOrEmpty(fccUpdate.Text), "工具轮 updates 文本应为空（不推前端）");
        var fcc = Assert.Single(fccUpdate.Contents.OfType<FunctionCallContent>());

        // CallId/Name/Arguments（反序列化后）拼装正确
        Assert.Equal("call_add", fcc.CallId);
        Assert.Equal("add_to_cart", fcc.Name);
        Assert.NotNull(fcc.Arguments);
        Assert.Equal(4, GetArgumentInt(fcc.Arguments!, "productId"));
        Assert.Equal(1, GetArgumentInt(fcc.Arguments!, "quantity"));
    }

    /// <summary>
    /// T5 — spec Requirement 2「多 index 的 tool_calls 独立拼装」：
    /// index 不同的多个 tool_call 分片交错下发，每个 index 独立累积，
    /// 流末 yield 顺序按 index 升序（与分片到达顺序无关）。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_MultiIndexToolCalls_YieldedInIndexAscendingOrder()
    {
        using var stream = new ChunkedDelayedStream();
        using var handler = new ChunkedDelayedHttpMessageHandler(stream);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 交错下发 index=0 / index=1 分片：先各自首分片、再各自续段（模拟真实交错到达）
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 0, id = "call_0", type = "function", function = new { name = "search_product", arguments = "{\"query\":" } }
        }));
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 1, id = "call_1", type = "function", function = new { name = "add_to_cart", arguments = "{\"productId\":" } }
        }));
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 0, function = new { arguments = "\"耳机\"}" } }
        }));
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 1, function = new { arguments = "4,\"quantity\":2}" } }
        }));
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        // 两个 index 独立累积并各自 yield 完整 FCC，顺序按 index 升序（index 0 先于 index 1）
        var fccs = updates.Select(u => Assert.Single(u.Contents.OfType<FunctionCallContent>())).ToList();
        Assert.Equal(2, fccs.Count);
        Assert.All(updates, u => Assert.True(string.IsNullOrEmpty(u.Text), "工具轮 updates 文本应为空（不推前端）"));

        var search = fccs[0];
        Assert.Equal("call_0", search.CallId);
        Assert.Equal("search_product", search.Name);
        Assert.Equal("耳机", GetArgumentString(search.Arguments!, "query"));

        var add = fccs[1];
        Assert.Equal("call_1", add.CallId);
        Assert.Equal("add_to_cart", add.Name);
        Assert.Equal(4, GetArgumentInt(add.Arguments!, "productId"));
        Assert.Equal(2, GetArgumentInt(add.Arguments!, "quantity"));
    }

    /// <summary>
    /// T7 — spec Requirement 3「分块 reasoning 累积并经 callId 回传」：
    /// 流式下多个 reasoning_content 分块被拼接累积（完整推理内容 = 全部分块，非仅最后一块），
    /// 流末 tool_calls 将完整推理内容写入 _reasoningByCallId[callId]；
    /// 后续 GetResponseAsync 请求体中同一 call_id 的 assistant 消息经 BuildRequestBody
    /// 回传完整 reasoning_content（与 GetResponseAsync L96-110 行为一致）。
    /// 同时断言推理内容未被 yield 到前端（消费流时无 reasoning 文本 chunk，仅 FCC 更新）。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ReasoningAccumulatedAndPassedBackByCallId()
    {
        // 首响应：分块延迟流（流式，SSE）；次响应：常规 JSON（非流式 GetResponseAsync 读响应体）
        using var stream = new ChunkedDelayedStream();
        using var handler = new SequencedHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ChunkedDelayedHttpContent(stream) },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}"),
            });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.example") };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 多个 reasoning_content 分块 + 流末 tool_calls（完整推理内容 = "推理第一段" + "，推理第二段"）
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"推理第一段\"}}]}\n\n");
        stream.WriteChunk("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"，推理第二段\"}}]}\n\n");
        stream.WriteChunk(ToolCallSseChunk(new object[]
        {
            new { index = 0, id = "call_r1", type = "function", function = new { name = "add_to_cart", arguments = "{\"productId\":4}" } }
        }));
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();

        // 消费完整流：推理内容不 yield（工具轮仅 FCC 更新，文本为空 → 不推前端）
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        var fccUpdate = Assert.Single(updates);
        Assert.True(string.IsNullOrEmpty(fccUpdate.Text), "推理内容不应被 yield 到前端（工具轮 updates 文本为空）");
        var fcc = Assert.Single(fccUpdate.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_r1", fcc.CallId);
        Assert.Equal("add_to_cart", fcc.Name);
        Assert.Equal(4, GetArgumentInt(fcc.Arguments!, "productId"));

        // 后续请求：携带同一 call_id 的 assistant 消息（无 TextReasoningContent），
        // BuildRequestBody 经 _reasoningByCallId 回传完整推理内容
        var assistantMsg = new ChatMessage { Role = ChatRole.Assistant };
        assistantMsg.Contents.Add(new FunctionCallContent("call_r1", "add_to_cart",
            new Dictionary<string, object?> { ["productId"] = 4 }));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi"), assistantMsg]);

        // 请求体携带完整拼接的推理内容（非仅最后一块——若退化"只留最后一块"则此处缺失"推理第一段"）。
        // 注：System.Text.Json 默认编码器将非 ASCII 字符转义为 \uXXXX，故经 JsonDocument 解析取值断言（对转义鲁棒）。
        Assert.NotNull(handler.LastRequestBody);
        using var bodyDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var reasoningMsg = bodyDoc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.TryGetProperty("role", out var role) && role.GetString() == "assistant");
        Assert.True(reasoningMsg.TryGetProperty("reasoning_content", out var reasoningEl),
            "后续请求体应携带同一 call_id 对应的 reasoning_content 字段");
        Assert.Equal("推理第一段，推理第二段", reasoningEl.GetString());
    }

    /// <summary>
    /// T9 — spec Requirement 4「非 2xx 响应抛 HttpRequestException」：
    /// HTTP 400 → 枚举 GetStreamingResponseAsync 时抛 HttpRequestException（不再静默空流），
    /// 异常消息携带状态码与截断错误体（"DeepSeek API 400" + "bad request detail"）。
    /// 抛出的异常类型即 HttpRequestException，命中上层 IsRetryableAgentFailure 分类器
    /// （HttpRequestException => true，已由 AIShop.Api.Tests 既有
    /// IsRetryableAgentFailure_ClassifiesByExceptionType 谓词测试覆盖）→ 进入重试/降级路径。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_OnHttp400_ThrowsHttpRequestException()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.BadRequest, "bad request detail"))
        {
            BaseAddress = new Uri("https://api.deepseek.example"),
        };
        using var client = new DeepSeekChatClient(httpClient, "deepseek-v4-flash");

        // 枚举流时（首个 MoveNextAsync）非 2xx 分支抛 HttpRequestException，而非静默 yield break 返回空流
        await using var enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
            .GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync(); // 先取出 ValueTask 再 AsTask，规避 Sonar S5034 误报
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => move.AsTask());

        // 异常消息含状态码与截断错误体（与 OTel span 状态文案一致）
        Assert.Contains("DeepSeek API 400", ex.Message);
        Assert.Contains("bad request detail", ex.Message);
    }

    /// <summary>
    /// T9 — spec Requirement 4 / R11 语义保留：流式非 2xx 抛异常时，
    /// span 仍置 Error + StatusDescription 含状态码与截断错误体 + 产生 "exception" 事件
    /// （与既有非流式 GetResponseAsync_OnHttp400_SetsActivityErrorAndExceptionEvent 遥测行为对称，
    /// 抛异常不丢被吞 API 错误进 OTel span 的语义）。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_OnHttp400_StillSetsActivityErrorAndExceptionEvent()
    {
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
            using var source = new ActivitySource("R4.DeepSeekChatClientStreamTests");
            using var activity = source.StartActivity("deepseek.stream.request", ActivityKind.Client);

            // 首个 MoveNextAsync 抛 HttpRequestException（迭代器在非 2xx 分支 throw，先取 ValueTask 规避 S5034）
            await using var enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
                .GetAsyncEnumerator();
            var move = enumerator.MoveNextAsync();
            await Assert.ThrowsAsync<HttpRequestException>(() => move.AsTask());

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
    /// T5 — 构造 OpenAI 兼容 tool_calls 分片 SSE 行（data: {...}）：
    /// 经 JsonSerializer 序列化保证嵌套 arguments 转义正确（避免手工拼 JSON 转义出错）。
    /// </summary>
    private static string ToolCallSseChunk(IEnumerable<object> fragments)
    {
        var json = JsonSerializer.Serialize(fragments);
        return $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":{json}}}}}]}}\n\n";
    }

    /// <summary>
    /// T5 — 从 tool_call arguments 字典读取整数值。
    /// 反序列化 `Dictionary&lt;string, object?&gt;` 后，JSON 数字被表示为 JsonElement（非原生 long/int），
    /// Convert.ToInt32(JsonElement) 会抛 InvalidCastException，故统一经 GetInt32 提取。
    /// </summary>
    private static int GetArgumentInt(IDictionary<string, object?> args, string key)
    {
        var value = args[key];
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            return element.GetInt32();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// T5 — 从 tool_call arguments 字典读取字符串值。
    /// JSON 字符串反序列化为 JsonElement（String 类型），经 GetString 提取。
    /// </summary>
    private static string GetArgumentString(IDictionary<string, object?> args, string key)
    {
        var value = args[key];
        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";
        return value?.ToString() ?? "";
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
    /// T7 — 顺序响应 + 记录请求体 handler：按调用次数依次返回预设响应，
    /// 并记录最近一次请求体（供断言 _reasoningByCallId 经后续请求体回传）。
    /// 首响应为分块延迟流（流式 GetStreamingResponseAsync），次响应为常规 JSON
    /// （非流式 GetResponseAsync 读响应体）；两个调用共享同一 client 实例（_reasoningByCallId 为实例字段）。
    /// </summary>
    private sealed class SequencedHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly HttpResponseMessage[] _responses = responses;
        private int _callIndex;
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            var response = _responses[_callIndex];
            _callIndex++;
            return response;
        }
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
