using System.Reflection;
using AIShop.Api.Agents;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace AIShop.Api.Tests;

/// <summary>
/// T31：DeepSeek 直发路径 BuildApiMessages 的并行工具结果修复测试。
/// 反射调用 private BuildApiMessages（BindingFlags.NonPublic | BindingFlags.Instance），
/// 断言多 FRC tool 消息生成独立 API tool 消息（tool_call_id 一一对应）。
/// </summary>
public sealed class DeepSeekDelegatingChatClientTests
{
    private static readonly MethodInfo BuildApiMessagesMethod = typeof(DeepSeekDelegatingChatClient).GetMethod(
        "BuildApiMessages", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static List<Dictionary<string, object?>> InvokeBuildApiMessages(List<ChatMessage> messages)
    {
        using var sut = new DeepSeekDelegatingChatClient(Substitute.For<IChatClient>(), null, "deepseek-test");
        var result = (List<object>)BuildApiMessagesMethod.Invoke(sut, new object[] { messages })!;
        return result.OfType<Dictionary<string, object?>>().ToList();
    }

    [Fact]
    public void BuildApiMessages_WithMultipleFrcs_EmitsOneToolMessagePerFrc()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_a", "search_product", null),
                    new FunctionCallContent("call_b", "get_cart_summary", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents =
                [
                    new FunctionResultContent("call_a", "结果A"),
                    new FunctionResultContent("call_b", "结果B"),
                ]
            },
        };

        var apiMessages = InvokeBuildApiMessages(messages);

        // 恰好 2 条 tool 消息（每个 FRC 一条，无 FRC 被丢弃）
        var toolMessages = apiMessages.Where(m => Equals(m["role"], "tool")).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal(2, apiMessages.Count(m => m.TryGetValue("tool_call_id", out _)));

        // tool_call_id 与 assistant tool_calls 一一对应
        Assert.Equal("call_a", toolMessages[0]["tool_call_id"]);
        Assert.Equal("call_b", toolMessages[1]["tool_call_id"]);

        // content 分别等于对应 FRC 的 Result
        Assert.Equal("结果A", toolMessages[0]["content"]);
        Assert.Equal("结果B", toolMessages[1]["content"]);
    }
}
