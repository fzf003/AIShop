#pragma warning disable MAAI001 // 上下文压缩 API（CompactionProvider / ContextWindowCompactionStrategy）为 MAF [Experimental]
using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Services;
using AIShop.Service.Providers;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Agents;

/// <summary>
/// AGUIShoppingAgent 装配（agui-host T4 + T8）：从零设计的新购物 <see cref="ChatClientAgent"/>。
/// 形态 = <c>chatClient.AsAIAgent(name: "AGUIShopping", instructions, tools)</c>（T8 起经
/// <see cref="ChatClientAgentOptions"/> 重载构造，等价于位置签名并额外挂 AIContextProviders），工具复用
/// <see cref="CartToolProvider.CreateTools()"/>（5 购物工具，与老 Agent 同源）。
/// 会话由 AG-UI <c>AgentSessionStore</c>（ThreadId）承载，无自管 session 字典、无 StateBag 偏好注入；
/// 不复用旧 ShoppingAssistantAgent 的 HarnessAgent 外壳与 <c>Reply + Keywords + Preferences</c> JSON 回复协议。
/// T8 起装配 <see cref="CompactionProvider"/> 上下文压缩（阈值对齐老 ShoppingAssistantAgent），见 Create 内注释。
/// </summary>
/// <remarks>
/// AG-UI 为 preview 包（Microsoft.Agents.AI 1.20.0）：核心包 <b>没有 <c>WithTools</c></b> 扩展，
/// 挂工具经 <see cref="ChatClientExtensions.AsAIAgent"/> 的 <c>tools</c> 参数直接写入 Agent 默认
/// <see cref="ChatOptions"/>（API 面以本地镜像 preview 源码为准）。
/// T7 起 AguiHost/Program.cs 不再直接调用本方法构造局部 agent，而是把它作为 <b>keyed factory body</b>
/// 经 <c>AddKeyedSingleton&lt;AIAgent&gt;(AgentName, ...)</c> 注册进 DI（签名不变）：供 AG-UI <c>MapAGUIServer(agentName, pattern)</c>
/// 按名解析与 DevUI <c>/v1/entities</c> 实体发现共用同一装配产物。
/// </remarks>
internal static class AGUIShoppingAgent
{
    /// <summary>Agent 名称（AG-UI 会话/遥测标识）。</summary>
    internal const string AgentName = "AGUIShopping";

    /// <summary>
    /// T14 工具循环护栏上限：对齐老 ShoppingAssistantAgent 的 HarnessAgentOptions.MaximumIterationsPerRequest = 3
    /// （ShoppingAssistantAgent.cs L162）。含义 = 单次请求（单个 AG-UI run，对应老一次 RunChatAsync）内模型请求
    /// 工具调用的迭代数上限，超限即终止循环而非无限执行，防 FICC/工具失控与无限 token 消耗。
    /// </summary>
    internal const int MaximumToolIterations = 3;

    /// <summary>
    /// 自然语言购物人设 instructions（面向 AG-UI 会话）：先 <c>search_product</c> 检索商品 → 命中后用工具输出中的商品
    /// 编号加购 → 查车/改量/移除。全程简体中文、工具驱动，明确不使用旧 <c>Reply/Keywords/Preferences</c> JSON 结构化回复协议。
    /// </summary>
    internal const string DefaultInstructions =
        """
        你是 AIShop 线上商城的购物助手。请始终使用简体中文回复，语气自然、简洁，直接帮用户办成事。

        你可以使用以下工具（参数与描述详见各工具说明）：
        - search_product：检索商品。用户表达想要/寻找某类商品时，先调用它搜索，再基于真实检索结果介绍，绝不凭空编造商品。
        - add_to_cart：把某件商品加入购物车（数量在现有基础上追加）。
        - update_cart_quantity：把购物车中某商品的数量设置成用户要求的最终值（如「只要 2 件」）。
        - get_cart_summary：查看当前购物车的内容与合计。
        - remove_from_cart：从购物车移除某个商品条目。

        请遵循以下工作方式：
        1. 用户提出购物/寻找请求 → 先 search_product，命中后在回复中说明商品名称与价格（不要在回复中出现商品内部编号），再按用户意图推进购买。
        2. 用户表达要买/加购某商品 → 用检索结果中的商品编号直接 add_to_cart，不要反复确认；
           已加购过的商品不要重复加购，如需调整数量改用 update_cart_quantity。
        3. 用户查看购物车 → get_cart_summary；改数量(update_cart_quantity)/移除(remove_from_cart) → 相应工具，不要用自然语言假装完成。
        4. 每次执行工具后给用户一句自然的文字反馈（加购成功、车内现有商品等），不要沉默，也不要用冗长解释替代行动。
        5. 用户身份与购物车由系统自动关联，无需向用户询问任何登录信息。

        6. 面向用户的回复输出规约（用户明确要求，优先级最高）：
           - 一律为简体中文纯文本，禁止任何 Markdown 标记：不要用加粗或斜体（**、*、__），
             不要用列表符号（-、*、数字加点的项目列表），不要用标题（#），不要用代码块（```）。
           - 简洁自然：每执行完一步工具后，紧跟一句自然的文字说明结果（如「已为您加入购物车」「购物车当前共 1 件，合计 ¥129.99」），
             不要输出分点清单，不要输出 Markdown 列表，不要长篇大论。
           - 不要在回复中向用户展示任何商品内部编号 / 商品 ID（工具检索结果里的编号仅用于内部加购等操作）。
             需要指代某件商品时，只使用商品名称与价格即可，例如「专业跑鞋，¥129.99」，不要写成「专业跑鞋（编号 3）」。
        """;

