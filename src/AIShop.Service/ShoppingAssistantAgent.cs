#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Infrastructure.Data;
using AIShop.Service.Providers;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Serilog;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIShop.Service;

public sealed class ShoppingAssistantAgent : IShoppingAssistantAgent
{
    // _agent readonly：T13 测试缝——internal 构造在 public 构造委托后包装 _agent
    // （见 internal 构造，agentWrapper 参数），readonly 允许构造器内多次赋值；字段仅在构造期赋值
    private readonly AIAgent _agent;
    private readonly SqliteChatHistoryProvider _provider;
    private readonly bool _isOpenAI;
    private static readonly Serilog.ILogger Logger = Log.ForContext<ShoppingAssistantAgent>();

    internal static bool IsOpenAIModel(string model) =>
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3-", StringComparison.OrdinalIgnoreCase);

    // R4/R5/R9：清洗 LLM 回复中的商品 ID 展示（#5、商品Id:4、商品ID为4 等）
    // 与 ChatEndpoints.cs 中的正则一致，用于流式增量清洗
    private static readonly Regex HashIdPattern =
        new(@"#(?<id>\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex FixedIdPattern =
        new(@"商品Id[:：]\d+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ProductIdLabelPattern =
        new(@"商品ID[\s:：为是]*\d+", RegexOptions.None, TimeSpan.FromSeconds(1));
    private const int MinProductId = 1;
    private const int MaxProductId = 18;

    /// <summary>
    /// 清洗 LLM 回复文本（R4/R5/R9）：去除商品 ID 展示，只清洗 Reply 字符串。
    /// </summary>
    private static string SanitizeReply(string? reply)
    {
        // T14：委托 ApplySanitizePatterns（三路替换）后 Trim。
        // 收敛为复用而非内联，使 ApplySanitizePatterns 保持存活（tasks.md T14：方法本身保留），行为与内联零变化
        return ApplySanitizePatterns(reply ?? "").Trim();
    }

    private static bool IsProductId(string idText) =>
        int.TryParse(idText, out var id) && id is >= MinProductId and <= MaxProductId;

    private static string BuildInstructions(IReadOnlyDictionary<string, string[]> keywordMap)
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
        // R9：固定商品 ID 的唯一合法展示格式，配合服务端 SanitizeReply 精确删除，杜绝 ID 泄漏。
        lines.Add("3. 回复文本中不要出现商品编号。若确需提及，必须且只能使用格式『商品Id:N』（如 商品Id:4）；");
        lines.Add("   任何其他形式（商品ID为4、#4、编号4、ID：4 等）均属违例。");
        lines.Add("");
        lines.Add("【JSON 输出格式要求】");
        lines.Add($"回复必须使用以下 JSON 格式（工具调用时除外）：");
        lines.Add($"{outputExampleJson}");

        lines.Add("");
        lines.Add("【商品关键词表（用于推荐栏）】");
        lines.Add("关键词 | 覆盖标签");

        foreach (var (key, tags) in keywordMap)
        {
            lines.Add($"{key} | {string.Join("、", tags)}");
        }

        return string.Join("\n", lines);
    }

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory,
        IReadOnlyDictionary<string, string[]> keywordMap, CartToolProvider cartTools, bool isOpenAI,
        AgentTelemetryOptions telemetryOptions)
    {
        _isOpenAI = isOpenAI;
        var instructions = BuildInstructions(keywordMap);


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

        var chartOptions = new ChatOptions { Tools = tools, Reasoning = new() { Effort = ReasoningEffort.Medium } };

        // T10：Provider 存为字段（不只内联传给 ChatHistoryProvider）——
        // RunChatAsync 在 Run 正常返回后需调用 _provider.MarkRoundFinalAsync(runId) 兜底补标本轮终点
        _provider = new SqliteChatHistoryProvider(dbFactory, (session) => {

            if (session!.TryGetInMemoryChatHistory(out var chatHistory) && chatHistory is { Count: > 0 }) 
            {
                return new SqliteChatHistoryProvider.State() { Messages = chatHistory };
            }

            return new SqliteChatHistoryProvider.State();

        }, stateKey: "ShoppingAssistant");

        var options = new HarnessAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            HarnessInstructions = instructions,
            ChatOptions = chartOptions,
            ChatHistoryProvider = _provider,

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

        // 创建 HarnessAgent 后立即以 AgentTelemetryOptions（SourceName + Level）包装：
        // 复用官方 AgentTelemetry 模式开启 MAF 内建 OpenTelemetryAgent 的内容采集
        // （默认 Metadata 仅采集元数据，生产安全；改 MetadataAndContent 即可在 Aspire 看到请求/回复内容）
        // 注：Instrument 返回 OpenTelemetryAgent（继承自 AIAgent，与 HarnessAgent 无继承关系），
        //     故 _agent 字段类型为 AIAgent，不能强转回 HarnessAgent；
        //     命名空间 AIShop.AgentTelemetry 与本类同名，用完整限定名调用静态类 AgentTelemetry.Instrument。
        _agent = AIShop.AgentTelemetry.AgentTelemetry.Instrument(
            _agent,
            telemetryOptions.SourceName,
            telemetryOptions.Level);
    }

