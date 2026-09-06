using AIShop.AgentTelemetry;
using AIShop.AguiHost.Agents;
using AIShop.Core.Interfaces;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
/// </summary>
public sealed class AguiToolLoopGuardTests
{
    /// <summary>循环护栏测试用的桩工具名（内层模型永不收敛地请求它，工具函数体只返回固定文本、不触 DB）。</summary>
    private const string LoopToolName = "loop_tool";

    /// <summary>构造真实 CartToolProvider（同 AGUIShoppingAgentTests）：仅把 5 购物工具挂到 Agent，函数体不被调用。</summary>
    private static CartToolProvider CreateCartTools()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        return new CartToolProvider(scopeFactory, accessor);
    }

    /// <summary>
    /// 构造「永不收敛」的内层 IChatClient：每次被调都返回请求 <see cref="LoopToolName"/> 的 assistant 工具调用
    /// （callId 每次唯一），使 FICC 若无上限会一直循环；<paramref name="callCount"/> 统计内层真实被调次数。
    /// </summary>
    private static Meai.IChatClient CreateNeverConvergingInner(Action increment)
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
                    LoopToolName,
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
        // Level.None：Instrument 裸返回 ChatClientAgent，保留 .ChatClient 读面供解析 FICC
        var agent = AGUIShoppingAgent.Create(
            Substitute.For<Meai.IChatClient>(),
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });
        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);

        // FICC 由 MAF WithDefaultAgentMiddleware 注入 agent.ChatClient 管线（镜像 ChatClientExtensions.cs L93-105），
        // 经 GetService 解析同实例（MAF 自身设 AdditionalTools 的同一解析缝）。
        var functionInvoker = chatClientAgent.ChatClient.GetService<FunctionInvokingChatClient>();
        Assert.NotNull(functionInvoker);

        // 护栏把迭代上限收成 3：对齐老 HarnessAgentOptions.MaximumIterationsPerRequest = 3（默认 40 → 收到 3）
        Assert.Equal(3, functionInvoker.MaximumIterationsPerRequest);
        Assert.Equal(AGUIShoppingAgent.MaximumToolIterations, functionInvoker.MaximumIterationsPerRequest);
    }

    [Fact]
    public async Task ToolLoop_WhenModelNeverStopsCallingTool_TerminatesAfterConfiguredLimit()
    {
        var innerCallCount = 0;
        var inner = CreateNeverConvergingInner(() => Interlocked.Increment(ref innerCallCount));

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
}