    /// <summary>
    /// 从 <paramref name="chatClient"/> 装配 AGUIShoppingAgent 的 <see cref="AIAgent"/>（运行时类型为
    /// <see cref="ChatClientAgent"/>）。供 AguiHost/Program.cs（T5）与宿主级测试复用；测试可注入 NSubstitute
    /// <see cref="IChatClient"/> 走离线链路。
    /// </summary>
    /// <param name="chatClient">底层对话客户端（AguiHost 装配时 = ModelRouter.GetDefaultChatClient() 的 DI 单例）。</param>
    /// <param name="cartTools">5 购物工具工厂（<see cref="CartToolProvider.CreateTools"/>，与老 Agent 同源）。</param>
    /// <param name="telemetryOptions">Agent 遥测选项（<c>AgentTelemetry</c> 配置节绑定，含 SourceName/Level）；
    /// 由 Program keyed factory 从 DI 解析传入，测试按需构造。</param>
    /// <param name="instructionsOverride">覆盖人设 instructions（测试注入）；null 时用 <see cref="DefaultInstructions"/>。</param>
    /// <param name="memoryService">Mem0 记忆服务（T13，可选）；与 <paramref name="currentUser"/> 均非 null 时挂载
    /// <see cref="MemoryContextProvider"/>（跨会话记忆读注入 + 轮后写入触发），null 时维持仅上下文压缩装配。</param>
    /// <param name="currentUser">当前用户访问器（T13，可选）；<see cref="MemoryContextProvider"/> 依赖其按用户读写记忆。</param>
    /// <param name="compactionStrategy">上下文压缩策略（S1，可选）；由 Program keyed factory 注入 DI 单例
    /// （<see cref="AguiCompaction.CreateStrategy"/> 构造，与 S4 的 store 侧压缩共用同一实例）。null 时回退
    /// <see cref="AguiCompaction.CreateStrategy"/>（保证阈值单一来源，直构调用点源码兼容）。</param>
    /// <returns>装配完成的新购物 Agent（<see cref="ChatClientAgent"/> 经 <c>AgentTelemetry.Instrument</c> 包装，
    /// 运行时类型为 <c>OpenTelemetryAgent</c>；Level=None 时裸返回 <see cref="ChatClientAgent"/>，由 AG-UI AgentSessionStore 承载会话）。</returns>
    internal static AIAgent Create(
        IChatClient chatClient,
        CartToolProvider cartTools,
        AgentTelemetryOptions telemetryOptions,
        string? instructionsOverride = null,
        IMemoryService? memoryService = null,
        ICurrentUserAccessor? currentUser = null,
        CompactionStrategy? compactionStrategy = null)
    {
        var instructions = instructionsOverride ?? DefaultInstructions;

        // 上下文压缩（T8，评审补齐缺口 + S1 阈值单一来源）：现装配无压缩、长对话上下文会无限膨胀；老
        // ShoppingAssistantAgent 挂了 CompactionProvider + ContextWindowCompactionStrategy。阈值上提至
        // AguiCompaction 单一来源（对齐老 Agent 128000/16384/0.5/0.8，消除配置漂移）：注入者用注入实例
        // （DI 单例，S4 的 store 侧压缩复用同一实例），未注入（直构调用点）回退 AguiCompaction.CreateStrategy()。
        // stateKey 显式给 "AGUIShopping-Compaction"：CompactionProvider 状态存 AgentSession.StateBag，缺省按策略
        // 类型名（ContextWindowCompactionStrategy）作 key，多个 agent 同 session 会话会互相覆盖，显式 key 隔离。
        var compactionProvider = new CompactionProvider(
            compactionStrategy ?? AguiCompaction.CreateStrategy(),
            stateKey: "AGUIShopping-Compaction");

        // AIContextProviders：压缩 provider 恒挂；记忆 provider 条件挂载（记忆服务 + 当前用户访问器均可用时）。
        var contextProviders = new List<AIContextProvider> { compactionProvider };

        // Mem0 跨会话记忆（T13）：记忆服务与用户访问器均非 null 时，把 MemoryContextProvider（AIShop.Service.Providers，
        // 老 ShoppingAssistantAgent 同款 Provider）追加进 AIContextProviders，与 CompactionProvider 并列。
        // 读写时机由 ChatClientAgent 驱动：run 开始 Provide（语义召回当前用户记忆注入 Instructions）、run 结束
        // Store（把本轮用户消息经 Mem0 提取落独立记忆库，见 Service/Providers/MemoryContextProvider）。两依赖任一
        // 为 null（如记忆模型缺失降级）→ 不挂记忆、仅保留上下文压缩（T4/T8 装配断言不回退）。
        if (memoryService is not null && currentUser is not null)
        {
            contextProviders.Add(new MemoryContextProvider(memoryService, currentUser));
        }

        // AG-UI 官方宿主形态：位置签名 AsAIAgent(instructions, name, description, tools, ...) 实为包一层
        // ChatClientAgentOptions；这里直接构造 ChatClientAgentOptions（Name/ChatOptions/AIContextProviders），
        // 以便把压缩/记忆 provider 经 AIContextProviders 挂入。ChatOptions.Tools 承载 5 购物工具。
        var options = new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = cartTools.CreateTools()
            },
            AIContextProviders = contextProviders
        };

        var agent = chatClient.AsAIAgent(options);

        // T14（工具循环护栏）：给工具迭代循环设硬上限 3，防模型反复请求工具导致失控循环/无限 token 消耗。
        // 必须在 Instrument 之前应用——此时 agent 仍是裸 ChatClientAgent（.ChatClient 读面可达），而
        // OpenTelemetryAgent 装饰器不暴露内层 ChatClient。见 ApplyToolIterationLimit 内注释。
        ApplyToolIterationLimit(agent);

        // T11（agent 遥测埋点）：返回前用 AIShop.AgentTelemetry.AgentTelemetry.Instrument 包装，让 AGUIShopping
        // 执行（Run/RunStreaming）产生 OpenTelemetry span，供 Aspire Dashboard 观察——对齐老 ShoppingAssistantAgent
        // 构造 L188 的 Instrument(agent, SourceName, Level) 用法。Level=None 时裸返回原 ChatClientAgent（不包装饰器），
        // 其余级别包装为 OpenTelemetryAgent（继承 AIAgent，与 ChatClientAgent 无继承关系，故返回类型为 AIAgent）。
        return AIShop.AgentTelemetry.AgentTelemetry.Instrument(
            agent,
            telemetryOptions.SourceName,
            telemetryOptions.Level);
    }

    /// <summary>
    /// T14 工具循环护栏：把 <paramref name="agent"/> 的工具迭代上限设为 <see cref="MaximumToolIterations"/>（3）。
    /// ChatClientAgentOptions 没有老 Harness 的 MaximumIterationsPerRequest 装配旋钮（T14 实证），工具循环跑在 MAF
    /// 默认中间件注入的 MEAI <see cref="FunctionInvokingChatClient"/>（FICC）里——镜像 ChatClientExtensions.
    /// WithDefaultAgentMiddleware 以 <c>new FunctionInvokingChatClient(...)</c> 注入、未显式设上限时 MEAI 10.9.0
    /// 默认 <b>40</b>（10.9.0 XML 文档实证；tasks/早期实证记为 5 系版本差异，以实际包为准）。这里在装配后从 agent
    /// 自身 ChatClient 管线解析 FICC 实例并设 MaximumIterationsPerRequest=3（与老 MaximumIterationsPerRequest=3 语义
    /// 对齐：单次请求内工具迭代上限，默认 40 → 收到 3）。MAF WithDefaultAgentMiddleware 构建后即用
    /// <c>GetService&lt;FunctionInvokingChatClient&gt;()</c> 设 AdditionalTools，此处沿用同一解析缝改迭代上限。
    /// 解析不到 FICC = 装配形态异常（如 UseProvidedChatClientAsIs 直通未注入 FICC），fail-fast 抛明确异常，
    /// 避免静默以默认上限运行而护栏失效。
    /// </summary>
    /// <param name="agent">已由 <c>chatClient.AsAIAgent(options)</c> 装配的裸 ChatClientAgent（未 Instrument 包装）。</param>
    /// <exception cref="InvalidOperationException">agent 的 ChatClient 管线解析不到 FICC 时抛出。</exception>
    internal static void ApplyToolIterationLimit(ChatClientAgent agent)
    {
        var functionInvoker = agent.ChatClient.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException(
                $"AGUIShopping 装配异常：ChatClientAgent 管线未解析到 {nameof(FunctionInvokingChatClient)}，" +
                $"无法设置工具循环上限 {MaximumToolIterations}。");
        functionInvoker.MaximumIterationsPerRequest = MaximumToolIterations;
    }
}
