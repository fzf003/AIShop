using System.Net;
using System.Text.Json;
using AIShop.AguiHost.Model;
using AIShop.Service;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本集合多次启动 AguiHost Program（WAF 真实宿主，Development/Production 各环境），置 DisableParallelization
/// 串行集合：避免多个宿主并行迁移同一 SQLite 文件库（learnings 先例同 AguiRequestTests / ProgramSeedingTests）。
/// </summary>
[CollectionDefinition(nameof(AguiDevUITests), DisableParallelization = true)]
public sealed class AguiDevUITestsCollection;

/// <summary>
/// T7 DevUI 开发面板冒烟测试：IsDevelopment 门（设计 §4.6 / spec 验收标准 6）。
/// 覆盖：
///  1) Development 下 /devui 面板可达（非 404）；
///  2) Development 下 /v1/entities 可发现 keyed AIAgent "AGUIShopping"（实体枚举 = GetKeyedServices&lt;AIAgent&gt;(AnyKey)，镜像 DevUI EntitiesApiExtensions）；
///  3) 非 Development（Production）不暴露 /devui 与 /v1/entities（404）；
///  4) keyed AIAgent 注册独立于 IsDevelopment 门可解析（AG-UI "/" 端点不因 keyed 化丢失的前提，MapAGUIServer 按名解析）。
/// 离线驱动：模型 seam 替换为 <see cref="IModelChatClientFactory"/> stub（C5 M4 起离线 override 点从全局 IChatClient
/// 迁到工厂接口，spec Req11；stub 让所有 modelId 返回脚本化文本 mock，不触发真实 LLM 会话，仅元数据/路由层），同 AguiRequestTests。
/// WAF TestServer 的 RemoteIpAddress 为 null（非 loopback）→ DevUI auth filter 默认 403，
/// 故 Configure&lt;DevUIOptions&gt; 覆写 AllowRemoteAccess=true（DevUI 自身默认 loopback 属上游库行为，非本仓代码）。
/// </summary>
[Collection(nameof(AguiDevUITests))]
public sealed class AguiDevUITests
{
    /// <summary>模拟回复里携带的 ASCII 标记（本套测试不驱动真实会话，仅保证宿主可离线启动）。</summary>
    private const string SimulatedText = "购物助手已为您检索，DEVUI-MARKER 专业跑鞋 349.00 元。";

