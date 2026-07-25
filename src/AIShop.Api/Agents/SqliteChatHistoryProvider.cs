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
                // tool 消息：重建为 FunctionResultContent
                if (!string.IsNullOrEmpty(row.Content))
                {
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

                // reasoning 不重建为 TextReasoningContent —— 仅调试用
            }

            var hasNonEmptyText = contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));
            var hasToolCalls = contents.OfType<FunctionCallContent>().Any();

            // 过滤纯 FCC 无有效文本的 assistant 消息（只调工具不说话的中间轮次）
            if (role == ChatRole.Assistant && hasToolCalls && !hasNonEmptyText)
            {
                Logger.Debug("跳过纯 FCC 无文本的 assistant 消息 RowId={RowId}", row.Id);
                continue;
            }

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

        // 步骤 1：追加本轮增量消息
        // 注意：绝不先删再插。FICC 在第 2 轮只传了 [ToolMessage] 进来，
        // 如果先删历史再插，第 1 轮的 UserMessage + Assistant{tool_calls} 会丢失，
        // 下次 Provide 就凑不出完整的消息配对，导致 400。
        foreach (var msg in allMessages)
        {
            var role = msg.Role.ToString() ?? "user";

            // 提取纯文本内容
            var rawText = string.Join(Environment.NewLine,
                msg.Contents.OfType<TextContent>().Select(t => t.Text));

            // 对 assistant 回复：如果 TextContent 是 {"Reply":"...",...}，提取 Reply 字段
            // 避免 JSON 元数据（Keywords/Preferences）泄漏到对话历史
            var textContent = msg.Role == ChatRole.Assistant
                ? StripAgentReplyJson(rawText)
                : rawText;

            string? toolCalls = null;
            string? toolCallId = null;
            string? reasoning = null;

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

                // 提取 TextReasoningContent
                reasoning = string.Join(Environment.NewLine,
                    msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (string.IsNullOrEmpty(reasoning))
                    reasoning = null;
            }
            else if (msg.Role == ChatRole.Tool)
            {
                // 提取 FunctionResultContent 的 CallId 和结果文本
                var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
                if (frc is not null)
                {
                    toolCallId = frc.CallId;
                    // 如果 TextContent 为空，从 FRC.Result 提取文本
                    if (string.IsNullOrEmpty(textContent) && frc.Result is string resultStr)
                        textContent = resultStr;
                }
            }

            db.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = sessionId,
                Role = role,
                Content = textContent,
                ToolCalls = toolCalls,
                ToolCallId = toolCallId,
                Reasoning = reasoning,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // 步骤 2：裁剪旧消息，控制存储大小
        // 只保留该 session 最新的 MaxStoredMessages 条，超出部分物理删除。
        // 使用 Skip + 批量删除，避免一次加载全量到内存。
        var toDelete = await db.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .Skip(MaxStoredMessages)
            .ToListAsync(cancellationToken);

        if (toDelete.Count > 0)
        {
            Logger.Debug("裁剪旧消息 Session={SessionId} Count={DeleteCount}",
                sessionId, toDelete.Count);
            db.ChatMessageRecords.RemoveRange(toDelete);
        }

        await db.SaveChangesAsync(cancellationToken);

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

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('{'))
            return raw;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("Reply", out var reply) && reply.ValueKind == JsonValueKind.String)
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
}
