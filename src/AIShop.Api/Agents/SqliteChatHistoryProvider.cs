using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using Serilog;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Agents;

/// <summary>
/// 基于 EF Core + SQLite 的 ChatHistoryProvider。
///
/// 设计原则：
/// - **增量追加 + 后台裁剪**：Store 时先追加本轮消息，然后裁剪该 session 超出上限的旧消息
///   → 既保障跨 FICC 轮次的消息完整性（不丢 tool_calls 配对），又控制存储不无限膨胀
/// - **读取时截断**：MaxReadMessages 仅在 ProvideChatHistoryAsync 读取时应用
///   → 存储层保留更多作为缓冲，未来调大读取窗口也有历史可用
/// - **ContentsJson 全量序列化**：FunctionCallContent / FunctionResultContent / TextContent
///   全量序列化到 contents_json 字段，反序列化时 1:1 还原
/// - **不过滤 tool 消息**：历史中保留 ToolMessage，确保 Assistant{tool_calls} 与 ToolMessage 配对
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
    /// <summary>每次 Provide 返回给 LLM 的消息上限。</summary>
    private const int MaxReadMessages = 30;

    /// <summary>
    /// 每个 session 存储的消息上限，需 >= MaxReadMessages。
    /// </summary>
    private const int MaxStoredMessages = 50;

    private static readonly Serilog.ILogger Logger = Log.ForContext<SqliteChatHistoryProvider>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new AIContentListConverter() }
    };

    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = GetSessionId(context.Session!);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // 取该 session 最新 N 条，不按 SourceType / Role 过滤
        // 保留所有角色（user / assistant / tool），让 Assistant{tool_calls} 与 ToolMessage 配对完整
        var rows = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.SequentialNumber)
            .Take(MaxReadMessages)
            .OrderBy(m => m.SequentialNumber)
            .ToListAsync(cancellationToken);

        sw.Stop();
        Logger.Information("ProvideChatHistory: Session={SessionId} Count={Count} Elapsed={ElapsedMs}ms",
            sessionId, rows.Count, sw.ElapsedMilliseconds);

        var result = rows.Select(row =>
        {
            var role = row.Role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User
            };

            var msg = new AgentChatMessage { Role = role };

            if (!string.IsNullOrEmpty(row.ContentsJson))
            {
                try
                {
                    msg.Contents = JsonSerializer.Deserialize<List<AIContent>>(
                        row.ContentsJson, JsonOptions) ?? [];
                }
                catch (JsonException ex)
                {
                    Logger.Warning(ex, "ContentsJson 反序列化失败, 降级为纯文本");
                    msg.Contents = !string.IsNullOrEmpty(row.Content)
                        ? [new TextContent(row.Content)]
                        : [];
                }
            }
            else if (!string.IsNullOrEmpty(row.Content))
            {
                msg.Contents = [new TextContent(row.Content)];
            }

            return msg;
        }).ToList();

        // 【核心策略】保留完整的消息配对结构：
        // 保留所有角色（user / assistant / tool），确保 assistant{tool_calls} ↔ tool 一一对应，
        // 满足 OpenAI 兼容 API 的约束（tool 消息前必须有 assistant 且有匹配 tool_call_id）。
        // 历史中的 FunctionCallContent 不会被 FICC 重执行，FICC 只执行 LLM 当前响应中的 FCC。
        // 只过滤掉剥离 FCC 后变空的 Assistant 消息（纯 tool_call 无文本的情况）。
        result = result.Where(m => !(m.Role == ChatRole.Assistant && m.Contents.Count == 0)).ToList();

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

        // 获取当前 session 最大序号，用于新消息的递增赋值
        var maxSeq = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .MaxAsync(m => (long?)m.SequentialNumber, cancellationToken) ?? 0;

        foreach (var (msg, i) in allMessages.Select((m, i) => (m, i)))
        {
            var contentsJson = msg.Contents.Count > 0
                ? JsonSerializer.Serialize(msg.Contents, JsonOptions)
                : null;

            var rawText = string.Join(Environment.NewLine,
                msg.Contents.OfType<TextContent>().Select(t => t.Text));

            // 对 assistant 回复：如果 TextContent 是 {"Reply":"...",...}，提取 Reply 字段
            // 避免 JSON 元数据（Keywords/Preferences）泄漏到对话历史
            var textContent = msg.Role == ChatRole.Assistant
                ? StripAgentReplyJson(rawText)
                : rawText;

            db.ChatMessages.Add(new Core.Entities.ChatMessage
            {
                SessionId = sessionId,
                Role = msg.Role.ToString() ?? "user",
                Content = textContent,
                ContentsJson = contentsJson,
                SequentialNumber = maxSeq + i + 1,
                Timestamp = DateTime.UtcNow,
            });
        }

        // 步骤 2：裁剪旧消息，控制存储大小
        // 只保留该 session 最新的 MaxStoredMessages 条，超出部分删除。
        // 使用 Skip + 批量删除，避免一次加载全量到内存。
        var toDelete = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.SequentialNumber)
            .Skip(MaxStoredMessages)
            .ToListAsync(cancellationToken);

        if (toDelete.Count > 0)
        {
            Logger.Debug("裁剪旧消息 Session={SessionId} Count={DeleteCount}",
                sessionId, toDelete.Count);
            db.ChatMessages.RemoveRange(toDelete);
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
}

