using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;
using Serilog;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Agents;

/// <summary>
/// 基于 EF Core + SQLite 的 ChatHistoryProvider。
///
/// 设计原则：
/// - **增量追加 + 后台裁剪**：Store 时先追加本轮消息，然后裁剪该 session 超出上限的旧消息
///   → 既保障跨 FICC 轮次的消息完整性（不丢 tool_calls 配对），又控制存储不无限膨胀
/// - **行列化存储**：Store 时解包 MEAI ChatMessage.Contents，按角色分列写入 chat_messages 表
///   → 不再依赖 AIContentListConverter 的 ContentsJson 序列化
/// - **reasoning 独立列**：TextReasoningContent 仅存于 reasoning 列，Provide 时不重建
/// </summary>
public sealed class SqliteChatHistoryProvider(
    IDbContextFactory<AppDbContext> dbFactory) : ChatHistoryProvider()
{

    protected override ValueTask<IEnumerable<AgentChatMessage>> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        return base.InvokingCoreAsync(context, cancellationToken);
    }

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        return base.InvokedCoreAsync(context, cancellationToken);
    }

    /// <summary>每次 Store 后保留的最大消息数。</summary>
    private const int MaxStoredMessages = 50;

    private static readonly Serilog.ILogger Logger = Log.ForContext<SqliteChatHistoryProvider>();

    /// <summary>
    /// tool_calls JSON 的序列化选项（小驼峰，无缩进）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = GetSessionId(context.Session!);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // 全量加载 is_compacted=0 的消息，按 id 升序
        var rows = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

        sw.Stop();
        Logger.Information("ProvideChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, rows.Count, sw.ElapsedMilliseconds);

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
                        Logger.Warning(ex, "tool 结果反序列化失败 Session={SessionId} RowId={RowId}",
                            sessionId, row.Id);
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
                {
                    contents.Add(new TextContent(row.Content));
                }

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
                        Logger.Warning(ex, "tool_calls 反序列化失败 Session={SessionId} RowId={RowId}",
                            sessionId, row.Id);
                    }
                }

                // 重建 TextReasoningContent（供 DeepSeekChatClient 映射为 reasoning_content）
                if (role == ChatRole.Assistant && !string.IsNullOrEmpty(row.Reasoning))
                {
                    contents.Add(new TextReasoningContent(row.Reasoning));
                }
            }

            // 过滤完全空的 assistant 消息（无任何内容）
            // (NOSONAR: single-statement if with continue is intentional)
            if (role == ChatRole.Assistant && contents.Count == 0)
                continue;

            // 过滤无 FunctionResultContent 的孤儿 tool 消息（CallId 可能为空或不对齐）
            if (role == ChatRole.Tool && contents.Count == 0)
                continue;

            result.Add(new AgentChatMessage(role, contents));
        }

        Logger.Debug("ProvideChatHistory 返回: Count={Count} Roles=[{Roles}]",
            result.Count,
            string.Join(",", result.Select(m => $"{m.Role}({m.Contents.Count}个内容)")));

        return result;
    }

    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = GetSessionId(context.Session!);

        var allMessages = (context.RequestMessages ?? [])
            .Concat(context.ResponseMessages ?? [])
            .ToList();

        if (allMessages.Count == 0)
        {
            Logger.Debug("StoreChatHistory: 无消息可存 Session={SessionId}", sessionId);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // 读取本轮 run_id：同一 StateBag RunId 为所有 FICC 迭代打同一轮次标记；
        // StateBag 无 RunId 或值非法时兜底生成独立 run_id，该批自成独立轮次，不抛异常（退化现状）
        var runId = context.Session!.StateBag.TryGetValue<string>("RunId", out var runIdText, null)
            && Guid.TryParse(runIdText, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        // 步骤 1：追加本轮增量消息
        // 注意：绝不先删再插。FICC 在第 2 轮只传了 [ToolMessage] 进来，
        // 如果先删历史再插，第 1 轮的 UserMessage + Assistant{tool_calls} 会丢失，
        // 下次 Provide 就凑不出完整的消息配对，导致 400。
        foreach (var msg in allMessages)
        {
            var role = msg.Role.ToString() ?? "user";

            // 提取纯文本内容
            var textContents = msg.Contents.OfType<TextContent>()
                .Select(t => t.Text);
            var rawText = string.Join(Environment.NewLine, textContents);

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

            // 清理 <think> 标签（部分模型在 TextContent 中返回思维链）
            // <think> 本质是思维链，应存入 reasoning 列而不是 content 列
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

            // 对 assistant 回复：如果 TextContent 是 {"Reply":"...",...}，提取 Reply 字段
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

                // 提取 TextReasoningContent（仅当尚未通过第一次提取或 <think> 设置时才赋值）
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
                    // 单 FRC：保持旧格式（ToolCallId + Content），兼容存量数据
                    var frc = frcs[0];
                    toolCallId = frc.CallId;
                    // 如果 TextContent 为空，从 FRC.Result 提取文本
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

            db.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = sessionId,
                RunId = runId,
                Role = role,
                Content = textContent,
                ToolCalls = toolCalls,
                ToolCallId = toolCallId,
                Reasoning = reasoning,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // 先提交新消息，确保它们有 Id 且对后续查询可见
        await db.SaveChangesAsync(cancellationToken);

        // 步骤 2：裁剪旧消息，控制存储大小
        // 只保留该 session 最新的 MaxStoredMessages 条，超出部分标记为已压缩。
        // 非物理删除，保留原始数据——历史可追溯、可查询。
        var unCompressedCount = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .CountAsync(cancellationToken);

        var toCompressCount = unCompressedCount - MaxStoredMessages;
        if (toCompressCount <= 0)
        {
            Logger.Debug("裁剪旧消息 Session={SessionId} 无需裁剪 Count={Count}",
                sessionId, unCompressedCount);
            return;
        }

        // 找出要压缩的 Id：保留最新的 MaxStoredMessages 条，其余压缩。
        // 降序排列，跳过前 MaxStoredMessages 条（最新的），取剩下的压缩。
        var compressIds = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .OrderByDescending(m => m.Id)
            .Select(m => m.Id)
            .Skip(MaxStoredMessages)
            .Take(toCompressCount)
            .ToHashSetAsync(cancellationToken);

        // 关键：确保不切断 assistant(FCC) → tool 的配对。
        // compressIds 是降序排序后跳过50条的结果，即要压缩的最旧N条。
        // 取压缩集中最小的 id（即保留区之后的第一条被压缩消息）。
        var firstCompressId = compressIds.OrderBy(id => id).FirstOrDefault();
        if (firstCompressId > 0)
        {
            var firstCompress = await db.ChatMessageRecords.FindAsync(firstCompressId, cancellationToken);
            if (firstCompress is not null)
            {
                // 检查前一条是否是对应此 tool 的 assistant(FCC)
                if (firstCompress.Role == "tool")
                {
                    var prevMsg = await db.ChatMessageRecords
                        .Where(m => m.SessionId == sessionId && m.Id == firstCompressId - 1 && !m.IsCompacted
                            && m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls != "")
                        .FirstOrDefaultAsync(cancellationToken);
                    if (prevMsg is not null)
                        compressIds.Remove(prevMsg.Id);
                }
                // 检查第一条被压缩的是 assistant(FCC)，则也要压缩紧跟的 tool
                else if (firstCompress.Role == "assistant" && !string.IsNullOrEmpty(firstCompress.ToolCalls))
                {
                    var nextMsg = await db.ChatMessageRecords
                        .Where(m => m.SessionId == sessionId && m.Id == firstCompressId + 1 && m.Role == "tool")
                        .FirstOrDefaultAsync(cancellationToken);
                    if (nextMsg is not null)
                        compressIds.Add(nextMsg.Id);
                }
            }
        }

        // 执行压缩
        await db.ChatMessageRecords
            .Where(m => compressIds.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCompacted, true), cancellationToken);

        Logger.Information("裁剪旧消息 Session={SessionId} Count={DeleteCount}",
            sessionId, compressIds.Count);


        sw.Stop();
        Logger.Debug("StoreChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, allMessages.Count, sw.ElapsedMilliseconds);
    }

    private static Guid GetSessionId(AgentSession session)
    {
        if (session.StateBag.TryGetValue<string>("SessionId", out var id, null) && id is not null)
            return Guid.Parse(id);
        throw new InvalidOperationException("SessionId not found in session StateBag.");
    }

    /// <summary>
    /// 如果文本是 {"Reply":"...",...} 格式的 JSON，提取 Reply 字段的值。
    /// 用于防 AgentChatResult 的 JSON 序列化泄漏到对话历史。
    /// 非 JSON 或没有 Reply 字段则原样返回。
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
        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            if ((doc.RootElement.TryGetProperty("Reply", out var reply) ||
                 doc.RootElement.TryGetProperty("reply", out reply)) && reply.ValueKind == JsonValueKind.String)
                return reply.GetString() ?? raw;
        }
        catch (JsonException)
        {
            // 不是合法 JSON，原样返回
        }

        return raw;
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
