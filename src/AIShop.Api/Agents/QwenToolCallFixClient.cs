using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Serilog;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Agents;

/// <summary>
/// 修复非 OpenAI 模型（Qwen/DeepSeek）的 FunctionCallContent 兼容性问题。
///
/// 问题现象：
/// - GPT 系列模型：FICC 收到 FunctionCallContent → 正常执行工具 → 第 2 次 LLM 调用
/// - Qwen/DeepSeek：FICC 收到 FunctionCallContent → 工具不执行 → 直接返回
///
/// 根因：OpenAI SDK 的 AsIChatClient() 对非 OpenAI 兼容端点的 tool_calls 响应，
/// 在特定场景下（contents.Count > 1 且含 TextReasoningContent 时），
/// FunctionCallContent 无法被 FICC 内部的 ProcessFunctionCallsAsync 正确识别和执行。
///
/// 修复策略：直接从 ChatCompletion.RawRepresentation 重新解析 tool_calls，
/// 替换 msg.Contents 中的 FunctionCallContent 为全新的实例，
/// 消除 FICC 内部的兼容性分支。
/// </summary>
public sealed class QwenToolCallFixClient : DelegatingChatClient
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<QwenToolCallFixClient>();

    public QwenToolCallFixClient(IChatClient innerClient) : base(innerClient) { }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        foreach (var msg in response.Messages)
        {
            if (msg.Role != ChatRole.Assistant)
                continue;

            var hasFcc = msg.Contents.Any(c => c.GetType().Name == "FunctionCallContent");
            if (!hasFcc)
                continue;

            var rebuiltFccs = RebuildFunctionCalls(msg.RawRepresentation);
            if (rebuiltFccs.Count == 0)
                continue;

            Log.Information("[QwenFix] 修复 FCC: 原={OldCount}个 新={NewCount}个",
                msg.Contents.Count(c => c.GetType().Name == "FunctionCallContent"),
                rebuiltFccs.Count);

            var newContents = new List<AIContent>(msg.Contents.Count);
            foreach (var content in msg.Contents)
            {
                if (content.GetType().Name == "FunctionCallContent")
                    continue;
                newContents.Add(content);
            }
            foreach (var fcc in rebuiltFccs)
                newContents.Add(fcc);

            msg.Contents = newContents;
        }

        return response;
    }

    private static List<AIContent> RebuildFunctionCalls(object? rawRepresentation)
    {
        var result = new List<AIContent>();

        if (rawRepresentation is not ChatCompletion completion)
            return result;

        foreach (var toolCall in completion.ToolCalls)
        {
            if (toolCall is not ChatToolCall funcCall)
                continue;

            Dictionary<string, object?>? args = null;
            try
            {
                var argsJson = funcCall.FunctionArguments.ToString();
                if (!string.IsNullOrEmpty(argsJson))
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
            }
            catch (JsonException)
            {
                // 忽略参数解析失败
            }

            var fcc = new FunctionCallContent(
                funcCall.Id,
                funcCall.FunctionName,
                args);

            result.Add(fcc);
        }

        return result;
    }
}
