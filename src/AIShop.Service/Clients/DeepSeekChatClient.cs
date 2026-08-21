using System.IO;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Clients;

/// <summary>
/// DeepSeek 专用 IChatClient，直接通过 HTTP API 调用，不经过 OpenAIChatClient。
/// 解决 AsIChatClient() 无法序列化 TextReasoningContent 为 reasoning_content 的问题。
/// DeepSeek 要求请求中必须携带历史上 assistant 消息的 reasoning_content 字段，
/// 但 OpenAIChatClient 只读不写此字段 → 报错 "must be passed back to the API"。
/// 此实现绕开 SDK，直接发送原生的 JSON 请求，手动处理 reasoning_content。
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DeepSeekChatClient>();
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    /// <summary>
    /// IChatClient.Metadata 实现（R10）：修 DeepSeek gen_ai 遥测属性（ProviderName/ModelId）为空——
    /// 旧主构造函数未实现 Metadata，接口默认 Metadata 为空对象，OTel gen_ai 属性缺失。
    /// </summary>
    public ChatClientMetadata Metadata { get; }

    public DeepSeekChatClient(HttpClient httpClient, string modelName)
    {
        _httpClient = httpClient;
        _modelName = modelName;
        // 构造函数签名 ChatClientMetadata(string providerName, Uri? providerUri, string? defaultModelId)
        // （实测 10.8.3：第 2 参是 providerUri 而非 modelId，模型名走第 3 参 defaultModelId）
        Metadata = new ChatClientMetadata(providerName: "DeepSeek", defaultModelId: modelName);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 记录对话中哪些 assistant 消息有 reasoning_content
    private readonly Dictionary<string, string?> _reasoningByCallId = new();

    public void Dispose() => _httpClient.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: false);
        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        Log.Information("[DeepSeekDirect] 请求体: {Body}", json);

        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("", httpContent, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        Log.Information("[DeepSeekDirect] 响应体: {Body}",
            responseBody.Length > 3000 ? responseBody[..3000] + "..." : responseBody);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("[DeepSeekDirect] API 错误: {StatusCode} {Body}", response.StatusCode, responseBody);
            // R11：被吞的 API 错误进 OTel span
            Activity.Current?.SetStatus(ActivityStatusCode.Error,
                $"DeepSeek API {(int)response.StatusCode}: {Truncate(responseBody, 200)}");
            Activity.Current?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", "HttpRequestException" },
                    { "exception.message", Truncate(responseBody, 200) },
                }));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "抱歉，暂时无法处理您的请求，请重试。"));
        }

        // 2. 解析 DeepSeek 响应
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var replyContent = message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
        string? replyReasoning = message.TryGetProperty("reasoning_content", out var reasoningEl) ? reasoningEl.GetString() : null;
        JsonElement? toolCallsEl = message.TryGetProperty("tool_calls", out var tcEl) ? tcEl : null;

        var chatMessage = new ChatMessage { Role = ChatRole.Assistant };

        if (!string.IsNullOrEmpty(replyContent))
            chatMessage.Contents.Add(new TextContent(replyContent));

        // 保存 reasoning_content 供后续请求使用
        if (replyReasoning is not null)
        {
            chatMessage.Contents.Add(new TextReasoningContent(replyReasoning));

            // 记录 tool_call_id → reasoning 映射
            if (toolCallsEl is not null)
            {
                foreach (var tc in toolCallsEl.Value.EnumerateArray())
                {
                    var callId = tc.GetProperty("id").GetString();
                    if (callId is not null && !_reasoningByCallId.ContainsKey(callId))
                        _reasoningByCallId[callId] = replyReasoning;
                }
            }
        }

        // tool_calls
        if (toolCallsEl is not null)
        {
            foreach (var tc in toolCallsEl.Value.EnumerateArray())
            {
                var callId = tc.GetProperty("id").GetString() ?? "";
                var funcName = tc.GetProperty("function").GetProperty("name").GetString() ?? "";
                var argsEl = tc.GetProperty("function").GetProperty("arguments");

                Dictionary<string, object?>? args = null;
                if (argsEl.ValueKind == JsonValueKind.String)
                {
                    try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsEl.GetString()!); }
                    catch { /* args 反序列化失败时不影响 FCC 创建 */ }
                }

                chatMessage.Contents.Add(new FunctionCallContent(callId, funcName, args));
            }
        }

        return new ChatResponse(chatMessage);
    }

    /// <summary>
    /// 真流式：调用 DeepSeek API 开启 stream=true，逐行解析 SSE 响应，
    /// 每个 delta.content yield 为一个 ChatResponseUpdate。
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: true);
        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        Log.Information("[DeepSeekDirect][Stream] 请求体: {Body}", json);

        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        // ResponseHeadersRead：不缓冲完整响应体，响应头一到即返回，边读边用（真流式 TTFB 的前提）
        var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = httpContent };
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Error("[DeepSeekDirect][Stream] API 错误: {StatusCode} {Body}", response.StatusCode, errorBody);
            Activity.Current?.SetStatus(ActivityStatusCode.Error,
                $"DeepSeek API {(int)response.StatusCode}: {Truncate(errorBody, 200)}");
            Activity.Current?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", "HttpRequestException" },
                    { "exception.message", Truncate(errorBody, 200) },
                }));
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 真增量文本：每块 delta.content 解析成功后立即 yield，不收集到 List。
        // CS1626（C# 禁止在含 catch 的 try 内 yield）由不 yield 的 ParseDelta helper 解决：
        // try 内仅调用 ParseDelta（无 yield），yield 放在 try 外。
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            // SSE 格式：data: {...} 或 data: [DONE]
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..]; // 去掉 "data: " 前缀

            if (data == "[DONE]")
                break;

            ParsedDelta parsed;
            try
            {
                parsed = ParseDelta(data);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "[DeepSeekDirect][Stream] 解析 SSE 行失败: {Line}", line);
                continue;
            }

            // 非空 content 立即 yield（工具轮 delta content 为空 → 自然不 yield，保持"工具调用不推前端"语义）
            if (!string.IsNullOrEmpty(parsed.Content))
                yield return new ChatResponseUpdate(ChatRole.Assistant, parsed.Content);

            // reasoning_content 暂不累积回传（T6 实现 reasoningAccumulator 累积）
        }
    }

    /// <summary>
    /// 单条 SSE data 行解析结果：独立提取 content / reasoning_content。
    /// tool_calls 第三路解析（T4）在此扩展。
    /// </summary>
    private sealed class ParsedDelta
    {
        public string? Content { get; init; }
        public string? Reasoning { get; init; }
    }

    /// <summary>
    /// 解析单条 SSE data 行（OpenAI 兼容流式）。
    /// 不 yield（供循环内 try-catch 包裹以规避 CS1626），解析失败抛 JsonException。
    /// </summary>
    private static ParsedDelta ParseDelta(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            return new ParsedDelta();

        var choice = choices[0];
        var delta = choice.GetProperty("delta");

        // content：文本增量（tool-only delta 无此属性 → null → 主循环不 yield）
        var content = delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null
            ? contentEl.GetString()
            : null;

        // reasoning_content：推理内容（仅服务端累积回传，不推前端；T2 先解析，T6 累积）
        var reasoning = delta.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind != JsonValueKind.Null
            ? reasoningEl.GetString()
            : null;

        return new ParsedDelta { Content = content, Reasoning = reasoning };
    }

    /// <summary>
    /// 构建 DeepSeek API 请求体（非流式/流式复用）。
    /// </summary>
    private Dictionary<string, object?> BuildRequestBody(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var messagesList = messages.ToList();
        var apiMessages = new List<object>();

        foreach (var msg in messagesList)
        {
            var role = msg.Role.ToString()?.ToLower() ?? "user";
            var content = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));

            var apiMsg = new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = string.IsNullOrEmpty(content) ? null : content,
            };

            // tool_calls：assistant 消息中的 FunctionCallContent
            var fccs = msg.Contents.OfType<FunctionCallContent>().ToList();
            if (fccs.Count > 0)
            {
                var toolCalls = fccs.Select(fcc => new
                {
                    id = fcc.CallId,
                    type = "function",
                    function = new
                    {
                        name = fcc.Name,
                        arguments = fcc.Arguments is not null
                            ? JsonSerializer.Serialize(fcc.Arguments, JsonOptions)
                            : "{}"
                    }
                }).ToList();
                apiMsg["tool_calls"] = toolCalls;
            }

            // tool_call_id：tool 消息中的 FunctionResultContent
            var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (frc is not null)
            {
                apiMsg["tool_call_id"] = frc.CallId;
                apiMsg["content"] = frc.Result?.ToString() ?? "";
            }

            // content=null 时 DeepSeek 报 "missing field content"
            if (!apiMsg.ContainsKey("content") || (apiMsg["content"] is null && role != "tool"))
                apiMsg["content"] = "";

            // reasoning_content：从 TextReasoningContent 提取
            var reasoning = string.Join("", msg.Contents.OfType<TextReasoningContent>().Select(t => t.Text));
            if (!string.IsNullOrEmpty(reasoning))
            {
                apiMsg["reasoning_content"] = reasoning;
            }
            else if (fccs.Count > 0)
            {
                var callId = fccs[0].CallId;
                if (_reasoningByCallId.TryGetValue(callId, out var savedReasoning) && savedReasoning is not null)
                    apiMsg["reasoning_content"] = savedReasoning;
            }

            if (apiMsg["content"] is null && !apiMsg.ContainsKey("tool_calls"))
                apiMsg.Remove("content");

            apiMessages.Add(apiMsg);
        }

        var toolsList = new List<object>();
        if (options?.Tools is not null)
        {
            foreach (var tool in options.Tools)
            {
                if (tool is AIFunction aFunc)
                {
                    object? parameters = null;
                    var schemaElement = aFunc.JsonSchema;
                    var schemaJson = JsonSerializer.Serialize(schemaElement, JsonOptions);
                    if (!string.IsNullOrEmpty(schemaJson))
                    {
                        try
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(schemaJson, JsonOptions);
                            parameters = dict;
                        }
                        catch
                        {
                            parameters = schemaJson;
                        }
                    }
                    toolsList.Add(new
                    {
                        type = "function",
                        function = new
                        {
                            name = aFunc.Name,
                            description = aFunc.Description,
                            parameters
                        }
                    });
                }
            }
        }

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["messages"] = apiMessages,
            ["tools"] = toolsList.Count > 0 ? toolsList : null,
            ["tool_choice"] = "auto",
            ["stream"] = stream ? true : null,
        };

        return requestBody;
    }

    // R10.1：gen_ai.provider.name 由 MEAI 埋点从 IChatClient.GetService(typeof(ChatClientMetadata))
    // 返回的 metadata.ProviderName 读取。暴露 R10 已实现的 Metadata，供 DelegatingChatClient 链转发到遥测。
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    /// <summary>
    /// 截断超长响应体（R11）：避免超大错误详情撑爆 OTel tag/span 属性。
    /// </summary>
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
