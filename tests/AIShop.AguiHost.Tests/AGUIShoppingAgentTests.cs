using AIShop.AgentTelemetry;
using AIShop.AguiHost.Agents;
using AIShop.Core.Interfaces;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T4 AGUIShoppingAgent 装配测试：从零设计的 ChatClientAgent 形态（不复用旧 HarnessAgent 外壳）、
/// 名称 AGUIShopping、自然语言购物人设（不含旧 Reply+Keywords+Preferences JSON 协议关键字）、
/// 5 购物工具（CartToolProvider.CreateTools）已挂载。
/// 离线链路：NSubstitute <see cref="IChatClient"/> + 真实 <see cref="CartToolProvider"/>（mock
/// IServiceScopeFactory/ICurrentUserAccessor，semanticSearch 缺省 null —— 工具创建不触碰 DB）。
/// </summary>
public sealed class AGUIShoppingAgentTests
{
    /// <summary>装配一个默认人设的新购物 Agent（每用例独立 NSubstitute chatClient，无共享状态）。</summary>
    private static AIAgent CreateAgent(string? instructionsOverride = null)
    {
        var chatClient = Substitute.For<IChatClient>();
        // AgentTelemetry Level.None：Instrument 裸返回原 ChatClientAgent（不包 OpenTelemetryAgent 装饰器），
        // 便于下方直接断言装配面（运行时类型/挂载 tools/AIContextProviders）。
        return AGUIShoppingAgent.Create(
            chatClient,
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            instructionsOverride: instructionsOverride);
    }

