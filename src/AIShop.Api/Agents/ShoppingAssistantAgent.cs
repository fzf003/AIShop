#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using AIShop.Api.Features.Chat;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;

namespace AIShop.Api.Agents;

public sealed class ShoppingAssistantAgent
{
    private readonly AIAgent _agent;

    private static readonly string _instructions = BuildInstructions();

    private static string BuildInstructions()
    {
        var lines = new List<string>
        {
            "你是智能购物助手。用中文回复，简洁友好，帮助用户找到心仪的商品。",
            "",
            "【商品关键词参考】",
            "当用户表达购物需求时，从下表选择 0-5 个最匹配的关键词：",
            "",
            "关键词 | 覆盖的商品标签"
        };

        foreach (var (key, tags) in ProductCatalog.KeywordMap)
        {
            lines.Add($"{key} | {string.Join("、", tags)}");
        }

        lines.Add("");
        lines.Add("规则：");
        lines.Add("- 只能从\"关键词\"列选择");
        lines.Add("- 根据用户对话语义做判断（例如\"运动鞋\"→跑步、运动、鞋子）");
        lines.Add("- 无匹配返回空数组");

        return string.Join("\n", lines);
    }

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory)
    {
        _agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            ChatOptions = new ChatOptions
            {
                Instructions = _instructions
            },
            ChatHistoryProvider = new SqliteChatHistoryProvider(dbFactory)
            {
                 
            },
            ThrowOnChatHistoryProviderConflict = false
        });
    }

    public async Task<AgentChatResult> RunChatAsync(
        Guid sessionId, string userMessage, CancellationToken ct = default)
    {
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        var response = await _agent.RunAsync<AgentChatResult>(
            userMessage, session, options: new AgentRunOptions { ResponseFormat=ChatResponseFormatJson.ForJsonSchema<AgentChatResult>() }, cancellationToken: ct);
        return response.Result;
    }
}
