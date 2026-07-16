#pragma warning disable MAAI001
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using AIShop.Api.Features.Chat;
using AIShop.Infrastructure.Data;
using AIShop.Core.Interfaces;
using Microsoft.Agents.AI.Compaction;
using Serilog;

namespace AIShop.Api.Agents;

public sealed class ShoppingAssistantAgent : IShoppingAssistantAgent
{
    private readonly HarnessAgent _agent;
    private static readonly Serilog.ILogger Logger = Log.ForContext<ShoppingAssistantAgent>();

    private static string BuildInstructions(IProductCatalogService catalog)
    {
        var lines = new List<string>
        {
            "你是智能购物助手。用中文回复，简洁友好，帮助用户找到心仪的商品。",
            "当前登录用户的用户名可以在对话上下文中获取。",
            "",
            "你可以使用以下工具帮助用户管理购物车：",
            "- add_to_cart: 向购物车添加商品，参数 username=当前登录用户名, productId=商品ID, quantity=数量(默认为1)",
            "- get_cart_summary: 查看购物车内容，参数 username=当前登录用户名",
            "- remove_from_cart: 从购物车移除商品，参数 username=当前登录用户名, itemId=购物车中商品项的ID",
            "- 当用户说\"把这个加入购物车\"时，先确认商品名称，再从推荐面板或商品列表中找到对应 productId，然后调用 add_to_cart",
        };

        lines.Add("");
        lines.Add("【商品关键词参考】");
        lines.Add("当用户表达购物需求时，从下表选择 0-5 个最匹配的关键词：");
        lines.Add("");
        lines.Add("关键词 | 覆盖的商品标签");

        foreach (var (key, tags) in catalog.KeywordMap)
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

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory,
        IProductCatalogService catalog, CartToolProvider cartTools)
    {
        var instructions = BuildInstructions(catalog);

        // 注册购物车工具函数到 Agent
        var tools = new List<AITool>();

        tools.Add(AIFunctionFactory.Create(
            (Func<string, int, int, Task<string>>)cartTools.AddToCartAsync,
            "add_to_cart",
            "向购物车添加商品。当用户表达购买某商品的意愿时调用此工具。参数包含 username(用户名), productId(商品ID), quantity(数量)"));

        tools.Add(AIFunctionFactory.Create(
            (Func<string, Task<string>>)cartTools.GetCartSummaryAsync,
            "get_cart_summary",
            "查看当前用户的购物车摘要。当用户询问购物车内容时调用此工具。参数包含 username(用户名)"));

        tools.Add(AIFunctionFactory.Create(
            (Func<string, Guid, Task<string>>)cartTools.RemoveFromCartAsync,
            "remove_from_cart",
            "从购物车中移除指定商品。当用户表达移除某商品的意愿时调用此工具。参数包含 username(用户名), itemId(购物车中商品项的ID)"));

        var chartOptions = new ChatOptions { Tools = tools };

        var options = new HarnessAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            HarnessInstructions = instructions,
            ChatOptions = chartOptions,
            ChatHistoryProvider = new SqliteChatHistoryProvider(dbFactory),

            // 上下文压缩：保留最近 15 轮对话+system，超出自动丢弃
            DisableCompaction = false,
            CompactionStrategy = new SlidingWindowCompactionStrategy(
                trigger: CompactionTriggers.Always,
                minimumPreservedTurns: 3),

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
        var sw = Stopwatch.StartNew();
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        var response = await _agent.RunAsync<AgentChatResult>(
            userMessage, session,
            options: new AgentRunOptions
            {
                ResponseFormat = ChatResponseFormatJson.ForJsonSchema<AgentChatResult>()
            },
            cancellationToken: ct);

        sw.Stop();
        Logger.Information("[Diagnose] Agent调用总耗时 AgentCall={ElapsedMs}ms SessionId={SessionId}",
            sw.ElapsedMilliseconds, sessionId);
        return (response.Result, session);
    }
}