    /// <summary>构造真实 CartToolProvider：仅把 5 购物工具挂到 Agent，工具函数体不被调用，故依赖可全部 mock。</summary>
    private static CartToolProvider CreateCartTools()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        return new CartToolProvider(scopeFactory, accessor);
    }

    /// <summary>
    /// 读取 ChatClientAgent 挂载到默认 <see cref="ChatOptions"/> 的装配面（instructions + tools）。
    /// ChatClientAgent 未公开 tools 枚举，经 AIAgent.GetService(typeof(ChatOptions)) 取 agent 自身默认选项
    /// （对应验收「若 AIAgent 不暴露工具枚举，则断言 Create 返回值携带的工具清单」）。
    /// </summary>
    private static ChatOptions GetMountedChatOptions(AIAgent agent)
    {
        var chatOptions = agent.GetService(typeof(ChatOptions)) as ChatOptions;
        Assert.NotNull(chatOptions);
        return chatOptions;
    }

    [Fact]
    public void Create_ProducesChatClientAgentNamedAguiShopping_NotLegacyAgent()
    {
        var agent = CreateAgent();

        // 名称契约：AGUIShopping（AG-UI 会话/遥测标识，spec「AsAIAgent("AGUIShopping", instructions)」）
        Assert.Equal("AGUIShopping", agent.Name);

        // 运行时精确类型 = ChatClientAgent（从零 ChatClientAgent 形态）：既非旧 HarnessAgent 外壳，
        // 也非 ShoppingAssistantAgent 包装（spec「不迁移旧 HarnessAgent 外壳」「新购物 Agent 以 ChatClientAgent 形态装配」）
        Assert.IsType<ChatClientAgent>(agent);
        Assert.IsNotType<ShoppingAssistantAgent>(agent);
    }

    [Fact]
    public void Create_Instructions_AreToolDrivenNaturalLanguage_WithoutLegacyJsonProtocolKeywords()
    {
        var agent = CreateAgent();
        var instructions = GetMountedChatOptions(agent).Instructions;

        // 自然语言购物人设非空，且明确引导工具驱动流程（先 search 检索、命中后加购）
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("search_product", instructions);
        Assert.Contains("add_to_cart", instructions);

        // 不复用旧 Reply+Keywords+Preferences JSON 结构化回复协议：instructions 不得出现这三个协议字段名
        Assert.DoesNotContain("Reply", instructions);
        Assert.DoesNotContain("Keywords", instructions);
        Assert.DoesNotContain("Preferences", instructions);
    }

    [Fact]
    public void Create_AttachesAllFiveCartTools_FromCartToolProvider()
    {
        var agent = CreateAgent();
        var tools = GetMountedChatOptions(agent).Tools;

        // 5 购物工具已挂载（复用 CartToolProvider.CreateTools，与老 Agent 同源）：加购/改量/查车/移除/搜索
        Assert.NotNull(tools);
        var names = tools.Select(t => t.Name).ToArray();
        var expected = new[] { "search_product", "add_to_cart", "update_cart_quantity", "get_cart_summary", "remove_from_cart" };
        Assert.Equal(expected.Length, names.Length);
        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal),
            names.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Create_HonorsInstructionsOverride_AndKeepsToolsMounted()
    {
        var overrideInstructions = "自定义人设：严格执行用户指令，简体中文。";
        var agent = CreateAgent(overrideInstructions);

        // instructionsOverride 生效时覆盖默认人设（供宿主/测试按需注入）
        Assert.Equal(overrideInstructions, GetMountedChatOptions(agent).Instructions);

        // 覆盖人设不改变工具挂载（5 购物工具仍来自 CartToolProvider.CreateTools）
        var tools = GetMountedChatOptions(agent).Tools;
        Assert.NotNull(tools);
        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void Create_AttachesContextCompactionProvider_WithExplicitStateKey()
    {
#pragma warning disable MAAI001 // CompactionProvider 为 MAF [Experimental]，测试内引用类型亦触发诊断
        var agent = CreateAgent();

        // 上下文压缩装配（T8，评审补齐缺口）：现 AsAIAgent(name, instructions, tools) 无压缩，长对话上下文会无限
        // 膨胀。ChatClientAgent 公开 AIContextProviders 列表（GetService(typeof(ChatClientAgentOptions)) 亦可达），
        // 应恰好含一个 CompactionProvider——此前列表为空/未挂，装配改变即被本用例锁定。
        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(chatClientAgent.AIContextProviders);
        var compactionProviders = chatClientAgent.AIContextProviders.OfType<CompactionProvider>().ToList();

        // 上下文压缩应恰挂一个 CompactionProvider（此前 AsAIAgent 位置签名无 AIContextProviders）
        Assert.Single(compactionProviders);

        // stateKey 显式 "AGUIShopping-Compaction"（非缺省策略类型名），避免多 agent 同 session 共享 StateBag 撞 key。
        Assert.Equal(new[] { "AGUIShopping-Compaction" }, compactionProviders[0].StateKeys);

        // 阈值对齐老 ShoppingAssistantAgent（128k/16k/0.5/0.8）在 Create 内以具名实参内联，CompactionProvider 不暴露
        // 内嵌 strategy 读面；此处锁定「压缩 provider 已挂 + 独立 stateKey」，阈值由装配代码评审核对。
#pragma warning restore MAAI001
    }

    [Fact]
    public void Create_WithTelemetryMetadataLevel_ReturnsOpenTelemetryAgent()
    {
        // T11（agent 遥测埋点）：非 None Level 经 AgentTelemetry.Instrument 包装（对齐老 ShoppingAssistantAgent L188）。
        // 返回 OpenTelemetryAgent（继承 AIAgent 的装饰器），Name 保持 AGUIShopping；GetService 转发内层 ChatOptions
        // （5 购物工具挂载面仍可读，证明装配产物可用）。
        var agent = AGUIShoppingAgent.Create(
            Substitute.For<IChatClient>(),
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.Metadata });

        Assert.Contains("OpenTelemetryAgent", agent.GetType().Name);
        Assert.Equal("AGUIShopping", agent.Name);

        var tools = GetMountedChatOptions(agent).Tools;
        Assert.NotNull(tools);
        Assert.Equal(5, tools.Count);
    }
}
