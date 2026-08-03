#pragma warning disable MAAI001
using AIShop.Api.Features.Chat;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Serilog;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIShop.Api.Agents;

public sealed class ShoppingAssistantAgent : IShoppingAssistantAgent
{
    private readonly HarnessAgent _agent;
    private readonly bool _isOpenAI;
    private static readonly Serilog.ILogger Logger = Log.ForContext<ShoppingAssistantAgent>();

    internal static bool IsOpenAIModel(string model) =>
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3-", StringComparison.OrdinalIgnoreCase);

    private static string BuildInstructions(IProductCatalogService catalog)
    {
        // 所有模型统一用 Text + Instructions 内嵌 JSON 示例
        // 测试报告证明这是唯一 4 模型（OpenAI/DeepSeek/Qwen/MiMo）100% 兼容的路径
        // 注意：用示例格式而非 Schema 定义，避免 Qwen 复制 Schema 定义到输出中
        var outputExampleJson = JsonSerializer.Serialize(new
        {
            Reply = "你的实际回复内容，禁止使用 Markdown，移除多余 Emoji",
            Keywords = new[] { "关键词1", "关键词2" },
            Preferences = new[] { "偏好1" }
        },
        new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var lines = new List<string>
        {
            "你是购物助手。中文回复，简洁，直接干活。",
            "风格:模仿一些拟人风格，比如：客官请稍等奴家这就为您找合适的产品",
            "用户名自动注入，不用传 username。",
            "",
            "可用工具：",
            "- search_product(keyword): 搜索商品",
            "- add_to_cart(productId, quantity): **追加**商品到购物车（在原数量上加）",
            "- update_cart_quantity(productId, quantity): **设置**精确数量（用户说只要X个时调用）",
            "- get_cart_summary(): 查看购物车",
            "- remove_from_cart(itemId): 从购物车移除商品",
          "",
            "",
            "规则：",
            "- 用户说搜索/想要 → 直接 search_product，不要先说话",
            "- 用户说加购物车/买个 → 直接 add_to_cart(productId, quantity)，不问确认",
            "- 用户说只要X个/改为X个 → 直接 update_cart_quantity，不问确认",
            "- **已执行过的工具调用不要重复执行**（已加购的商品不要再次加购）",
            "- 每次执行完工具后都必须回复一句话，不要沉默",
        };

        // 【回复规范】适用于所有模型
        // 非 OpenAI 模型依赖此约束替代 ForJsonSchema<T>；
        // OpenAI 模型有 ForJsonSchema<T> 兜底，此约束作为双重保障
        lines.Add("");
        lines.Add("【回复规范】");
        lines.Add("1. 当用户的请求需要查询外部信息时，");
        lines.Add("   你必须优先调用提供的工具（Function Calling），");
        lines.Add("   严禁用自然语言或 JSON 描述你要调用工具的动作。");
        lines.Add("2. 当不需要调用工具时，");
        lines.Add("   你必须且只能以标准的 JSON 格式回复，");
        lines.Add("   不要包含任何 Markdown 标记或额外的解释文本。");
        lines.Add("");
        lines.Add("【JSON 输出格式要求】");
        lines.Add($"回复必须使用以下 JSON 格式（工具调用时除外）：");
        lines.Add($"{outputExampleJson}");

        lines.Add("");
        lines.Add("【商品关键词表（用于推荐栏）】");
        lines.Add("关键词 | 覆盖标签");

        foreach (var (key, tags) in catalog.KeywordMap)
        {
            lines.Add($"{key} | {string.Join("、", tags)}");
        }

        return string.Join("\n", lines);
    }

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory,
        IProductCatalogService catalog, CartToolProvider cartTools, bool isOpenAI)
    {
        _isOpenAI = isOpenAI;
        var instructions = BuildInstructions(catalog);


        var tools = new List<AITool>();

        tools.Add(AIFunctionFactory.Create(
            (Func<int, int, Task<string>>)((productId, quantity) => cartTools.AddToCartAsync(productId, quantity)),
            "add_to_cart",
            "追加商品到购物车。参数 productId=商品ID, quantity=追加数量。在现有数量上追加，不是设置最终数量。"));

        tools.Add(AIFunctionFactory.Create(
            (Func<int, int, Task<string>>)((productId, quantity) => cartTools.UpdateCartItemQuantityAsync(productId, quantity)),
            "update_cart_quantity",
            "设置购物车中某个商品的精确数量。参数 productId=商品ID, quantity=最终数量。用户说'只要X个'时调用。"));

        tools.Add(AIFunctionFactory.Create(
            (Func<Task<string>>)(() => cartTools.GetCartSummaryAsync()),
            "get_cart_summary",
            "查看当前用户的购物车摘要，无参数。"));

        tools.Add(AIFunctionFactory.Create(
            (Func<Guid, Task<string>>)(itemId => cartTools.RemoveFromCartAsync(itemId)),
            "remove_from_cart",
            "从购物车中移除指定商品。参数 itemId=购物车中商品项的ID。"));

        tools.Add(AIFunctionFactory.Create(
            (Func<string, Task<string>>)(keyword => cartTools.SearchProductAsync(keyword)),
            "search_product",
            "搜索商品。参数 keyword=商品关键词（如咖啡机、耳机）。用户提到商品名时调用。"));

        var chartOptions = new ChatOptions { Tools = tools };



        var options = new HarnessAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            HarnessInstructions = instructions,
            ChatOptions = chartOptions,
            ChatHistoryProvider = new SqliteChatHistoryProvider(dbFactory),

            DisableCompaction = true,
            MaximumIterationsPerRequest = 3,
              

            DisableToolAutoApproval = false,//DisableToolAutoApproval = false（即默认启用）。设 true 的话，所有工具都不走审批——包括那些本应审批的
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentSkillsProvider = true,
            DisableAgentModeProvider = true,
            DisableApprovalNotRequiredFunctionBypassing = false,
     
            AIContextProviders = [new PreferenceMemoryProvider()]
        };

