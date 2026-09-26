using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Services;
using AIShop.Service;
using AIShop.Service.Agui;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T14 工具循环护栏测试：AGUIShopping 的工具迭代循环上限对齐老 ShoppingAssistantAgent（HarnessAgentOptions.
/// MaximumIterationsPerRequest = 3，ShoppingAssistantAgent.cs L162）。
/// ChatClientAgentOptions 无该装配旋钮（T14 实证），工具循环跑在 MAF 默认中间件注入的 MEAI FunctionInvokingChatClient
/// （FICC）里（MEAI 10.9.0 未显式设上限时默认 40）。装配断言 = Create 后经 agent.ChatClient 解析 FICC 读
/// MaximumIterationsPerRequest == 3；行为断言 = 内层模型永不收敛连续请求工具时，FICC 单次请求内层调用次数被限制在
/// 上限而非无限/默认 40 循环。
///
/// <para>T9（盘点 §二·C）：补上「<c>recommend_products</c> × 工具迭代上限」的交互验证 —— 装配侧补传真实
/// <see cref="RecommendationToolProvider"/>（与真实宿主一致），断言工具集含 <c>recommend_products</c> 且上限仍为 3；
/// 行为侧让内层模型反复请求真实 <c>recommend_products</c>，断言循环仍在上限内终止。</para>
/// </summary>
public sealed class AguiToolLoopGuardTests
{
    /// <summary>循环护栏测试用的桩工具名（内层模型永不收敛地请求它，工具函数体只返回固定文本、不触 DB）。</summary>
    private const string LoopToolName = "loop_tool";

    /// <summary>推荐工具名（T9 行为用例中内层模型永不收敛地请求它，走真实 Create 装配的 recommend_products）。</summary>
    private const string RecommendationToolName = "recommend_products";