/// <summary>
/// AIContent 多态序列化/反序列化转换器。
///
/// 支持三种子类型：FunctionCallContent / FunctionResultContent / TextContent。
///
/// 判断规则（Read）：
/// - 含 "CallId" + "Result" → FunctionResultContent
/// - 含 "Name" + "Arguments" → FunctionCallContent
/// - 其他 → TextContent
///
/// MEAI 10.8.0 兼容：
/// - FunctionCallContent: 构造函数 (string? callId, string name, IDictionary<string, object?>? arguments)
/// - FunctionResultContent: 构造函数 (string? callId, object? result), Exception 是属性赋值
/// </summary>
public sealed class AIContentListConverter : JsonConverter<List<AIContent>>
{
    public override List<AIContent>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for AIContent list");

        var result = new List<AIContent>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            using var doc = JsonDocument.ParseValue(ref reader);
            var obj = doc.RootElement;

            AIContent? content = null;

            // 规则 1: FunctionResultContent — 有 CallId + Result 字段
            if (obj.TryGetProperty("CallId", out var callIdEl) &&
                obj.TryGetProperty("Result", out var resultEl))
            {
                var callId = callIdEl.GetString();
                if (callId is not null)
                {
                    object? resultValue = null;
                    if (resultEl.ValueKind == JsonValueKind.String)
                    {
                        var raw = resultEl.GetString();
                        if (raw is not null)
                        {
                            try
                            {
                                // 旧格式：双重序列化的 JSON 字符串（"找到..." 带引号）
                                resultValue = JsonSerializer.Deserialize<object>(raw, options);
                            }
                            catch (JsonException)
                            {
                                // 新格式：纯文本字符串，直接使用
                                resultValue = raw;
                            }
                        }
                    }
                    else if (resultEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        resultValue = JsonSerializer.Deserialize<object>(resultEl.GetRawText(), options);
                    }

                    content = new FunctionResultContent(callId, resultValue);

                    // Exception 是属性赋值，不是构造函数参数
                    if (obj.TryGetProperty("Exception", out var exEl)
                        && exEl.ValueKind != JsonValueKind.Null
                        && exEl.ValueKind != JsonValueKind.Undefined)
                    {
                        try
                        {
                            if (exEl.ValueKind == JsonValueKind.String)
                            {
                                var json = exEl.GetString();
                                if (json is not null)
                                    ((FunctionResultContent)content).Exception =
                                        JsonSerializer.Deserialize<Exception>(json, options);
                            }
                            else if (exEl.ValueKind == JsonValueKind.Object)
                            {
                                ((FunctionResultContent)content).Exception =
                                    exEl.Deserialize<Exception>(options);
                            }
                        }
                        catch { /* ignore deserialization errors for Exception */ }
                    }
                }
            }
            // 规则 2: FunctionCallContent — 有 Name + Arguments 字段
            else if (obj.TryGetProperty("Name", out var nameEl) &&
                     obj.TryGetProperty("Arguments", out var argsEl))
            {
                var name = nameEl.GetString();
                var callId = obj.TryGetProperty("CallId", out var cidEl)
                    ? cidEl.GetString()
                    : null;

                if (name is not null)
                {
                    Dictionary<string, object?>? args = null;
                    if (argsEl.ValueKind == JsonValueKind.String)
                    {
                        // 旧格式：WriteString + Serialize 导致双重序列化为 JSON 字符串
                        // 提取字符串后再反序列化为 Dictionary
                        var json = argsEl.GetString();
                        if (json is not null)
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, options);
                    }
                    else if (argsEl.ValueKind == JsonValueKind.Object)
                    {
                        // 新格式：WritePropertyName + Serialize(writer) 写入原生 JSON 对象
                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            argsEl.GetRawText(), options);
                    }

                    content = new FunctionCallContent(callId ?? "", name, args);
                }
            }
            // 规则 3: TextContent — 兜底
            else
            {
                var text = obj.TryGetProperty("Text", out var textEl)
                    ? textEl.GetString()
                    : "";
                content = new TextContent(text ?? "");
            }

            if (content is not null)
                result.Add(content);
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<AIContent> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var content in value)
        {
            writer.WriteStartObject();

            switch (content)
            {
                case FunctionCallContent fcc:
                    writer.WriteString("Name", fcc.Name);
                    if (fcc.Arguments is not null)
                    {
                        writer.WritePropertyName("Arguments");
                        JsonSerializer.Serialize(writer, fcc.Arguments, options);
                    }
                    else
                        writer.WriteNull("Arguments");
                    writer.WriteString("CallId", fcc.CallId);
                    break;

                case FunctionResultContent frc:
                    writer.WriteString("CallId", frc.CallId);
                    if (frc.Result is not null)
                    {
                        writer.WritePropertyName("Result");
                        JsonSerializer.Serialize(writer, frc.Result, options);
                    }
                    else
                        writer.WriteNull("Result");
                    if (frc.Exception is not null)
                    {
                        writer.WritePropertyName("Exception");
                        JsonSerializer.Serialize(writer, frc.Exception, options);
                    }
                    break;

                case TextContent tc:
                    writer.WriteString("Text", tc.Text);
                    break;

                default:
                    // 未知子类型：按原始 AIContent 序列化
                    JsonSerializer.Serialize(writer, content, options);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