    /// <summary>
    /// T13 测试缝（internal，经 InternalsVisibleTo 对测试项目可见）：比 public 构造多一个
    /// <paramref name="agentWrapper"/> 参数，让测试可包装真实 _agent（如首次 CreateSessionAsync 抛异常），
    /// 验证 RunChatStreamAsync 会话创建失败降级到 RunChatAsync 路径。
    /// public 构造委托本构造并传 null，行为与接口契约零变化（spec Requirement 7：RunChatStreamAsync 签名不变）。
    /// </summary>
    internal ShoppingAssistantAgent(
        IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory,
        IReadOnlyDictionary<string, string[]> keywordMap, CartToolProvider cartTools, bool isOpenAI,
        AgentTelemetryOptions telemetryOptions, Func<AIAgent, AIAgent>? agentWrapper)
        : this(chatClient, dbFactory, keywordMap, cartTools, isOpenAI, telemetryOptions)
    {
        if (agentWrapper is not null)
            _agent = agentWrapper(_agent);
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
        Guid sessionId, string userMessage, string username,
        string? preferences = null, CancellationToken ct = default)
    {
        CartToolProvider.SetCurrentUser(username);

        var sw = Stopwatch.StartNew();
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        // T10：每轮开始生成唯一 run_id 写入 StateBag（一次 Run 只生成一次，spec「RunChatAsync
        // 每轮开始生成 run_id 写 StateBag」）——Provider.Store 从 StateBag 读同一值，
        // 为一次 Run 的所有 FICC 迭代写入的消息行打同一轮次标记
        var runId = Guid.NewGuid();
        session.StateBag.SetValue("RunId", runId.ToString());

        // 会话重建回填：从数据库加载的历史偏好经端点传入，写入 StateBag，
        // PreferenceMemoryProvider 在本次运行即可读取并注入 LLM 上下文。
        if (!string.IsNullOrWhiteSpace(preferences))
            session.StateBag.SetValue("Preferences", preferences);

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

        // T10：Run 正常返回后兜底补标本轮终点（spec「Run 后兜底补标 is_final」）——
        // 若 Store 内判定（T5）未落 is_final=true 行（如仅调工具未输出文本），
        // 由 Provider 将本轮末条补标为轮次终点；补标收敛在 Provider.MarkRoundFinalAsync，
        // Agent 不直接操作 DbContext（评审 Y3）。异常中断时不会执行到此，该轮天然视为未完成
        await _provider.MarkRoundFinalAsync(runId, ct);

        return (result ?? new AgentChatResult(rawText ?? "", [], null), session);
    }

    /// <summary>
    /// 执行一次 Agent 对话（流式版本）。
    /// 调用 MAF RunStreamingAsync 获取 IAsyncEnumerable&lt;AgentResponseUpdate&gt;，
    /// 以原生流式迭代器内联边收边 yield：对每个 text delta 执行增量 SanitizeReplyIncremental 清洗后
    /// 立即 yield 为 ChatStreamChunk{TextDelta, IsComplete=false}，不收集到 List（真流式）。
    /// 流结束后冲洗缓冲残留文本、解析完整文本提取 JSON 得到 AgentChatResult，
    /// yield 最终完整结果 chunk，最后调用 _provider.MarkRoundFinalAsync 兜底补标。
    ///
    /// 异常语义：会话创建阶段 try-catch（失败降级到 RunChatAsync 返回单个 chunk）；
    /// 迭代循环不 try-catch——异常自然传播给端点 catch 处理（降级/重试），
    /// MarkRoundFinalAsync 不执行 → 该轮视为未完成，与 RunChatAsync 异常语义一致。
    /// 流式过程中不执行工具调用（FICC 内部处理，文本增量只含最终回复）。
    /// </summary>
    public async IAsyncEnumerable<ChatStreamChunk> RunChatStreamAsync(
        Guid sessionId, string userMessage, string username,
        string? preferences = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        CartToolProvider.SetCurrentUser(username);

        // 会话创建（失败直接降级，无需 MarkRoundFinal）
        AgentSession? session = null;
        try
        {
            session = await _agent.CreateSessionAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "创建流式会话失败，降级到非流式");
        }

        if (session is null)
        {
            var (fallbackResult, _) = await RunChatAsync(sessionId, userMessage, username, preferences, ct);
            yield return new ChatStreamChunk { TextDelta = fallbackResult.Reply, IsComplete = false };
            yield return new ChatStreamChunk { TextDelta = "", IsComplete = true, FullResult = fallbackResult };
            yield break;
        }

        session.StateBag.SetValue("SessionId", sessionId.ToString());
        var runId = Guid.NewGuid();
        session.StateBag.SetValue("RunId", runId.ToString());
        if (!string.IsNullOrWhiteSpace(preferences))
            session.StateBag.SetValue("Preferences", preferences);

