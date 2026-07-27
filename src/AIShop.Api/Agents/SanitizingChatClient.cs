using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AIShop.Api.Agents;

/// <summary>
/// 发前清洗管线装饰器（IChatClient），在消息发给 LLM 前执行 4 步清洗：
/// 1. 删空 tool_calls — 移除无 FCC 且无文本的 assistant 消息
/// 2. 补缺失的工具结果 — FCC 数量 > tool 消息数量时追加兜底
/// 3. 合并连续相同角色消息 — 相邻 user/user 或 assistant/assistant 合并文本，跳过含 FCC 的
/// 4. 重编号 tool_call_id — 所有 CallId → call_sanitized_N
/// </summary>
public sealed class SanitizingChatClient(IChatClient inner) : IChatClient
{
    public void Dispose() => inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();

        // 注意：不在这里剥离 TextReasoningContent。
        // DeepSeekChatClient 需要 TextReasoningContent 来映射 reasoning_content 请求字段。
        // 非 DeepSeek 模型（通过 OpenAIClient/AsIChatClient）本身不识别此类型，传递无影响。

        list = Step1_RemoveEmptyToolCalls(list);
        list = Step2_FillMissingToolResults(list);
        list = Step3_MergeConsecutiveSameRole(list);
        list = Step4_RenumberCallIds(list);

        return await inner.GetResponseAsync(list, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey);

