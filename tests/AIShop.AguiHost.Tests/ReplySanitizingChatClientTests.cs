using System.Text;
using AIShop.Core.Services;
using AIShop.Service.Agui;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T11（评审方案 A）ReplySanitizingChatClient 服务端清洗单测：验证 agent 输出文本（非流式完整响应 + 流式增量）
/// 在离开 IChatClient 前经 Core ReplySanitizer 清洗商品编号展示（#Id / 商品ID为4 / 商品Id:4 等），
/// 商品名/价格等合法内容保留（对齐 ChatReplySanitizationTests 的既有语义）。
/// </summary>
public sealed class ReplySanitizingChatClientTests
{
    /// <summary>构造以 <paramref name="inner"/> 为底层的清洗中间件。</summary>
    private static ReplySanitizingChatClient BuildWrapper(Meai.IChatClient inner) => new(inner);

    /// <summary>脚本化非流式回复 chatClient：GetResponseAsync 返回含指定文本的 assistant 消息。</summary>
    private static Meai.IChatClient MockResponseClient(string replyText)
    {
        var inner = Substitute.For<Meai.IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, replyText)));
        return inner;
    }

    /// <summary>脚本化流式回复 chatClient：GetStreamingResponseAsync 依次 yield 各文本增量。</summary>
    private static Meai.IChatClient MockStreamingClient(params string[] chunks)
    {
        var inner = Substitute.For<Meai.IChatClient>();
        inner.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamAsync(chunks));
        return inner;
    }

    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamAsync(string[] chunks)
    {
        foreach (var chunk in chunks)
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, chunk);
    }

    [Fact]
    public async Task GetResponseAsync_AssistantTextWithProductIds_StripsIds_KeepsNamesPricesAndPunctuation()
    {
        const string raw =
            "推荐 #3 专业跑鞋（¥129.99）与商品ID为4 意式浓缩咖啡机 ¥349.99；另见 商品Id:4 无线降噪耳机。";
        using var wrapper = BuildWrapper(MockResponseClient(raw));

        var response = await wrapper.GetResponseAsync(Array.Empty<Meai.ChatMessage>());

        Assert.NotNull(response);
        var text = Assert.Single(response.Messages).Text;

        // 商品编号三种形态（#3 / 商品ID为4 / 商品Id:4）均被清洗
        Assert.DoesNotContain("#3", text);
        Assert.DoesNotContain("商品ID", text);
        Assert.DoesNotContain("商品Id:4", text);
        Assert.DoesNotContain("#", text);

        // 商品名与价格保留（清洗不误删名称/价格数字）
        Assert.Contains("专业跑鞋", text);
        Assert.Contains("129.99", text);
        Assert.Contains("意式浓缩咖啡机", text);
        Assert.Contains("349.99", text);
        Assert.Contains("无线降噪耳机", text);
    }

    [Fact]
    public async Task GetResponseAsync_AssistantTextWithoutProductIds_IsUnchanged()
    {
        const string plain = "这款意式浓缩咖啡机值得入手，价格 ¥349.99";
        using var wrapper = BuildWrapper(MockResponseClient(plain));

        var response = await wrapper.GetResponseAsync(Array.Empty<Meai.ChatMessage>());

        Assert.NotNull(response);
        Assert.Equal(plain, Assert.Single(response.Messages).Text);
    }

    [Fact]
    public async Task GetResponseAsync_PreservesNonTextContents_SanitizesOnlyText()
    {
        // assistant 消息同时含文本 + 工具调用内容：清洗只作用于 TextContent，FunctionCallContent 原样保留
        var message = new Meai.ChatMessage
        {
            Role = Meai.ChatRole.Assistant,
            Contents = new List<Meai.AIContent>
            {
                new Meai.TextContent("已为您把 #3 加入购物车"),
                new Meai.FunctionCallContent("call_1", "add_to_cart", new Dictionary<string, object?> { ["productId"] = 3 }),
            },
        };
        var inner = Substitute.For<Meai.IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(message));
        using var wrapper = BuildWrapper(inner);

        var response = await wrapper.GetResponseAsync(Array.Empty<Meai.ChatMessage>());

        Assert.NotNull(response);
        var result = Assert.Single(response.Messages);
        Assert.DoesNotContain("#3", result.Text);
        Assert.Contains("加入购物车", result.Text);
        Assert.Contains(result.Contents, c => c is Meai.FunctionCallContent { Name: "add_to_cart" });
    }

    [Fact]
    public async Task GetStreamingResponseAsync_SplitPatternAcrossChunks_IsSanitized_OnFlush()
    {
        // 跨 chunk 边界拆开 #4（chunk1 只到 #，chunk2 才出现数字）+ 完整变体 商品ID为4 在同一流
        using var wrapper = BuildWrapper(MockStreamingClient("推荐 #", "4 无线降噪耳机（¥199.00）商品ID为4"));

        var builder = new StringBuilder();
        await foreach (var update in wrapper.GetStreamingResponseAsync(Array.Empty<Meai.ChatMessage>()))
            builder.Append(update.Text);

        var text = builder.ToString();

        Assert.DoesNotContain("#4", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("商品ID为4", text);
        Assert.Contains("推荐", text);
        Assert.Contains("无线降噪耳机", text);
        Assert.Contains("199.00", text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_NoProductIds_TextPassesThroughUnchanged()
    {
        const string plain = "这款专业跑鞋很不错，价格 ¥129.99。";
        using var wrapper = BuildWrapper(MockStreamingClient("这款专业跑鞋", "很不错，价格 ¥129.99。"));

        var builder = new StringBuilder();
        await foreach (var update in wrapper.GetStreamingResponseAsync(Array.Empty<Meai.ChatMessage>()))
            builder.Append(update.Text);

        Assert.Equal(plain, builder.ToString());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EndsWithIncompleteHash_FlushRestoresLiteralHash()
    {
        // 流末尾出现孤立的 #（无后续数字、非商品编号）：缓冲在流结束经 Clean 冲洗原样恢复，不丢失内容
        using var wrapper = BuildWrapper(MockStreamingClient("如需帮助请回复 #"));

        var builder = new StringBuilder();
        await foreach (var update in wrapper.GetStreamingResponseAsync(Array.Empty<Meai.ChatMessage>()))
            builder.Append(update.Text);

        Assert.Equal("如需帮助请回复 #", builder.ToString());
    }
}
