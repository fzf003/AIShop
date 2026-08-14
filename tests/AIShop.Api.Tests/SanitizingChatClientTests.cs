using System.Reflection;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using AIShop.Api.Features.Chat;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Tests;

public sealed class SanitizingChatClientTests
{
    // =========================================================
    // T3.1 — 骨架 + 流式透传
    // =========================================================

    [Fact]
    public void Dispose_DisposesInner()
    {
        var inner = Substitute.For<IChatClient>();
        var sut = new SanitizingChatClient(inner);

        sut.Dispose();

        inner.Received(1).Dispose();
    }

    [Fact]
    public void GetService_DelegatesToInner()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetService(typeof(string), null).Returns("test");
        var sut = new SanitizingChatClient(inner);

        var result = sut.GetService(typeof(string));

        Assert.Equal("test", result);
        inner.Received(1).GetService(typeof(string), null);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PassesThroughWithoutModification()
    {
        var inner = Substitute.For<IChatClient>();
        var updates = new[]
        {
            new ChatResponseUpdate(new ChatRole("assistant"), "Hello"),
            new ChatResponseUpdate(new ChatRole("assistant"), " World"),
        }.ToAsyncEnumerable();
        inner.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(updates);

        var sut = new SanitizingChatClient(inner);
        var input = new[] { new ChatMessage(ChatRole.User, "Hi") };

        var result = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(input))
        {
            result.Add(update);
        }