    /// <summary>构造真实 CartToolProvider（同 AGUIShoppingAgentTests）：仅把 5 购物工具挂到 Agent，函数体不被调用。</summary>
    private static CartToolProvider CreateCartTools()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        return new CartToolProvider(scopeFactory, accessor);
    }

    /// <summary>
    /// 构造真实 RecommendationToolProvider（T9，同 AGUIShoppingAgentTests 的依赖全替身模式）：currentUser 用**真实**
    /// <see cref="CurrentUserAccessor"/>（未 SetCurrentUser → <c>CurrentUser</c> 为 null），工具函数体因此在
    /// **身份缺失分支离线短路**（返回「无法确定用户身份」payload，不触 DB / 语义检索 / 记忆库），使本文件能把
    /// recommend_products 真正挂上并离线反复调用。
    /// <para>注意：<c>currentUser</c> **不能**用 <c>Substitute.For&lt;ICurrentUserAccessor&gt;()</c> —— NSubstitute 对
    /// <c>string</c> 返回成员给的是 <see cref="string.Empty"/> 而非 null，会绕过身份缺失短路走进
    /// <c>RecommendationService</c> 解析（替身 scope 无该服务 → 抛 InvalidOperationException）。</para>
    /// </summary>
    private static RecommendationToolProvider CreateRecommendationTools()
        => new(
            Substitute.For<IServiceScopeFactory>(),
            new CurrentUserAccessor(),
            Substitute.For<IMemoryStore>(),
            new MemoryCache(new MemoryCacheOptions()));

    /// <summary>
    /// 构造「永不收敛」的内层 IChatClient：每次被调都返回请求 <paramref name="toolName"/> 的 assistant 工具调用
    /// （callId 每次唯一），使 FICC 若无上限会一直循环；<paramref name="increment"/> 统计内层真实被调次数。
    /// </summary>
    private static Meai.IChatClient CreateNeverConvergingInner(string toolName, Action increment)
    {
        var inner = Substitute.For<Meai.IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                increment();
                var functionCall = new Meai.FunctionCallContent(
                    Guid.NewGuid().ToString("N"),
                    toolName,
                    new Dictionary<string, object?>());
                var message = new Meai.ChatMessage
                {
                    Role = Meai.ChatRole.Assistant,
                    Contents = [functionCall]
                };
                return new Meai.ChatResponse(new List<Meai.ChatMessage> { message });
            });
        return inner;
    }

    [Fact]
    public void Create_AppliesToolLoopGuard_MaximumIterationsPerRequestIsThree()
    {
        // Level.None：Instrument 裸返回 ChatClientAgent，保留 .ChatClient 读面供解析 FICC。
        // T9：补传真实 RecommendationToolProvider，使本用例的装配与真实宿主一致（9 工具，非仅购物工具）。
        var agent = AGUIShoppingAgent.Create(
            Substitute.For<Meai.IChatClient>(),
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            recommendationTools: CreateRecommendationTools());
        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);

        // T9 装配证据：recommend_products 确实挂上了（证明护栏是在**含推荐工具的真实装配**上生效，
        // 而不是只挂购物工具的子集）。
        var chatOptions = agent.GetService(typeof(Meai.ChatOptions)) as Meai.ChatOptions;
        Assert.NotNull(chatOptions);
        Assert.NotNull(chatOptions.Tools);
        Assert.Contains(chatOptions.Tools, tool => tool.Name == RecommendationToolName);

        // FICC 由 MAF WithDefaultAgentMiddleware 注入 agent.ChatClient 管线（镜像 ChatClientExtensions.cs L93-105），
        // 经 GetService 解析同实例（MAF 自身设 AdditionalTools 的同一解析缝）。
        var functionInvoker = chatClientAgent.ChatClient.GetService<FunctionInvokingChatClient>();
        Assert.NotNull(functionInvoker);

        // 护栏把迭代上限收成**字面量** 3：对齐老 HarnessAgentOptions.MaximumIterationsPerRequest = 3（默认 40 → 收到 3）。
        // T9：此处原有一条 `Assert.Equal(AGUIShoppingAgent.MaximumToolIterations, functionInvoker.MaximumIterationsPerRequest)`
        // —— 该属性正是 ApplyToolIterationLimit 用同一常量赋值的，属「常量自比自」的恒真断言（无独立证据），已删除；
        // 独立证据改由上方的「工具集含 recommend_products」+ 本行的字面量 3 提供。
        Assert.Equal(3, functionInvoker.MaximumIterationsPerRequest);
    }

    [Fact]
    public async Task ToolLoop_WhenModelNeverStopsCallingTool_TerminatesAfterConfiguredLimit()
    {
        var innerCallCount = 0;
        var inner = CreateNeverConvergingInner(LoopToolName, () => Interlocked.Increment(ref innerCallCount));

        // 挂一个可离线执行的桩工具（函数体返回固定文本，不触 DB），让 FICC 每次迭代都能真正执行工具后再回喂模型
        var loopTool = Meai.AIFunctionFactory.Create(
            static () => "已执行循环工具",
            new Meai.AIFunctionFactoryOptions { Name = LoopToolName });

        var agent = Assert.IsType<ChatClientAgent>(
            inner.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "LoopProbe",
                ChatOptions = new Meai.ChatOptions { Tools = [loopTool] }
            }));

        // 施加与 Create 相同的护栏（Create 挂的是真实购物工具、函数体触 DB，不适合离线驱动永不收敛循环，
        // 故行为面直测 Create 复用的同一 ApplyToolIterationLimit）
        AGUIShoppingAgent.ApplyToolIterationLimit(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await agent.RunAsync(
            "请一直调用工具，不要停",
            session: null,
            options: null,
            cts.Token);

        // 护栏生效（实测口径）：FICC 的 MaximumIterationsPerRequest 计「工具回喂轮次」，不含最初的模型请求——
        // 上限 3 → 内层模型总被调 = 初始 1 + 工具回喂 3 = 4 次，随后终止，不再无限/按 MEAI 默认 40 循环。
        Assert.NotNull(response);
        Assert.Equal(AGUIShoppingAgent.MaximumToolIterations + 1, innerCallCount);
        Assert.True(innerCallCount <= AGUIShoppingAgent.MaximumToolIterations + 1);
    }

    /// <summary>
    /// T9 行为面：让内层模型**永不收敛地反复请求真实 recommend_products**（经 Create 装配的真实 9 工具集），
    /// 断言工具迭代循环在上限内终止 —— 补上「挂上推荐工具后护栏是否仍生效」这一从未验证过的交互。
    ///
    /// <para>与 <see cref="ToolLoop_WhenModelNeverStopsCallingTool_TerminatesAfterConfiguredLimit"/> 的差别：
    /// 那条自建桩工具（真实购物工具的函数体触 DB，无法离线驱动反复循环）；本条走**真实 Create 装配**，
    /// currentUser 为真实 <see cref="CurrentUserAccessor"/> 且未设用户（<c>CurrentUser</c> 为 null）→
    /// recommend_products 在身份缺失分支离线短路（返回说明性 payload、不触 DB），故可用真实工具驱动循环。</para>
    /// </summary>
    [Fact]
    public async Task ToolLoop_WhenModelRepeatedlyCallsRecommendProducts_TerminatesAfterConfiguredLimit()
    {
        var innerCallCount = 0;
        var inner = CreateNeverConvergingInner(RecommendationToolName, () => Interlocked.Increment(ref innerCallCount));

        // 真实装配（真实购物工具 + 真实 RecommendationToolProvider 挂载的 recommend_products），
        // 内层模型只请求 recommend_products（购物工具函数体因此永不执行、不触 DB）。
        var agent = Assert.IsType<ChatClientAgent>(AGUIShoppingAgent.Create(
            inner,
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            recommendationTools: CreateRecommendationTools()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await agent.RunAsync(
            "请一直调用推荐工具，不要停",
            session: null,
            options: null,
            cts.Token);

        // 口径同既有行为用例：上限 3 → 内层模型总被调 = 初始 1 + 工具回喂 3 = 4 次后终止。
        // 用**字面量 4**（独立于常量赋值路径）——若护栏失效按 MEAI 默认 40 循环，此断言必红。
        Assert.NotNull(response);
        Assert.Equal(4, innerCallCount);
    }
}
