using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.Api.Agents;

/// <summary>
/// 统一 IChatClient 中间件，继承 DelegatingChatClient，所有模型共用。
/// 在 GetResponseAsync 中：
/// 1. 统一执行发前清洗（删空 tool_calls / 补缺失工具结果 / 合并连续角色 / 重编号 CallId）
/// 2. DeepSeek → 自建原生 JSON 直发 HTTP，绕过 MEAI 序列化（处理 reasoning_content）
/// 3. GPT/Qwen → 清洗后通过 base.GetResponseAsync() 委托给 inner（OpenAIClient）
/// </summary>
public sealed class DeepSeekDelegatingChatClient : DelegatingChatClient
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DeepSeekDelegatingChatClient>();
    private readonly HttpClient? _httpClient;
    private readonly string _modelName;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // DeepSeek 专用：记录 reasoning_content 以便后续请求回传
    private readonly Dictionary<string, string?> _reasoningByCallId = new();

    public DeepSeekDelegatingChatClient(IChatClient innerClient, HttpClient? httpClient, string modelName)
        : base(innerClient)
    {
        _httpClient = httpClient;
        _modelName = modelName;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _httpClient is not null)
            _httpClient.Dispose();
        base.Dispose(disposing);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // R10.1：MEAI 埋点的 gen_ai.request.model 从 request.ChatOptions.ModelId 读取（不是 IChatClient.Metadata）。
        // deepseek 请求时 ChatOptions.ModelId 为空 → 属性空；此处所有模型统一补填，
        // 已显式指定则不被覆盖（??=）。放在发前清洗与模型分流之前，deepseek 直发路径与 base 路径都生效。
        options ??= new ChatOptions();
        options.ModelId ??= _modelName;

        var list = messages.ToList();

        Log.Information("[DSDelegate] Enter: Count={Count} Roles=[{Roles}] LastTool={LastTool}",
            list.Count,
            string.Join(",", list.Select(m => m.Role.ToString())),
            list.LastOrDefault(m => m.Role == ChatRole.Tool)
                ?.Contents?.OfType<FunctionResultContent>().FirstOrDefault()?.CallId ?? "none");

        // ===== 统一发前清洗（所有模型） =====
        list = RemoveEmptyToolCalls(list);
        list = FillMissingToolResults(list);
        list = MergeConsecutiveSameRole(list);

        // ===== 模型分流 =====
        if (IsDeepSeek)
        {
            // 注意：不执行 RenumberCallIds。
            // DeepSeek 返回的 call_00_xxx 以及 Qwen 跨模型切换带来的 call_sanitized_N 都是合法 ID。
            // 重编号会破坏 FICC 的 tool 结果匹配，导致 [工具调用结果丢失]。
            return await DeepSeekDirectCallAsync(list, options, cancellationToken);
        }

        // GPT/Qwen：清洗后走标准 inner pipeline
        return await base.GetResponseAsync(list, options, cancellationToken);
    }

    private bool IsDeepSeek => _modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    // =================================================================
    // DeepSeek 直发：自建 JSON + HTTP 调用
    // =================================================================
    private async Task<ChatResponse> DeepSeekDirectCallAsync(
        List<ChatMessage> messages, ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var client = _httpClient ?? throw new InvalidOperationException("DeepSeek 路径要求 _httpClient 非 null");
        var apiMessages = BuildApiMessages(messages);
        var toolsList = BuildTools(options);

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
        var response = await client.PostAsync("", httpContent, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        Log.Information("[DeepSeekDirect] 响应体: {Body}",
            responseBody.Length > 3000 ? responseBody[..3000] + "..." : responseBody);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("[DeepSeekDirect] API 错误: {StatusCode} {Body}", response.StatusCode, responseBody);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "抱歉，暂时无法处理您的请求，请重试。"));
        }

        return ParseDeepSeekResponse(responseBody);
    }

    private List<object> BuildApiMessages(List<ChatMessage> messages)
    {
        var apiMessages = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString()?.ToLower() ?? "user";
            var text = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));

            var apiMsg = new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = string.IsNullOrEmpty(text) ? null : text,
            };

            // tool 消息：每个 FRC 生成一条独立 API tool 消息（tool_call_id 与 assistant tool_calls 一一对应），
            // 修复 deepseek 并行工具结果丢失（多 FRC 只发第一条 → 400）；单 FRC 行为不变。
            if (msg.Role == ChatRole.Tool)
            {
                var frcs = msg.Contents.OfType<FunctionResultContent>().ToList();
                if (frcs.Count > 0)
                {
                    foreach (var frc in frcs)
                    {
                        apiMessages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = frc.CallId,
                            ["content"] = frc.Result?.ToString() ?? "",
                        });
                    }
                    continue;
                }
            }

            var fccs = msg.Contents.OfType<FunctionCallContent>().ToList();
            if (fccs.Count > 0)
            {
                apiMsg["tool_calls"] = fccs.Select(fcc => new
                {
                    id = fcc.CallId,
                    type = "function",
                    function = new
                    {
                        name = fcc.Name,
                        arguments = fcc.Arguments is not null
                            ? JsonSerializer.Serialize(fcc.Arguments, JsonOptions) : "{}"
                    }
                }).ToList();
            }

            if (!apiMsg.ContainsKey("content") || apiMsg["content"] is null)
                apiMsg["content"] = "";

            var reasoning = string.Join("", msg.Contents.OfType<TextReasoningContent>().Select(t => t.Text));
            if (!string.IsNullOrEmpty(reasoning))
                apiMsg["reasoning_content"] = reasoning;
            else if (fccs.Count > 0
                && _reasoningByCallId.TryGetValue(fccs[0].CallId, out var saved)
                && saved is not null)
                apiMsg["reasoning_content"] = saved;

            if (apiMsg["content"] is null && !apiMsg.ContainsKey("tool_calls"))
                apiMsg.Remove("content");

            apiMessages.Add(apiMsg);
        }
        return apiMessages;
    }

    private static List<object> BuildTools(ChatOptions? options)
    {
        var toolsList = new List<object>();
        if (options?.Tools is null) return toolsList;
        foreach (var tool in options.Tools)
        {
            if (tool is AIFunction aFunc)
            {
                object? parameters = null;
                var schemaJson = JsonSerializer.Serialize(aFunc.JsonSchema, JsonOptions);
                if (!string.IsNullOrEmpty(schemaJson))
                {
                    try { parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(schemaJson, JsonOptions); }
                    catch { parameters = schemaJson; }
                }
                toolsList.Add(new
                {
                    type = "function",
                    function = new { name = aFunc.Name, description = aFunc.Description, parameters }
                });
            }
        }
        return toolsList;
    }

    private ChatResponse ParseDeepSeekResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var replyContent = message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
        var replyReasoning = message.TryGetProperty("reasoning_content", out var reasoningEl) ? reasoningEl.GetString() : null;
        JsonElement? toolCallsEl = message.TryGetProperty("tool_calls", out var tcEl) ? tcEl : null;

        var chatMessage = new ChatMessage { Role = ChatRole.Assistant };

        if (!string.IsNullOrEmpty(replyContent))
            chatMessage.Contents.Add(new TextContent(replyContent));

        if (replyReasoning is not null)
        {
            chatMessage.Contents.Add(new TextReasoningContent(replyReasoning));
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
                    catch (JsonException) { /* args 非 JSON 格式时降级为 null */ }
                }
                chatMessage.Contents.Add(new FunctionCallContent(callId, funcName, args));
            }
        }
        return new ChatResponse(chatMessage);
    }

    // =================================================================
    // 清洗 1：删空 tool_calls
    // =================================================================
    private static List<ChatMessage> RemoveEmptyToolCalls(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                var hasFcc = msg.Contents.OfType<FunctionCallContent>().Any();
                var hasText = msg.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));
                if (!hasFcc && !hasText) continue;
            }
            result.Add(msg);
        }
        return result;
    }

    // =================================================================
    // 清洗 2：补缺失的工具结果（只删孤儿 tool，不补假结果）
    // 检查 FCC 是否有对应的 tool 结果。按 CallId 在整个消息列表范围搜索。
    //
    // 注意：不补兜底 [工具调用结果丢失]。
    // 补假结果会污染 history→FICC 基于假数据继续迭代→更多错误→历史膨胀→系统性崩溃。
    // 缺少 FCC 配对的 tool 结果归 FICC 自己处理。
    // =================================================================
    private static List<ChatMessage> FillMissingToolResults(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        int i = 0;
        while (i < messages.Count)
        {
            var msg = messages[i];
            if (msg.Role == ChatRole.Assistant)
            {
                var fccList = msg.Contents.OfType<FunctionCallContent>().ToList();
                if (fccList.Count > 0)
                {
                    result.Add(msg);

                    // 收集紧邻的 tool 消息，原样保留（只保留有 FunctionResultContent 的）
                    int j = i + 1;
                    while (j < messages.Count && messages[j].Role == ChatRole.Tool)
                    {
                        if (messages[j].Contents.OfType<FunctionResultContent>().Any())
                            result.Add(messages[j]);
                        j++;
                    }

                    i = j;
                    continue;
                }
            }
            result.Add(msg);
            i++;
        }
        return result;
    }

    // =================================================================
    // 清洗 3：合并连续相同角色消息
    // =================================================================
    private static List<ChatMessage> MergeConsecutiveSameRole(List<ChatMessage> messages)
    {
        if (messages.Count <= 1) return [.. messages];
        var result = new List<ChatMessage>(messages.Count) { messages[0] };
        for (int i = 1; i < messages.Count; i++)
        {
            var cur = messages[i];
            var prev = result[^1];
            if (prev.Role == cur.Role && prev.Role != ChatRole.Tool
                && !prev.Contents.OfType<FunctionCallContent>().Any()
                && !cur.Contents.OfType<FunctionCallContent>().Any())
            {
                var prevText = string.Join(Environment.NewLine,
                    prev.Contents.OfType<TextContent>().Select(t => t.Text));
                var curText = string.Join(Environment.NewLine,
                    cur.Contents.OfType<TextContent>().Select(t => t.Text));

                var mergedText = prevText;
                if (!string.IsNullOrEmpty(curText))
                    mergedText = string.IsNullOrEmpty(prevText) ? curText : prevText + Environment.NewLine + curText;

                var newContents = prev.Contents.Where(c => c is not TextContent).ToList();
                if (!string.IsNullOrEmpty(mergedText)) newContents.Add(new TextContent(mergedText));

                result[^1] = new ChatMessage
                {
                    Role = prev.Role, Contents = newContents,
                    AdditionalProperties = prev.AdditionalProperties,
                    AuthorName = prev.AuthorName,
                    RawRepresentation = prev.RawRepresentation
                };
                continue;
            }
            result.Add(cur);
        }
        return result;
    }

    // RenumberCallIds 已被移除。
    // DeepSeek 返回的 call_00_xxx 和 Qwen 存盘 call_sanitized_N 都是合法 ID，
    // 重编号会破坏 FICC 的 tool 结果匹配，导致 [工具调用结果丢失]。
}
