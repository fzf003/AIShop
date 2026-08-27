using AIShop.Core.Models;
using AIShop.Service.Providers;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// ChatMessageMapper 写方向剥离逻辑单元测试。
/// 重点覆盖 StripAgentReplyJson：LLM 输出 {"Reply":...,"Keywords":...,"Preferences":...} JSON 时，
/// 仅把 Reply 纯文本写入 content 字段，防止 AgentChatResult 原始 JSON 泄漏进 chat_messages。
/// </summary>
public sealed class ChatMessageMapperTests
{
    [Fact]
    public void ToStoredMessages_AssistantReplyJson_StripsToPlainReply()
    {
        // LLM 输出合法 JSON 时，Reply 纯文本入库
        var rawJson = "{\"Reply\":\"好的，客官\",\"Keywords\":[\"咖啡\"],\"Preferences\":null}";
        var stored = Map(new[] { "有咖啡吗", rawJson });

        var assistant = Assert.Single(stored, m => m.Role == "assistant");
        Assert.Equal("好的，客官", assistant.Content);
    }

    [Fact]
    public void ToStoredMessages_AssistantReplyJsonWithBareNewline_StripsToPlainReply()
    {
        // LLM 格式漂移：Reply 值内含裸换行（\r\n）构成非法 JSON。
        // 修复前 StripAgentReplyJson 的 JsonDocument.Parse 抛 JsonException 后原样返回，
        // 导致 AgentChatResult 原始 JSON 泄漏进历史（login 加载历史时前端可见 JSON 外壳）。
        var rawJson = "{\r\n  \"Reply\": \"客官，\r\n抱歉呐，小店目前没找到运动套装。奴家给您找找运动上衣、运动裤或者瑜伽服单件可好？\",\r\n  \"Keywords\": [\"运动\", \"健身\", \"瑜伽\"],\r\n  \"Preferences\": [\"运动套装\"]\r\n}";
        var stored = Map(new[] { "有运动套装的没有？", rawJson });

        var assistant = Assert.Single(stored, m => m.Role == "assistant");
        Assert.Equal("客官，\n抱歉呐，小店目前没找到运动套装。奴家给您找找运动上衣、运动裤或者瑜伽服单件可好？", assistant.Content);
    }

    [Fact]
    public void ToStoredMessages_AssistantPlainTextReply_PreservedAsIs()
    {
        // 非 JSON 纯文本回复原样保留（回归保护：剥离逻辑不误伤正常回复）
        var stored = Map(new[] { "你好", "客官您好，有什么可以帮您？" });

        var assistant = Assert.Single(stored, m => m.Role == "assistant");
        Assert.Equal("客官您好，有什么可以帮您？", assistant.Content);
    }

    private static IReadOnlyList<StoredMessage> Map(string[] contents)
    {
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var messages = new[]
        {
            new Meai.ChatMessage(Meai.ChatRole.User, contents[0]),
            new Meai.ChatMessage(Meai.ChatRole.Assistant, contents[1]),
        };
        return ChatMessageMapper.ToStoredMessages(sessionId, runId, messages);
    }
}
