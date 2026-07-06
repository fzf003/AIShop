#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using AIShop.Api.Features.Chat;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Agents.AI.Compaction;

namespace AIShop.Api.Agents;

public sealed class ShoppingAssistantAgent : IShoppingAssistantAgent
{
    private readonly HarnessAgent _agent;

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
        lines.Add("- 如果从对话中识别到用户的商品偏好（如品类、预算、品牌），请从对话中提取偏好关键词列表填写到 Preferences 字段");

        return string.Join("\n", lines);
    }

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory)
    {
        var options = new HarnessAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            HarnessInstructions = _instructions,
            ChatHistoryProvider = new SqliteChatHistoryProvider(dbFactory),

            // 上下文压缩：保留最近 15 轮对话+system，超出自动丢弃
            DisableCompaction = false,
            CompactionStrategy = new SlidingWindowCompactionStrategy(
                trigger: CompactionTriggers.Always,
                minimumPreservedTurns: 15),

            // Loop 循环评估：Agent 未 [COMPLETE] 时自动追问，最多 5 轮
            // LoopEvaluators = [new CompletionMarkerLoopEvaluator("[COMPLETE]")],
            // MaximumIterationsPerRequest = 2,

            // 明确关闭不需要的内置能力
            DisableToolAutoApproval = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableFileAccess = true,
            DisableTodoProvider = true,
            DisableAgentSkillsProvider = true,
            DisableAgentModeProvider = true,
            DisableNonApprovalRequiredFunctionBypassing = true,
            // 注入会话偏好记忆 Provider
            AIContextProviders = [new PreferenceMemoryProvider()]
        };

        _agent = new HarnessAgent(chatClient, options);
    }

    /// <summary>
    /// 执行一次 Agent 对话。
    /// 返回 (结果, 会话) — 会话引用用于偏好写回。
    /// </summary>
    public async Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, CancellationToken ct = default)
    {
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        var response = await _agent.RunAsync<AgentChatResult>(
            userMessage, session,
            options: new AgentRunOptions
            {
                ResponseFormat = ChatResponseFormatJson.ForJsonSchema<AgentChatResult>()
            },
            cancellationToken: ct);
        return (response.Result, session);
    }
}