        _agent = new HarnessAgent(chatClient, options);
    }

    /// <summary>
    /// 执行一次 Agent 对话。
    ///
    /// 所有模型统一走 Text 模式：
    /// - Instructions 中内嵌 JSON Schema 约束输出格式
    /// - 服务端 IndexOf('{') 抠 JSON 反序列化
    /// - 有 ForJsonSchema 支持的模型才额外做 schema 校验加强
    ///
    /// 分叉逻辑：
    /// - OpenAI (gpt-/o1-/o3-)：用 _isOpenAI 加强 ForJsonSchema 格式兜底
    /// - 非 OpenAI（千问/DeepSeek/MiMo）：纯 Text 路径
    /// </summary>
    public async Task<(AgentChatResult Result, AgentSession Session)> RunChatAsync(
        Guid sessionId, string userMessage, string username, CancellationToken ct = default)
    {
        CartToolProvider.SetCurrentUser(username);

        var sw = Stopwatch.StartNew();
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        AgentChatResult? result = null;
        string? rawText = null;

        if (_isOpenAI)
        {
            // OpenAI 路径：走 ForJsonSchema 加强格式校验
            var agentResponse = await _agent.RunAsync<AgentChatResult>(
                userMessage, session,
                cancellationToken: ct);

            sw.Stop();
            Logger.Information("[Diagnose] Agent调用总耗时 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                sw.ElapsedMilliseconds, sessionId);

            result = agentResponse?.Result;
        }
        else
        {
            // 非 OpenAI 路径：纯 Text，由 Instructions 约束 JSON 格式 + 服务端兜底解析
            var response = await _agent.RunAsync(
                userMessage, session,
                cancellationToken: ct);

            sw.Stop();
            Logger.Information("[Diagnose] Agent调用总耗时 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                sw.ElapsedMilliseconds, sessionId);

            rawText = response.Text?.Trim();

            // 兜底：模型（如 Qwen）在 FICC 循环后只调用工具未输出文本
            if (string.IsNullOrEmpty(rawText))
            {
                var toolResults = response.Messages
                    .Where(m => m.Role == ChatRole.Tool)
                    .SelectMany(m => m.Contents.OfType<TextContent>())
                    .Select(tc => tc.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (toolResults.Count > 0)
                    rawText = toolResults[^1];
            }

            if (!string.IsNullOrEmpty(rawText))
            {
                var jsonStart = rawText.IndexOf('{');
                var jsonEnd = rawText.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = rawText[jsonStart..(jsonEnd + 1)];
                    try
                    {
                        result = JsonSerializer.Deserialize<AgentChatResult>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning(ex, "Agent 回复 JSON 解析失败");
                    }
                }
            }
        }

        return (result ?? new AgentChatResult(rawText ?? "", [], null), session);
    }
}
