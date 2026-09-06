using System.Runtime.CompilerServices;
using AIShop.Core.Services;
using Microsoft.Extensions.AI;

namespace AIShop.AguiHost;

/// <summary>
/// 服务端回复清洗中间件（T11，评审方案 A）：以 MEAI <see cref="DelegatingChatClient"/> 中间件形态包装
/// AguiHost 的默认 <see cref="IChatClient"/> 单例，在 agent 输出文本（含流式增量）离开 AguiHost 前经
/// <see cref="ReplySanitizer"/>（Core 唯一来源）清洗商品编号展示（#5 / 商品Id:4 / 商品ID为4 / 商品ID是5 等），
/// 与老 Agent（ShoppingAssistantAgent 的 SanitizeReply）同语义的服务端兜底。只作用于 AguiHost 装配的
/// chatClient 单例，不影响老 Api/Service 经 ModelRouter 构建的实例。
/// </summary>
/// <remarks>
/// 为什么包在这里：ChatClientAgent 无 Harness 内建清洗，AG-UI 会话输出的文本即底层 <see cref="IChatClient"/>
/// 返回/流式推送的 LLM 文本；在 IChatClient 结果路径统一清洗即可覆盖 AGUIShopping 的全部对外输出，且指令层约束
/// （T9 输出规约）之外多一道服务端兜底。清洗只作用于 <see cref="TextContent"/>（用户可见文本），
/// 工具调用（<see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/>）与推理内容原样透传。
/// </remarks>
public sealed class ReplySanitizingChatClient : DelegatingChatClient
{
    /// <summary>用给定底层 chatClient 构造清洗中间件。</summary>
    public ReplySanitizingChatClient(IChatClient inner) : base(inner) { }

    /// <summary>
    /// 非流式路径：完整响应返回前对每个 assistant 消息的 <see cref="TextContent"/> 整体清洗
    /// （<see cref="ReplySanitizer.Clean"/>），随后透传。
    /// </summary>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        if (response is not null && response.Messages is { Count: > 0 })
        {
            foreach (var message in response.Messages)
            {
                if (message.Role == ChatRole.Assistant)
                    SanitizeMessageText(message);
            }
        }

        // response 已在上方判空（为 null 时无可清洗内容）；基类签名返回非空 ChatResponse，
        // 用 ! 抑制 CS8603（逻辑上此处与 DeepSeekDelegatingChatClient 直返 base 等价）
        return response!;
    }

    /// <summary>
    /// 流式路径：对每个含 <see cref="TextContent"/> 的增量 update 做 <see cref="ReplySanitizer.CleanIncremental"/>
    /// 增量清洗——返回可安全发送前缀、缓冲可能构成模式前缀/完整模式的尾部，跨 chunk 拼接后在流结束用
    /// <see cref="ReplySanitizer.Clean"/> 冲洗（真正删除商品编号），对齐老 Agent 流式增量清洗语义。
    /// buffer 是本次流式调用的局部变量（每次 <c>GetStreamingResponseAsync</c> 一个独立 LLM 响应流），
    /// 不跨请求/会话共享状态（中间件本身是 DI 单例）。
    /// </summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = "";

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            var textContents = update.Contents.OfType<TextContent>().ToList();
            if (textContents.Count == 0)
            {
                // 无文本内容（角色标记/工具调用增量/FinishReason）原样透传
                yield return update;
                continue;
            }

            // 拼接本次 update 的全部文本增量（通常恰一个 TextContent）做增量清洗
            var text = string.Concat(textContents.Select(t => t.Text));
            var (safeToEmit, remaining) = ReplySanitizer.CleanIncremental(text, buffer);
            buffer = remaining;

            // 全是文本且本 chunk 无可安全发送前缀（整个尾部在等模式完整）：不产生空文本 update
            if (safeToEmit.Length == 0 && update.Contents.Count == textContents.Count)
                continue;

            // 重建 Contents：TextContent 原位替换为 safeToEmit（空则不保留），其余内容（工具调用等）原样透传
            var newContents = new List<AIContent>(update.Contents.Count);
            var emitted = false;
            foreach (var content in update.Contents)
            {
                if (content is TextContent)
                {
                    if (!emitted && safeToEmit.Length > 0)
                    {
                        newContents.Add(new TextContent(safeToEmit));
                        emitted = true;
                    }
                    continue;
                }

                newContents.Add(content);
            }

            if (!emitted && safeToEmit.Length > 0)
                newContents.Add(new TextContent(safeToEmit));

            update.Contents = newContents;
            yield return update;
        }

        // 流结束：冲洗缓冲区残留（Clean 完整清洗才能真正删除已完成模式；非空才补发一条文本 update）
        if (!string.IsNullOrEmpty(buffer))
        {
            var flushed = ReplySanitizer.Clean(buffer);
            if (!string.IsNullOrEmpty(flushed))
                yield return new ChatResponseUpdate(ChatRole.Assistant, flushed);
        }
    }

    /// <summary>
    /// 把单个 assistant 消息的全部 <see cref="TextContent"/> 文本聚合后整体清洗，重建 Contents：
    /// 清洗后文本保留在第一个文本内容原位（维持与工具/推理内容的相对顺序），空结果整段删除。
    /// 非文本内容（工具调用/推理/附加内容）一律不动。
    /// </summary>
    private static void SanitizeMessageText(ChatMessage message)
    {
        var textContents = message.Contents.OfType<TextContent>().ToList();
        if (textContents.Count == 0)
            return;

        var rawText = string.Concat(textContents.Select(t => t.Text));
        if (rawText.Length == 0)
            return;

        var cleaned = ReplySanitizer.Clean(rawText);

        var newContents = new List<AIContent>(message.Contents.Count);
        var inserted = false;
        foreach (var content in message.Contents)
        {
            if (content is TextContent)
            {
                if (!inserted && cleaned.Length > 0)
                {
                    newContents.Add(new TextContent(cleaned));
                    inserted = true;
                }

                // 其余（原始）TextContent 丢弃，由上面的清洗后文本替代
                continue;
            }

            newContents.Add(content);
        }

        if (!inserted && cleaned.Length > 0)
            newContents.Add(new TextContent(cleaned));

        message.Contents = newContents;
    }
}
