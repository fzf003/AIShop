using AIShop.AguiHost.Model;
using AIShop.AguiHost.Recommendation;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-reco-realtime S5：推荐推送装饰器（<see cref="RecommendationPushAgent"/>）的**生产装配路径**回归
/// （design §4.4 D 第 1 条；对应 R1-1 的装配面）。
/// <para>
/// 与 <see cref="RecommendationPushAgentTests"/>（直构装饰器，证明「转发与门控本身对」）分工不同、缺一不可：
/// 装饰器再正确，只要 <c>Program.cs</c> 的 keyed factory 忘了套它，生产形态就退化成「无推荐实时推送」而
/// <b>编译通过、其余测试全绿</b>——只有经真实宿主解析 keyed <c>AIAgent</c>（走 Program 的 factory lambda）
/// 才能暴露该失效模式。本类是该装配点的唯一防线（对照 <see cref="RecommendationToolMountingTests"/> 盯
/// 「<c>recommend_products</c> 实参有没有漏传」，两者合起来锁住 keyed factory 的整条装配）。
/// </para>
/// <para>
/// 同时证明「外套装饰器未破坏转发」：<c>Name</c> 与 <c>GetService(typeof(ChatOptions))</c> 的工具集都由
/// <c>DelegatingAIAgent</c> 原样转发，是 AG-UI 端点的 keyed <c>AgentSessionStore</c> 解析（按 <c>agent.Name</c>）
/// 与工具挂载读面的前提。
/// </para>
/// <para>
/// 离线驱动：模型 seam 换 <see cref="StubModelChatClientFactory"/>（同 AguiRequestTests），本用例只解析 Agent、
/// 不发聊天请求，故不需要真实模型密钥。挂 <c>[Collection(nameof(AguiRequestTests))]</c>：启动真实宿主
/// （MigrateAsync / 播种 / RAG 预热），须与其它宿主级测试串行，避免并行迁移同一 SQLite 文件库。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class RecommendationPushMountingTests
{
    /// <summary>
    /// AGUIShopping 的期望工具总数（5 购物 + 3 通用 + <c>recommend_products</c>，spec MODIFIED R13）。
    /// 与 <see cref="RecommendationToolMountingTests"/> 同口径：本类断言「装饰器外套后工具集照样透出」。
    /// </summary>
    private const int ExpectedToolCount = 9;

    /// <summary>推荐工具名（spec ADDED R9）。</summary>
    private const string RecommendationTool = "recommend_products";

    [Fact]
    public void KeyedAgent_ResolvedFromRealHost_HasRecommendationPushAgentAsOutermostDecorator()
    {
        // S5 清单第 1 条：Program keyed factory 唯一生产装配点把 Create 产物套上装饰器。
        // 「最外层」是硬约束——装饰器若被塞进 Create 内层（= OpenTelemetryAgent 之内），合成更新要穿越
        // MEAI 遥测客户端的序列化/还原，自定义 AIContent 有被拒绝/丢弃的风险（S1 实测，design §4.1）。
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var agent = factory.Services.GetRequiredKeyedService<AIAgent>(AGUIShoppingAgent.AgentName);

        // 最外层类型 = 装饰器本身（内层仍是 Create 产物 OpenTelemetryAgent）。
        Assert.IsType<RecommendationPushAgent>(agent);

        // Name 经 DelegatingAIAgent 原样转发：MapAGUIServer 以 GetKeyedService<AgentSessionStore>(aiAgent.Name)
        // 解析会话 store，DevUI /v1/entities 也按该名发现实体——名字一变，会话持久化与实体发现同时断。
        Assert.Equal(AGUIShoppingAgent.AgentName, agent.Name);

        // GetService 转发内层 ChatOptions：工具集仍为 9（转发未破），且含 recommend_products。
        var chatOptions = Assert.IsType<ChatOptions>(agent.GetService(typeof(ChatOptions)));
        Assert.NotNull(chatOptions.Tools);

        var names = chatOptions.Tools.Select(tool => tool.Name).ToArray();
        Assert.Equal(ExpectedToolCount, names.Length);
        Assert.Contains(RecommendationTool, names);
    }

    /// <summary>
    /// WAF 装配：真实宿主（内容根 = <c>src/AIShop.AguiHost</c>，读真实 appsettings）+ 模型 seam 换
    /// <see cref="StubModelChatClientFactory"/>（离线，不发真实 LLM）。不替换其余 seam——本用例的断言对象正是
    /// 「生产装配路径」，替换越多越自证。测试库沿用宿主缺省（与同类宿主级测试同集合串行，无并发迁移冲突）。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelChatClientFactory>();
                services.AddSingleton<IModelChatClientFactory>(
                    new StubModelChatClientFactory(Substitute.For<IChatClient>()));
            }));
}