    // =================================================================
    // 步骤 1：删空 tool_calls
    // 移除 Contents 中无 FunctionCallContent 且无文本的 assistant 消息
    // =================================================================
    public static List<ChatMessage> Step1_RemoveEmptyToolCalls(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                var hasFcc = msg.Contents.OfType<FunctionCallContent>().Any();
                var hasText = msg.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));

                if (!hasFcc && !hasText)
                    continue; // 移除此空消息
            }
            result.Add(msg);
        }
        return result;
    }

    // =================================================================
    // 步骤 2：补缺失的工具结果
    // 检查 FCC 数量 vs tool 消息数量，不足时追加兜底 FRC
    // =================================================================
    public static List<ChatMessage> Step2_FillMissingToolResults(List<ChatMessage> messages)
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
                    // Add the assistant message itself
                    result.Add(msg);

                    // 收集后续紧随的 tool 消息（含空壳消息）
                    var rawToolMessages = new List<ChatMessage>();
                    int j = i + 1;
                    while (j < messages.Count && messages[j].Role == ChatRole.Tool)
                    {
                        rawToolMessages.Add(messages[j]);
                        j++;
                    }

                    // 过滤掉无 FunctionResultContent 的孤儿 tool 消息（来自旧版历史记录，无 CallId）
                    // 这些空壳消息会导致 DeepSeek 报 "missing field tool_call_id"
                    var toolMessages = rawToolMessages
                        .Where(m => m.Contents.OfType<FunctionResultContent>().Any())
                        .ToList();

                    // 按 CallId 匹配：找出已有的 tool 结果对应的 CallId 集合
                    var existingCallIds = toolMessages
                        .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                        .Select(frc => frc.CallId)
                        .ToHashSet();

                    // 添加已有的 tool 消息（保留顺序）
                    result.AddRange(toolMessages);

                    // 对没有对应 tool 结果的 FCC，追加兜底
                    foreach (var fcc in fccList.Where(f => !existingCallIds.Contains(f.CallId)))
                    {
                        result.Add(new ChatMessage
                        {
                            Role = ChatRole.Tool,
                            Contents = [new FunctionResultContent(fcc.CallId, "[工具调用结果丢失]")]
                        });
                    }

                    i = j; // 跳过已处理的 tool 消息
                    continue;
                }
            }

            // Non-assistant or assistant without FCC: add as-is
            result.Add(msg);
            i++;
        }
        return result;
    }

    // =================================================================
    // 步骤 3：合并连续相同角色消息
    // 相邻 user/user 或 assistant/assistant 合并文本，跳过含 FCC 的消息
    // =================================================================
    public static List<ChatMessage> Step3_MergeConsecutiveSameRole(List<ChatMessage> messages)
    {
        if (messages.Count <= 1)
            return [.. messages];

        var result = new List<ChatMessage>(messages.Count);
        result.Add(messages[0]);

        for (int i = 1; i < messages.Count; i++)
        {
            var current = messages[i];
            var previous = result[^1];

            if (previous.Role == current.Role && previous.Role != ChatRole.Tool)
            {
                // 两条消息都不能含 FCC（否则破坏配对）
                var prevHasFcc = previous.Contents.OfType<FunctionCallContent>().Any();
                var currHasFcc = current.Contents.OfType<FunctionCallContent>().Any();
                if (!prevHasFcc && !currHasFcc)
                {
                    // 合并：后一条的文本追加到前一条
                    var prevText = string.Join(Environment.NewLine,
                        previous.Contents.OfType<TextContent>().Select(t => t.Text));
                    var currText = string.Join(Environment.NewLine,
                        current.Contents.OfType<TextContent>().Select(t => t.Text));

                    // 如果前一条已经合并过文本，需要保留所有之前的文本
                    string mergedText;
                    if (string.IsNullOrEmpty(prevText))
                        mergedText = currText;
                    else if (string.IsNullOrEmpty(currText))
                        mergedText = prevText;
                    else
                        mergedText = prevText + Environment.NewLine + currText;

                    // 替换前一条 Contents：移除旧 TextContent，添加合并后的文本
                    var newContents = previous.Contents
                        .Where(c => c is not TextContent)
                        .ToList();
                    if (!string.IsNullOrEmpty(mergedText))
                        newContents.Add(new TextContent(mergedText));

                    result[^1] = new ChatMessage
                    {
                        Role = previous.Role,
                        Contents = newContents,
                        AdditionalProperties = previous.AdditionalProperties,
                        AuthorName = previous.AuthorName,
                        RawRepresentation = previous.RawRepresentation
                    };
                    continue; // 跳过当前消息
                }
            }
            result.Add(current);
        }
        return result;
    }

    // =================================================================
    // 步骤 4：重编号 tool_call_id
    // 所有 CallId → call_sanitized_N，FCC 与 FRC 同步更新
    // =================================================================
    public static List<ChatMessage> Step4_RenumberCallIds(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);

        // 第一遍：收集所有 FCC 的 (index, originalCallId) 映射
        // 我们需要按 FCC 出现的顺序编号，然后同步更新 FRC 的 CallId
        var fccEntries = new List<(int msgIndex, int contentIndex, string originalCallId)>();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            for (int j = 0; j < msg.Contents.Count; j++)
            {
                if (msg.Contents[j] is FunctionCallContent fcc)
                {
                    fccEntries.Add((i, j, fcc.CallId));
                }
            }
        }

        if (fccEntries.Count == 0)
            return [.. messages]; // 无 FCC，不做任何修改

        // 建立旧 CallId → 新 CallId 映射
        var callIdMap = new Dictionary<string, string>(fccEntries.Count);
        for (int k = 0; k < fccEntries.Count; k++)
        {
            var newId = $"call_sanitized_{k + 1}";
            callIdMap[fccEntries[k].originalCallId] = newId;
        }

        // 重建消息列表，更新 FCC 和 FRC 的 CallId
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var newContents = new List<AIContent>(msg.Contents.Count);
            bool changed = false;

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fcc:
                        {
                            if (callIdMap.TryGetValue(fcc.CallId, out var newId))
                            {
                                var replacement = new FunctionCallContent(newId, fcc.Name, fcc.Arguments);
                                newContents.Add(replacement);
                                changed = true;
                            }
                            else
                            {
                                newContents.Add(fcc);
                            }
                            break;
                        }
                    case FunctionResultContent frc:
                        {
                            if (!string.IsNullOrEmpty(frc.CallId) && callIdMap.TryGetValue(frc.CallId, out var newId))
                            {
                                var replacement = new FunctionResultContent(newId, frc.Result);
                                newContents.Add(replacement);
                                changed = true;
                            }
                            else
                            {
                                newContents.Add(frc);
                            }
                            break;
                        }
                    default:
                        newContents.Add(content);
                        break;
                }
            }

            if (changed)
            {
                result.Add(new ChatMessage
                {
                    Role = msg.Role,
                    Contents = newContents,
                    AdditionalProperties = msg.AdditionalProperties,
                    AuthorName = msg.AuthorName,
                    RawRepresentation = msg.RawRepresentation
                });
            }
            else
            {
                result.Add(msg);
            }
        }

        return result;
    }
}