        // 原生流式：内联迭代循环，边收边 yield（不收集到 List）。
        // 迭代循环不 try-catch——异常自然传播给端点 catch 处理，MarkRoundFinalAsync 不执行 → 该轮视为未完成
        var sw = Stopwatch.StartNew();
        var unflushed = "";
        // 累积完整原始文本：原生流式下增量文本不写回 session history，
        // 流结束的完整结果须从累积文本解析（T11 修复：ParseFinalResult(session) 在此返回空）
        var fullTextBuilder = new StringBuilder();
        await foreach (var text in _agent
            .RunStreamingAsync(userMessage, session, cancellationToken: ct)
            .Select(u => u.Text))
        {
            if (string.IsNullOrEmpty(text))
                continue;

            fullTextBuilder.Append(text);

            var (cleaned, remaining) = SanitizeReplyIncremental(text, unflushed);
            unflushed = remaining;

            if (!string.IsNullOrEmpty(cleaned))
                yield return new ChatStreamChunk { TextDelta = cleaned, IsComplete = false };
        }
        sw.Stop();

        Logger.Information("[Diagnose] 流式Agent调用总耗时 AgentCall={ElapsedMs}ms SessionId={SessionId}",
            sw.ElapsedMilliseconds, sessionId);

        // 流结束：冲洗缓冲区中残留的已清洗文本
        if (!string.IsNullOrEmpty(unflushed))
        {
            var finalCleaned = SanitizeReply(unflushed);
            if (!string.IsNullOrEmpty(finalCleaned))
                yield return new ChatStreamChunk { TextDelta = finalCleaned, IsComplete = false };
        }

        // 流结束后：从累积的完整原始文本解析 AgentChatResult
        // （原生流式路径下增量文本不写回 session history，改用累积文本解析）
        var finalResult = ParseFinalResultFromText(fullTextBuilder.ToString());
        yield return new ChatStreamChunk { TextDelta = "", IsComplete = true, FullResult = finalResult };

        await _provider.MarkRoundFinalAsync(runId, ct);
    }

    /// <summary>
    /// 从流式累积的完整原始文本中提取 JSON 解析 AgentChatResult。
    /// 解析失败返回空结果（Reply 兜底为原始文本）。
    /// </summary>
    private static AgentChatResult ParseFinalResultFromText(string rawText)
    {
        var text = rawText.Trim();
        if (string.IsNullOrEmpty(text))
            return new AgentChatResult(text, [], null);

        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
            return new AgentChatResult(text, [], null);

        var json = text[jsonStart..(jsonEnd + 1)];
        try
        {
            return JsonSerializer.Deserialize<AgentChatResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new AgentChatResult(text, [], null);
        }
        catch (JsonException ex)
        {
            Logger.Warning(ex, "流式 Agent 回复 JSON 解析失败");
            return new AgentChatResult(text, [], null);
        }
    }

    /// <summary>
    /// 增量清洗：将新文本追加到缓冲区，尝试去除商品 ID 模式，
    /// 返回安全可发送的前缀和剩余缓冲。
    /// 处理跨 chunk 的模式（如 "#5" 在一个 chunk、"无线" 在下一个 chunk）。
    /// </summary>
    private static (string safeToEmit, string remaining) SanitizeReplyIncremental(string newText, string buffer)
    {
        var fullText = buffer + newText;

        // 逐模式尝试匹配，取最早匹配位置作为安全边界
        int safePos = fullText.Length;

        var fixedMatch = FixedIdPattern.Match(fullText);
        if (fixedMatch.Success && fixedMatch.Index < safePos)
            safePos = fixedMatch.Index;

        var hashMatch = HashIdPattern.Match(fullText);
        if (hashMatch.Success && hashMatch.Index < safePos)
            safePos = hashMatch.Index;

        var labelMatch = ProductIdLabelPattern.Match(fullText);
        if (labelMatch.Success && labelMatch.Index < safePos)
            safePos = labelMatch.Index;

        if (safePos == fullText.Length)
        {
            // 无匹配，检查尾部是否可能是模式前缀（如以 "商品Id" 结尾）
            if (!EndsWithPatternPrefix(fullText))
                return (fullText, "");
            return ("", fullText);
        }

        // 有匹配：safePos 之前的部分已清洗（无模式），安全发送
        var safe = fullText[..safePos];
        // 从 safePos 开始是可能包含模式的区域，保留到下一轮
        var remaining = fullText[safePos..];

        return (safe, remaining);
    }

    private static string ApplySanitizePatterns(string text)
    {
        text = FixedIdPattern.Replace(text, "");
        text = HashIdPattern.Replace(text, static match =>
            IsProductId(match.Groups["id"].Value) ? "" : match.Value);
        text = ProductIdLabelPattern.Replace(text, "");
        return text;
    }

    /// <summary>
    /// 检查文本尾部是否可能是某个清洗模式的前缀。
    /// 如果是，则不能安全 flush，需要等待更多文本。
    /// </summary>
    private static bool EndsWithPatternPrefix(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var tail = text.Length > 10 ? text[^10..] : text;

        // # 后可能跟数字
        if (tail.EndsWith('#')) return true;

        // 商品Id 后可能跟 :N
        if (tail.Contains("商品Id", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("商品ID", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