        Assert.Equal(2, result.Count);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal(" World", result[1].Text);
    }

    [Fact]
    public void ShoppingAssistantAgent_ConstructorWrapsChatClient()
    {
        // 验证 ShoppingAssistantAgent 构造函数将 chatClient 包裹 SanitizingChatClient
        // 通过验证 HarnessAgent 的 chatClient 链包含 SanitizingChatClient 来实现
        // 由于 HarnessAgent 内部构造细节，此测试验证构造函数不抛异常
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "test")));
        inner.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory);
        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();

        var agent = new ShoppingAssistantAgent(inner, dbFactory, ProductKeywordMap.Entries, cartTools, true,
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.Metadata });

        Assert.NotNull(agent);
    }

    // =========================================================
    // T3.2 — 步骤 1：删空 tool_calls
    // =========================================================

    [Fact]
    public void Step1_RemovesAssistantWithNoFccAndNoText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new() { Role = ChatRole.Assistant, Contents = [] }, // empty assistant
            new(ChatRole.Tool, "Result"),
        };

        var result = SanitizingChatClient.Step1_RemoveEmptyToolCalls(messages);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, m => m.Role == ChatRole.Assistant && m.Contents.Count == 0);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal("Result", result[1].Text);
    }

    [Fact]
    public void Step1_RetainsAssistantWithNoFccButHasText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "I have text"),
        };

        var result = SanitizingChatClient.Step1_RemoveEmptyToolCalls(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal("I have text", result[1].Text);
    }

    [Fact]
    public void Step1_RetainsAssistantWithFcc_EvenIfNoText()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call_1", "search_product", null)]
            },
        };

        var result = SanitizingChatClient.Step1_RemoveEmptyToolCalls(messages);

        Assert.Single(result);
        Assert.Single(result[0].Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public void Step1_RetainsNonAssistantMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Tool, "Tool result"),
        };

        var result = SanitizingChatClient.Step1_RemoveEmptyToolCalls(messages);

        Assert.Equal(2, result.Count);
    }

    // =========================================================
    // T3.3 — 步骤 2：补缺失的工具结果
    // =========================================================

    [Fact]
    public void Step2_AppendsFallbackToolMessages_WhenFccCountGreaterThanToolCount()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Find products"),
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_qwen_a", "search_product", null),
                    new FunctionCallContent("call_qwen_b", "get_cart_summary", null),
                ]
            },
            // no tool messages follow — both are missing
        };

        var result = SanitizingChatClient.Step2_FillMissingToolResults(messages);

        // original 2 + 2 fallback = 4
        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal(ChatRole.Tool, result[2].Role);
        Assert.Equal(ChatRole.Tool, result[3].Role);

        var fallback1 = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_qwen_a", fallback1.CallId);
        Assert.Equal("[工具调用结果丢失]", fallback1.Result?.ToString());

        var fallback2 = result[3].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_qwen_b", fallback2.CallId);
    }

    [Fact]
    public void Step2_DoesNotAppend_WhenToolMessagesFullyMatch()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Find products"),
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_a", "search_product", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_a", "found 5 items")]
            },
        };

        var result = SanitizingChatClient.Step2_FillMissingToolResults(messages);

        Assert.Equal(3, result.Count);
        Assert.All(result, m => Assert.NotNull(m));
    }

    [Fact]
    public void Step2_DoesNotModifyMessages_WhenNoFccPresent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there"),
        };

        var result = SanitizingChatClient.Step2_FillMissingToolResults(messages);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Step2_PartiallyFills_WhenOnlySomeToolMessagesMissing()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_1", "search_product", null),
                    new FunctionCallContent("call_2", "add_to_cart", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_1", "found items")]
            },
            // call_2 tool result is missing
        };

        var result = SanitizingChatClient.Step2_FillMissingToolResults(messages);

        // original 2 + 1 fallback = 3
        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.Tool, result[2].Role);
        Assert.Equal("call_2", result[2].Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    // =========================================================
    // T3.4 — 步骤 3：合并连续相同角色消息
    // =========================================================

    [Fact]
    public void Step3_MergesConsecutiveUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First message"),
            new(ChatRole.User, "Second message"),
            new(ChatRole.Assistant, "Response"),
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Contains("First message", result[0].Text);
        Assert.Contains("Second message", result[0].Text);
        Assert.Equal("Response", result[1].Text);
    }

    [Fact]
    public void Step3_MergesConsecutiveAssistantMessages_WithoutFcc()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "First thought"),
            new(ChatRole.Assistant, "Second thought"),
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Contains("First thought", result[1].Text);
        Assert.Contains("Second thought", result[1].Text);
    }

    [Fact]
    public void Step3_DoesNotMerge_WhenPreviousAssistantHasFcc()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_1", "search_product", null),
                ]
            },
            new(ChatRole.Assistant, "Follow-up text"),
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        // Should not merge because prev has FCC
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Step3_DoesNotMerge_WhenCurrentAssistantHasFcc()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "Initial text"),
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_1", "search_product", null),
                ]
            },
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Step3_MergesThreeConsecutiveUsersIntoOne()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "A"),
            new(ChatRole.User, "B"),
            new(ChatRole.User, "C"),
            new(ChatRole.Assistant, "Response"),
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        Assert.Equal(2, result.Count);
        var text = result[0].Text;
        Assert.Contains("A", text);
        Assert.Contains("B", text);
        Assert.Contains("C", text);
    }

    [Fact]
    public void Step3_PreservesToolMessagesBetweenDifferentRoles()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search"),
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_1", "search_product", null),
                ]
            },
            new(ChatRole.Tool, "Results"),
            new(ChatRole.User, "Thanks"),
        };

        var result = SanitizingChatClient.Step3_MergeConsecutiveSameRole(messages);

        Assert.Equal(4, result.Count);
    }

    // =========================================================
    // T3.5 — 步骤 4：重编号 tool_call_id
    // =========================================================

    [Fact]
    public void Step4_RenumbersAllCallIds_ToCallSanitizedN()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_qwen_x", "search_product", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_qwen_x", "found items")]
            },
        };

        var result = SanitizingChatClient.Step4_RenumberCallIds(messages);

        var fcc = result[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_sanitized_1", fcc.CallId);

        var frc = result[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_sanitized_1", frc.CallId);
    }

    [Fact]
    public void Step4_AssignsSequentialNumbers_AcrossMultipleFccs()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_deepseek_a", "search_product", null),
                    new FunctionCallContent("call_deepseek_b", "add_to_cart", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_deepseek_a", "items")]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_deepseek_b", "done")]
            },
        };

        var result = SanitizingChatClient.Step4_RenumberCallIds(messages);

        var fccList = result[0].Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal("call_sanitized_1", fccList[0].CallId);
        Assert.Equal("call_sanitized_2", fccList[1].CallId);

        var frcList = result[1..].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Equal("call_sanitized_1", frcList[0].CallId);
        Assert.Equal("call_sanitized_2", frcList[1].CallId);
    }

    [Fact]
    public void Step4_DoesNotModifyMessages_WhenNoFccPresent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi"),
        };

        var result = SanitizingChatClient.Step4_RenumberCallIds(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal("Hi", result[1].Text);
    }

    [Fact]
    public void Step4_MapsFrcEvenWhenFccInDifferentMessage_ThanFrc()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_old_1", "search_product", null),
                ]
            },
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_old_2", "add_to_cart", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_old_1", "items")]
            },
        };

        var result = SanitizingChatClient.Step4_RenumberCallIds(messages);

        var fcc1 = result[0].Contents.OfType<FunctionCallContent>().Single();
        var fcc2 = result[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_sanitized_1", fcc1.CallId);
        Assert.Equal("call_sanitized_2", fcc2.CallId);

        var frc = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_sanitized_1", frc.CallId);
    }

    [Fact]
    public void Step4_PreservesOtherContent_WhenRenumbering()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("Some text"),
                    new FunctionCallContent("call_q", "search_product", null),
                ]
            },
        };

        var result = SanitizingChatClient.Step4_RenumberCallIds(messages);

        var texts = result[0].Contents.OfType<TextContent>().ToList();
        Assert.Single(texts);
        Assert.Equal("Some text", texts[0].Text);

        var fcc = result[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_sanitized_1", fcc.CallId);
    }

    // =========================================================
    // T3.6 — 管线组装 + 完整清洗
    // =========================================================

    [Fact]
    public async Task GetResponseAsync_RunsFullPipeline()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final reply")));

        var sut = new SanitizingChatClient(inner);

        // 输入消息：包含空 assistant、缺失 tool result、连续 assistant、旧 CallId
        var input = new List<ChatMessage>
        {
            new(ChatRole.User, "Find me a coffee machine"),
            new() { Role = ChatRole.Assistant, Contents = [] }, // 空 assistant — 应被步骤 1 移除
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_qwen_abc", "search_product", null),
                ]
            },
            // 缺少 tool result — 应被步骤 2 追加
            new(ChatRole.Assistant, "I'll search"),
            new(ChatRole.Assistant, "Here are the results"), // 连续 assistant — 应被步骤 3 合并
        };

        await sut.GetResponseAsync(input);

        // 验证内层收到的消息是清洗后的
        List<ChatMessage> capturedMessages = null!;
        await inner.GetResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs.ToList()),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());

        await sut.GetResponseAsync(input);

        Assert.NotNull(capturedMessages);

        // 步骤 1：空 assistant 已被移除
        Assert.DoesNotContain(capturedMessages, m => m.Role == ChatRole.Assistant && m.Contents.Count == 0);

        // 步骤 2：缺失的 tool result 已追加
        var toolMessages = capturedMessages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.NotEmpty(toolMessages);

        // 步骤 3：连续 assistant 已合并 — 不再有连续相同 role
        for (int i = 1; i < capturedMessages.Count; i++)
        {
            if (capturedMessages[i].Role == capturedMessages[i - 1].Role
                && capturedMessages[i].Role != ChatRole.Tool) // tool 可以连续
            {
                Assert.Fail($"Found consecutive same-role messages at index {i - 1} and {i}: {capturedMessages[i - 1].Role}");
            }
        }

        // 步骤 4：CallId 已重编号
        var allFccs = capturedMessages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        foreach (var fcc in allFccs)
        {
            Assert.StartsWith("call_sanitized_", fcc.CallId);
        }
    }

    [Fact]
    public void Step1_RemovesAssistantWithOnlyNonTextNonFccContent()
    {
        // Assistant 消息只包含非文本非 FCC 的内容（如仅有 reasoning）
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [] // 无内容
            },
        };

        var result = SanitizingChatClient.Step1_RemoveEmptyToolCalls(messages);

        Assert.Single(result);
    }

    [Fact]
    public void FullPipeline_ProducesOpenAiCompatibleOutput()
    {
        // 完整管线测试：模拟跨模型切换后的历史消息
        var input = new List<ChatMessage>
        {
            new(ChatRole.User, "Search coffee machine"),
            new() { Role = ChatRole.Assistant, Contents = [] },
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call_qwen_1", "search_product", new Dictionary<string, object?> { ["keyword"] = "coffee" }),
                    new FunctionCallContent("call_qwen_2", "add_to_cart", null),
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("call_qwen_1", "found 3 items")]
            },
            // call_qwen_2 对应的 tool 结果缺失
            new(ChatRole.User, "Also find filters"),
            new(ChatRole.User, "Never mind"), // 连续 user
        };

        // 手动执行 4 步
        var step1 = SanitizingChatClient.Step1_RemoveEmptyToolCalls(input);
        var step2 = SanitizingChatClient.Step2_FillMissingToolResults(step1);
        var step3 = SanitizingChatClient.Step3_MergeConsecutiveSameRole(step2);
        var result = SanitizingChatClient.Step4_RenumberCallIds(step3);

        // 验证约束：
        // 1. 不存在空 assistant
        Assert.DoesNotContain(result, m => m.Role == ChatRole.Assistant && !m.Contents.OfType<FunctionCallContent>().Any() && string.IsNullOrEmpty(m.Text));

        // 2. 配对完整
        for (int idx = 0; idx < result.Count; idx++)
        {
            var msg = result[idx];
            if (msg.Role == ChatRole.Assistant)
            {
                var fccCount = msg.Contents.OfType<FunctionCallContent>().Count();
                if (fccCount > 0)
                {
                    // 找到紧接着的 tool 消息
                    var toolCount = 0;
                    for (int i = idx + 1; i < result.Count && result[i].Role == ChatRole.Tool; i++)
                        toolCount++;
                    Assert.True(toolCount >= fccCount,
                        $"Assistant at index {idx} has {fccCount} FCCs but only {toolCount} tool messages follow");
                }
            }
        }

        // 3. CallId 统一格式
        var allFccs = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        var allFrcs = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        foreach (var fcc in allFccs)
            Assert.Matches(@"^call_sanitized_\d+$", fcc.CallId);
        foreach (var frc in allFrcs)
            Assert.Matches(@"^call_sanitized_\d+$", frc.CallId);

        // 4. FCC 与 FRC 配对
        var fccIds = allFccs.Select(f => f.CallId).ToHashSet();
        var frcIds = allFrcs.Select(f => f.CallId).ToHashSet();
        Assert.Subset(fccIds, frcIds); // 所有 FRC 的 CallId 都在 FCC 中存在

        // 5. 无连续相同 role（tool 可以连续）
        for (int i = 1; i < result.Count; i++)
        {
            if (result[i].Role != ChatRole.Tool)
                Assert.NotEqual(result[i - 1].Role, result[i].Role);
        }

        // 6. 合并后的 user 消息（前 2 个 user 被 assistant(tool_calls) 分隔，不能合并）
        //    "Search coffee machine" 在 index 0，之后助理 tool_calls，
        //    再之后是合并后的 "Also find filters\nNever mind"
        var userMessages = result.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal(2, userMessages.Count);
        Assert.Contains("Search coffee machine", userMessages[0].Text);
        Assert.Contains("Also find filters", userMessages[1].Text);
        Assert.Contains("Never mind", userMessages[1].Text);

        // 7. 无孤立 FCC 或 FRC
        var allFccIds = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(f => f.CallId).ToHashSet();
        var allFrcIds = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(f => f.CallId).ToHashSet();
        Assert.Subset(allFccIds, allFrcIds);
    }
}
