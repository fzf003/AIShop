using AIShop.AguiHost.Model;
using AIShop.Core.Interfaces;
using AIShop.Service.Agui;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T7：<c>recommend_products</c> 的**生产装配路径**回归（spec MODIFIED R13 场景 2 +
/// ADDED R9 场景 3）。
/// <para>
/// 本类与 <see cref="AGUIShoppingAgentTests"/> 分工不同、缺一不可：后者直构 <c>Create</c>，只能证明「收到 provider 时
/// 能正确拼出 9 个工具」；本类经 <see cref="WebApplicationFactory{TEntryPoint}"/> 起真实宿主、从 DI 解析 keyed
/// <c>AIAgent</c>，证明 <c>Program.cs</c> 的 keyed factory（<c>Create</c> 的唯一生产调用点）**确实把
/// <see cref="RecommendationToolProvider"/> 传了进去**——漏传该可选参时编译通过、直构测试全绿，工具却静默不挂载
/// （工具集退回 8），只有本链路能暴露该失效模式。
/// </para>
/// <para>
/// 离线驱动：模型 seam 换 <see cref="StubModelChatClientFactory"/>（C5 起 agent 聊天底层经 RouterChatClient → 工厂），
/// 本用例只解析 Agent 读工具集、不发聊天请求，故不需要真实模型密钥。挂
/// <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：启动真实宿主（MigrateAsync / 播种 / RAG 预热），
/// 须与其它宿主级测试串行，避免并行迁移同一 SQLite 文件库。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class RecommendationToolMountingTests
{
    /// <summary>推荐工具名（spec ADDED R9：AGUIShopping 挂载名为 <c>recommend_products</c> 的工具）。</summary>
    private const string RecommendationTool = "recommend_products";

    /// <summary>AGUIShopping 的期望工具总数：5 购物 + 3 通用 + <c>recommend_products</c>（spec MODIFIED R13）。</summary>
    private const int ExpectedToolCount = 9;

    [Fact]
    public void KeyedAgent_ResolvedFromRealHost_MountsRecommendationTool()
    {
        // spec MODIFIED R13 场景 2：经真实宿主的 keyed AIAgent 解析（走 Program 的 factory lambda），
        // 工具集合含 recommend_products 且总数为 9。本用例是「装配点漏传实参」的唯一防线。
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var agent = factory.Services.GetRequiredKeyedService<AIAgent>(AGUIShoppingAgent.AgentName);
        var chatOptions = agent.GetService(typeof(ChatOptions)) as ChatOptions;
        Assert.NotNull(chatOptions);

        var tools = chatOptions.Tools;
        Assert.NotNull(tools);
        var names = tools.Select(t => t.Name).ToArray();

        Assert.Contains(RecommendationTool, names);
        Assert.Equal(ExpectedToolCount, names.Length);
    }

    [Fact]
    public void CartToolProvider_CreateTools_RemainsUnchangedWithoutNewTool()
    {
        // spec ADDED R9 场景 3 + MODIFIED R13 场景 1：老链路工具集零改动——新增能力走独立 provider 追加，
        // CartToolProvider.CreateTools() 仍为原 8 个（5 购物 + 3 通用）且不含 recommend_products。
        // 往该方法内加工具会让老 ShoppingAssistantAgent（与 AGUIShopping 同源）长出计划外工具。
        var cartTools = new CartToolProvider(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ICurrentUserAccessor>());

        var names = cartTools.CreateTools().Select(t => t.Name).ToArray();

        Assert.Equal(8, names.Length);
        Assert.DoesNotContain(RecommendationTool, names);
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