    [Fact]
    public async Task Development_DevUiPanel_Returns200_IsReachable()
    {
        // Development 下面板可达（tasks T7 验收「Development 下面板可访问」）：GET /devui/ 直接命中
        // DevUIMiddleware 的 index.html（MapDevUI 挂载点 = "/devui/{*path}"，root 请求 301 → /devui/）；
        // GET /devui 也不得 404（挂载成立，301 跳转或 200 皆可）。
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        using (var panelRoot = await client.GetAsync("/devui/"))
        {
            Assert.Equal(HttpStatusCode.OK, panelRoot.StatusCode);
            Assert.NotNull(panelRoot.Content.Headers.ContentType);
            Assert.StartsWith("text/html", panelRoot.Content.Headers.ContentType.MediaType, StringComparison.OrdinalIgnoreCase);
            var body = await panelRoot.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body));
        }

        using (var noSlash = await client.GetAsync("/devui"))
        {
            Assert.NotEqual(HttpStatusCode.NotFound, noSlash.StatusCode);
        }
    }

    [Fact]
    public async Task Development_GetV1Entities_ContainsAguiShoppingKeyedAgent()
    {
        // DevUI 实体发现（tasks T7 验收「/v1/entities 含 AGUIShopping entity」）：keyed AIAgent "AGUIShopping"
        // 注册进 DI 后出现在 /v1/entities 列表（id/name=AGUIShopping、type=agent）。
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/entities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("entities", out var entities), "响应应含 entities 数组");
        Assert.Equal(JsonValueKind.Array, entities.ValueKind);

        var aguiEntity = entities.EnumerateArray().FirstOrDefault(e =>
            e.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && name.GetString() == AGUIShoppingAgent.AgentName);

        Assert.True(aguiEntity.ValueKind != JsonValueKind.Undefined, "/v1/entities 应含 name=AGUIShopping 的实体");

        Assert.Equal("agent", GetJsonPropertyString(aguiEntity, "type"));
        Assert.Equal(AGUIShoppingAgent.AgentName, GetJsonPropertyString(aguiEntity, "id"));

        // 实体元数据应带挂载工具（5 购物工具经 ChatOptions.Tools 进入 EntityInfo.Tools），证明发现的是同一装配后的 agent
        if (aguiEntity.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var toolNames = tools.EnumerateArray().Select(t => t.GetString()).ToArray();
            Assert.Contains("search_product", toolNames);
        }
    }

    [Fact]
    public async Task Production_DevUiAndEntities_AreNotExposed_Return404()
    {
        // IsDevelopment 门（tasks T7 验收「生产不暴露」）：Production 下 /devui 与 /v1/entities 均未映射 → 404
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        using var devUi = await client.GetAsync("/devui/");
        Assert.Equal(HttpStatusCode.NotFound, devUi.StatusCode);

        using var entities = await client.GetAsync("/v1/entities");
        Assert.Equal(HttpStatusCode.NotFound, entities.StatusCode);
    }

    [Fact]
    public void KeyedAIAgent_AguiShopping_IsResolvableFromDi_WithAgentName()
    {
        // keyed AIAgent 注册独立于 IsDevelopment 门（AG-UI "/" 端点在所有环境按名解析，不因 keyed 化丢失；
        // 这也是 /v1/entities 能发现该实体的 DI 前提）。Development 环境根容器按 key 解析得到装配完成的 agent。
        using var factory = CreateFactory("Development");

        var agent = factory.Services.GetRequiredKeyedService<AIAgent>(AGUIShoppingAgent.AgentName);

        Assert.NotNull(agent);
        Assert.Equal(AGUIShoppingAgent.AgentName, agent.Name);

        // T11（agent 遥测埋点）：Program keyed factory 把 appsettings AgentTelemetry:Level(MetadataAndContent)
        // 传入 Create → 返回前经 AgentTelemetry.Instrument 包装为 OpenTelemetryAgent（不再裸 ChatClientAgent）。
        // 仍是 AIAgent，Name/GetService(转发内层 ChatOptions) 保持——AG-UI/DevUI 会话链路不受影响。
        Assert.Contains("OpenTelemetryAgent", agent.GetType().Name);
    }

    [Fact]
    public void KeyedAIAgent_AguiShopping_UnderlyingChatClient_CarriesReplySanitizingChatClient()
    {
        // C3（回复清洗隔离到 agent 专属链）+ C5 M4（RouterChatClient 装配）：回复清洗从全局 IChatClient 隔离到
        // AGUIShopping 专属 chatClient——Program keyed factory 以 new ReplySanitizingChatClient(
        // sp.GetRequiredService<RouterChatClient>()) 作为 Create 的 chatClient 实参（C3 不回退：清洗仍在 agent 专属链
        // 最外层，只作用于 agent 输出文本；工具 FRC 不过洗，模型内部仍见商品编号用于加购）。本断言锁住装配路径：
        // keyed AIAgent 解析出的 agent（OpenTelemetryAgent，GetService 转发内层 ChatClientAgent → 其 ChatClient
        // 管线）应能沿 DelegatingChatClient 链解析回 ReplySanitizingChatClient（agent 链带清洗）。RouterChatClient
        // 位于清洗之下（每轮选模型，见 spec Req7），其入链装配证明由请求级 model 切换测试承担（GetService 沿链不自返回
        // RouterChatClient 实例——直接实现 IChatClient、GetService 转发到当轮目标，design §5.3/实施期确认项 ③）。
        // 对照 AguiServiceCollectionTests 的「全局纯净（不含清洗中间件）」断言，二者共同证明清洗只存在于 agent 专属路径。
        using var factory = CreateFactory("Development");

        var agent = factory.Services.GetRequiredKeyedService<AIAgent>(AGUIShoppingAgent.AgentName);
        Assert.NotNull(agent);
        Assert.Equal(AGUIShoppingAgent.AgentName, agent.Name);

        // agent.GetService(IChatClient) → 内层 ChatClientAgent.GetService 返回 this.ChatClient（LLM 管线）；
        // 非空即证明底层 chatClient 可达（不至于因 Instrument 包装丢失内层管线读面）。
        Assert.NotNull(agent.GetService(typeof(Meai.IChatClient)));
        // 清洗中间件在 AGUIShopping 专属 chatClient 链上（GetService 沿 Delegating 链解析命中）
        Assert.NotNull(agent.GetService(typeof(ReplySanitizingChatClient)));
    }

    /// <summary>从 JSON 对象中读取字符串属性（缺键/非字符串返回 null）。</summary>
    private static string? GetJsonPropertyString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 装配指定环境的 WAF：Program 在对应环境跑完整启动逻辑（IsDevelopment 门决定是否注册/映射 DevUI+OpenAI wire）；
    /// 替换模型 seam 为 <see cref="IModelChatClientFactory"/> stub（C5 M4 起离线 override 点从全局 IChatClient 迁到
    /// 工厂接口——agent 聊天底层经 RouterChatClient → 工厂；stub 让所有 modelId 返回脚本化文本 mock，免 Key/真实 LLM），
    /// 并覆写 DevUIOptions.AllowRemoteAccess=true（TestServer RemoteIpAddress 为 null → DevUI auth filter 403；
    /// 仅测试宿主需要，DevUI 默认 loopback 属上游库行为）。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(string environment)
    {
        var mockChat = CreateMockChatClient();
        var stubFactory = new StubModelChatClientFactory(mockChat);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelChatClientFactory>();
                services.AddSingleton<IModelChatClientFactory>(stubFactory);

                services.Configure<DevUIOptions>(options => options.AllowRemoteAccess = true);
            });
        });
    }

    /// <summary>脚本化文本回复的 chatClient：GetResponseAsync / GetStreamingResponseAsync 均返回固定中文文本。</summary>
    private static Meai.IChatClient CreateMockChatClient()
    {
        var mockChat = Substitute.For<Meai.IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, SimulatedText)));
        mockChat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync());
        return mockChat;
    }

    /// <summary>单条文本增量更新流（ChatClientAgent 走 GetStreamingResponseAsync，yield 一次即结束流）。</summary>
    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextAsync()
    {
        yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, SimulatedText);
    }
}
