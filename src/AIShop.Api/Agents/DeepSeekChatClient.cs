using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AIShop.Api.Agents;

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
        var messagesList = messages.ToList();

        // 1. 构建 DeepSeek API 请求体（原生 JSON，不依赖 SDK 序列化）
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

            // 去掉 content=null（tool 消息的 content 可以为空）
            // content=null 时 DeepSeek 报 "missing field content"
            if (!apiMsg.ContainsKey("content") || (apiMsg["content"] is null && role != "tool"))
                apiMsg["content"] = "";

            // 但 tool 消息必须保留 content 字段（可以空字符串）
            // 已经统一在上面保证了 content 不为 null

            // reasoning_content：从 TextReasoningContent 提取
            var reasoning = string.Join("", msg.Contents.OfType<TextReasoningContent>().Select(t => t.Text));
            if (!string.IsNullOrEmpty(reasoning))
            {
                apiMsg["reasoning_content"] = reasoning;
            }
            // 或者从历史记录匹配（FICC 可能重建了消息，丢了 TextReasoningContent）
            else if (fccs.Count > 0)
            {
                var callId = fccs[0].CallId;
                if (_reasoningByCallId.TryGetValue(callId, out var savedReasoning) && savedReasoning is not null)
                    apiMsg["reasoning_content"] = savedReasoning;
            }

            // 如果是 tool 角色但前面有 assistant 带着 reasoning，把 reasoning 带到 tool 消息的前一条
            // 因为 DeepSeek 检查的是 HISTORY 中 assistant 消息的 reasoning_content

            // 去掉 null content
            if (apiMsg["content"] is null && !apiMsg.ContainsKey("tool_calls"))
                apiMsg.Remove("content");

            apiMessages.Add(apiMsg);
        }

        // 构建完整请求
        var toolsList = new List<object>();
        if (options?.Tools is not null)
        {
            foreach (var tool in options.Tools)
            {
                if (tool is AIFunction aFunc)
                {
                    // 直接从 AIFunction.JsonSchema 获取原生 JSON Schema（object 类型）
                    object? parameters = null;
                    var schemaElement = aFunc.JsonSchema;
                    // AIFunction.JsonSchema 返回的是 JsonElement，可直接序列化为 object
                    var schemaJson = JsonSerializer.Serialize(schemaElement, JsonOptions);
                    if (!string.IsNullOrEmpty(schemaJson))
                    {
                        try
                        {
                            // 反序列化为 Dictionary 确保 JSON 序列化为原生对象而非字符串
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
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        Log.Information("[DeepSeekDirect] 请求体: {Body}", json);

        var httpContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("", httpContent, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        Log.Information("[DeepSeekDirect] 响应体: {Body}",
            responseBody.Length > 3000 ? responseBody[..3000] + "..." : responseBody);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("[DeepSeekDirect] API 错误: {StatusCode} {Body}", response.StatusCode, responseBody);
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
    /// Streaming 请求发送到 DeepSeek API，流式解析响应。
    /// DeepSeek 的 streaming 模式会在每个 chunk 中携带完整的 reasoning_content。
    /// 这里简单处理：收集所有 chunks 的 content 和 tool_calls，最后返回一个完整的结果。
    /// 注意：由于 MAF 框架要求返回 IAsyncEnumerable，但 DeepSeekDirect 不支持增量流式输出，
    /// 这里直接透传给下游（由 SanitizingChatClient 包装）。
    /// 如果框架未使用 streaming，此方法不会被调用。
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 用非 streaming 方式获取完整响应，然后模拟流式输出
        var response = await GetResponseAsync(messages, options, cancellationToken);
        if (response is not null)
        {
            foreach (var msg in response.Messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is TextContent tc)
                    {
                        yield return new ChatResponseUpdate(msg.Role, tc.Text);
                    }
                }
            }
        }
    }

    // R10.1：gen_ai.provider.name 由 MEAI 埋点从 IChatClient.GetService(typeof(ChatClientMetadata))
    // 返回的 metadata.ProviderName 读取。暴露 R10 已实现的 Metadata，供 DelegatingChatClient 链转发到遥测。
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? Metadata : null;
}
