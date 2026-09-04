using System.Text.Json;
using System.Text.RegularExpressions;
using AIShop.Core.Models;
using Microsoft.Extensions.AI;
using Serilog;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Service.Providers;

/// <summary>
/// MAF 消息（AgentChatMessage）与存储模型（StoredMessage）的双向转换器。
/// 从 SqliteChatHistoryProvider 迁移的纯转换逻辑：写方向（含 &lt;think&gt; 剥离、AgentReplyJson 剥离、
/// tool_calls 序列化）、读方向（tool_calls/tool 结果反序列化、reasoning 重建、孤儿 tool 配对过滤）。
/// 无 IO、无 EF 依赖，可独立单元测试。
/// </summary>
public static class ChatMessageMapper
{
    private static readonly Serilog.ILogger Logger = Log.ForContext("SourceContext", nameof(ChatMessageMapper));

    /// <summary>
    /// tool_calls JSON 的序列化选项（小驼峰，无缩进）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// JSON 字符串字面量（含转义）正则，用于容错提取：把引号内的裸换行归一化为转义 \n。
    /// 100ms 超时防 ReDoS（正则线性复杂度，超时仅兜底）。
    /// </summary>
    private static readonly Regex JsonStringLiteralRegex = new(
        @"""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 写方向：把 MAF 消息列表转换为待存储的 StoredMessage 列表。
    /// Id 由存储生成（传 0）；批内末条纯文本 assistant 标记为轮次终点（IsFinal）。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="runId">轮次 ID（一次 Run 的所有 FICC 迭代共享）。</param>
    /// <param name="messages">请求 + 响应消息（已过滤）。</param>
    public static IReadOnlyList<StoredMessage> ToStoredMessages(
        Guid sessionId, Guid runId, IReadOnlyList<AgentChatMessage> messages)
    {
        // T5：批内最后一条为「纯文本 assistant」（无 FunctionCallContent）→ 本轮轮次终点，该行 IsFinal=true
        var lastMessage = messages[^1];
        var isFinalLastMessage = lastMessage.Role == ChatRole.Assistant
            && !lastMessage.Contents.OfType<FunctionCallContent>().Any();

        var result = new List<StoredMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var role = msg.Role.ToString() ?? "user";

            // 提取纯文本内容。
            // 无分隔拼接：流式回复的每个 TextContent 是同一段回复的无缝 delta 片段
            //（MAF ToChatResponse 合并 delta 时为每 delta 一个 TextContent），
            // 用换行拼接会把连续回复拆成每段换行（DB 历史竖排）。原文换行在 delta 内部保留。
            var textContents = msg.Contents.OfType<TextContent>().Select(t => t.Text);
            var rawText = string.Concat(textContents);

            string? toolCalls = null;
            string? toolCallId = null;
            string? reasoning = null;

            // 提取 TextReasoningContent
            if (msg.Role == ChatRole.Assistant)
            {
                reasoning = string.Join(Environment.NewLine,
                    msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (string.IsNullOrEmpty(reasoning))
                    reasoning = null;
            }

            // 清理 <think> 标签（部分模型在 TextContent 中返回思维链）——存入 reasoning 列
            if (!string.IsNullOrEmpty(rawText))
            {
                var thinkStart = rawText.IndexOf("<think>");
                var thinkEnd = rawText.IndexOf("</think>");
                if (thinkStart >= 0 && thinkEnd > thinkStart)
                {
                    var thinkContent = rawText[(thinkStart + 7)..thinkEnd];
                    if (string.IsNullOrEmpty(reasoning))
                        reasoning = thinkContent.Trim();
                    rawText = (rawText[..thinkStart] + rawText[(thinkEnd + 8)..]).Trim();
                }
                else if (thinkStart >= 0)
                {
                    // 只有 <think> 没有 </think>（截断），内容移到 reasoning
                    var thinkContent = rawText[(thinkStart + 7)..];
                    if (string.IsNullOrEmpty(reasoning))
                        reasoning = thinkContent.Trim();
                    rawText = "";
                }
            }

            // assistant 回复：如果是 {"Reply":"...",...} JSON，提取 Reply 字段
            var textContent = msg.Role == ChatRole.Assistant
                ? StripAgentReplyJson(rawText)
                : rawText;

            if (msg.Role == ChatRole.Assistant)
            {
                // 提取 FunctionCallContent 序列化为 tool_calls JSON
                var fccList = msg.Contents.OfType<FunctionCallContent>().ToList();
                if (fccList.Count > 0)
                {
                    var serializedCalls = fccList.Select(fcc => new
                    {
                        id = fcc.CallId,
                        type = "function",
                        function = new
                        {
                            name = fcc.Name,
                            arguments = fcc.Arguments is not null
                                ? JsonSerializer.Serialize(fcc.Arguments, JsonOptions)
                                : null
                        }
                    }).ToList();

                    toolCalls = JsonSerializer.Serialize(serializedCalls, JsonOptions);
                }

                // 再次提取 TextReasoningContent（未通过第一次提取或 <think> 设置时才赋值）
                var secondReasoning = string.Join(Environment.NewLine,
                    msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrEmpty(secondReasoning))
                    reasoning = secondReasoning;
            }
            else if (msg.Role == ChatRole.Tool)
            {
                // 按 FRC 数量分流：单 FRC 保持旧格式（ToolCallId + Content），多 FRC 序列化为 JSON 数组存 ToolCalls 列
                var frcs = msg.Contents.OfType<FunctionResultContent>().ToList();
                if (frcs.Count == 1)
                {
                    var frc = frcs[0];
                    toolCallId = frc.CallId;
                    if (string.IsNullOrEmpty(textContent) && frc.Result is not null)
                        textContent = frc.Result?.ToString() ?? "";
                }
                else if (frcs.Count > 1)
                {
                    // 多 FRC：序列化成 JSON 数组存入 ToolCalls 列（[{id, result}, ...]），ToolCallId 置空
                    var results = frcs
                        .Select(frc => new { id = frc.CallId, result = frc.Result?.ToString() ?? "" })
                        .ToList();
                    toolCalls = JsonSerializer.Serialize(results, JsonOptions);
                    toolCallId = null;
                }
            }

            result.Add(new StoredMessage(
                0, sessionId, runId, role, textContent, toolCalls, toolCallId, null, reasoning,
                isFinalLastMessage && i == messages.Count - 1, false, DateTime.UtcNow));
        }

        return result;
    }

    /// <summary>
    /// 读方向：把存储消息列表重建为 MAF 消息列表（含孤儿 tool 配对过滤）。
    /// 过滤语义：空 assistant/tool 消息跳过；孤儿 tool（组内无 assistant-FCC 配对）跳过。
    /// </summary>
    public static IReadOnlyList<AgentChatMessage> ToAgentMessages(IReadOnlyList<StoredMessage> rows)
    {
        // T8：孤儿 tool 配对检查——预计算「含 assistant-FCC 行的 run_id 集合」与「assistant-FCC 行 id 集合」。
        // 有 run_id 的行走组内配对；run_id=NULL 的历史行走相邻 id 退化路径。
        var assistantFccRunIds = rows
            .Where(r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls) && r.RunId is not null)
            .Select(r => r.RunId!.Value)
            .ToHashSet();
        var assistantFccRowIds = rows
            .Where(r => r.Role == "assistant" && !string.IsNullOrEmpty(r.ToolCalls))
            .Select(r => r.Id)
            .ToHashSet();

        var result = new List<AgentChatMessage>(rows.Count);
        foreach (var row in rows)
        {
            var role = row.Role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                "system" => ChatRole.System,
                _ => ChatRole.User
            };

            var contents = new List<AIContent>();

            if (role == ChatRole.Tool)
            {
                // tool 消息：优先读 ToolCalls 列（多 FRC JSON [{id, result}, ...]）
                if (!string.IsNullOrEmpty(row.ToolCalls))
                {
                    try
                    {
                        var results = JsonSerializer.Deserialize<List<ToolResultJson>>(row.ToolCalls, JsonOptions);
                        if (results is not null)
                        {
                            foreach (var r in results)
                                contents.Add(new FunctionResultContent(r.Id, r.Result));
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning(ex, "tool 结果反序列化失败 RowId={RowId}", row.Id);
                    }
                }
                else if (!string.IsNullOrEmpty(row.Content))
                {
                    // 单 FRC 旧格式（ToolCallId + Content），兼容存量数据
                    contents.Add(new FunctionResultContent(row.ToolCallId ?? "", row.Content));
                }
            }
            else
            {
                // user/assistant: 文本内容
                if (!string.IsNullOrEmpty(row.Content))
                    contents.Add(new TextContent(row.Content));

                // assistant: 反序列化 tool_calls JSON 重建 FunctionCallContent
                if (role == ChatRole.Assistant && !string.IsNullOrEmpty(row.ToolCalls))
                {
                    try
                    {
                        var toolCalls = JsonSerializer.Deserialize<List<ToolCallJson>>(row.ToolCalls, JsonOptions);
                        if (toolCalls is not null)
                        {
                            foreach (var tc in toolCalls)
                            {
                                Dictionary<string, object?>? args = null;
                                if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                                {
                                    try
                                    {
                                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                            tc.Function.Arguments, JsonOptions);
                                    }
                                    catch (JsonException)
                                    {
                                        // arguments 不是合法 JSON 对象时降级为空
                                    }
                                }
                                contents.Add(new FunctionCallContent(tc.Id, tc.Function?.Name ?? "", args));
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning(ex, "tool_calls 反序列化失败 RowId={RowId}", row.Id);
                    }
                }

                // 重建 TextReasoningContent（供 DeepSeekChatClient 映射为 reasoning_content）
                if (role == ChatRole.Assistant && !string.IsNullOrEmpty(row.Reasoning))
                    contents.Add(new TextReasoningContent(row.Reasoning));
            }

            // 过滤完全空的 assistant 消息（无任何内容）
            if (role == ChatRole.Assistant && contents.Count == 0)
                continue;

            // 过滤无 FunctionResultContent 的孤儿 tool 消息（CallId 可能为空或不对齐）
            if (role == ChatRole.Tool && contents.Count == 0)
                continue;

            // T8：孤儿 tool 配对检查升级——有 FRC 内容的 tool 行还必须在轮次内找到 assistant-FCC 行才进上下文：
            //   1. 有 run_id → 检查同 run_id 组内是否存在 assistant-FCC 行，不存在则过滤
            //   2. run_id=NULL 的历史 tool 行无组可查 → 退化为旧相邻 id 检查（id-1 为 assistant-FCC 则保留）
            if (role == ChatRole.Tool)
            {
                var hasPairedFcc = row.RunId is { } rid
                    ? assistantFccRunIds.Contains(rid)
                    : assistantFccRowIds.Contains(row.Id - 1);
                if (!hasPairedFcc)
                    continue;
            }

            result.Add(new AgentChatMessage(role, contents));
        }

        return result;
    }

    /// <summary>
    /// 如果文本是 {"Reply":"...",...} 格式的 JSON，提取 Reply 字段的值。
    /// 用于防 AgentChatResult 的 JSON 序列化泄漏到对话历史。非 JSON 或没有 Reply 字段则原样返回。
    /// </summary>
    private static string StripAgentReplyJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        // 查找文本中第一个 { 开始的位置（兼容自然语言 + JSON 混合输出）
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0)
            return raw;

        var jsonEnd = raw.LastIndexOf('}');
        if (jsonEnd <= jsonStart)
            return raw;

        var jsonCandidate = raw[jsonStart..(jsonEnd + 1)];
        var reply = TryExtractReply(jsonCandidate);
        if (reply is not null)
            return reply;

        // 容错：LLM 偶尔在字符串值内输出裸换行（\r\n/\n），构成非法 JSON 导致 Parse 失败，
        // 把引号内的 CR/LF 归一化为转义 \n 后重试，避免 AgentChatResult 原始 JSON 泄漏进历史。
        if (jsonCandidate.Contains('\r') || jsonCandidate.Contains('\n'))
        {
            var normalized = JsonStringLiteralRegex.Replace(jsonCandidate,
                m => m.Value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n"));
            reply = TryExtractReply(normalized);
            if (reply is not null)
                return reply;
        }

        return raw;
    }

    /// <summary>
    /// 尝试从 JSON 文本解析并提取 Reply 字符串；解析失败或没有 Reply 字段时返回 null。
    /// </summary>
    private static string? TryExtractReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if ((doc.RootElement.TryGetProperty("Reply", out var reply) ||
                 doc.RootElement.TryGetProperty("reply", out reply)) && reply.ValueKind == JsonValueKind.String)
                return reply.GetString();
        }
        catch (JsonException)
        {
            // 非合法 JSON
        }

        return null;
    }

    /// <summary>
    /// tool_calls JSON 反序列化用模型。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolCallJson
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = "function";
        public ToolCallFunctionJson? Function { get; set; } = null!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolCallFunctionJson
    {
        public string Name { get; set; } = string.Empty;
        public string? Arguments { get; set; }
    }

    /// <summary>
    /// tool 结果 JSON 反序列化用模型（ToolCalls 列，[{id, result}, ...]）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459", Justification = "JSON deserialization target")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144", Justification = "JSON deserialization target")]
    private sealed class ToolResultJson
    {
        public string Id { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }
}
