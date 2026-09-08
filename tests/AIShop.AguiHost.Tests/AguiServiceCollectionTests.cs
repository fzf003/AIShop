using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Core.Interfaces;
using AIShop.Service.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T3 + C5 DI 装配测试：AddAguiBaseServices 应注册完整底座图（EF 独立库 + RAG 独立库 + 购物工具/模型 seam/全局
/// chatClient/用户访问器）。对应 spec「复用 EF 底座连独立 SQLite 业务库」「AddRagService 语义检索使用独立向量库」
/// 装配清单与 design §4.2/§7.1 注册表（CartToolProvider/IModelChatClientFactory/IActiveModelProvider/RouterChatClient/
/// ICurrentUserAccessor/默认 IChatClient 均可从容器解析；老 Service ModelRouter 不再注册）。
/// </summary>
public sealed class AguiServiceCollectionTests
{
    private static ServiceProvider BuildProvider(IConfiguration config, string dbConnection, string ragConnection)
    {
        var services = new ServiceCollection();
        // IConfiguration 需作为服务注册（宿主由 WebApplicationBuilder 自动注册；裸 ServiceCollection 需手动），
        // 供 AguiModelClientFactory 构造函数解析（Models 节读取发生在解析时，本测试不触发网络调用）
        services.AddSingleton(config);
        services.AddAguiBaseServices(config, dbConnection, ragConnection);
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildMinimalConfig()
    {
        // 最小 Models 节：AguiModelClientFactory 构造要求存在且至少一个模型（ActiveModel=qwen）；懒建底层客户端不联网
        // （OpenAIClient 构造不发起请求）；AgentTelemetry:Level 供遥测选项绑定
        var data = new Dictionary<string, string?>
        {
            ["Models:qwen:Endpoint"] = "https://example.com/v1",
            ["Models:qwen:Model"] = "qwen3-test",
            ["Models:qwen:Name"] = "Qwen",
            ["Models:qwen:Key"] = "test-key",
            ["Models:deepseek:Endpoint"] = "https://api.deepseek.com/v1",
            ["Models:deepseek:Model"] = "deepseek-test",
            ["Models:deepseek:Name"] = "DeepSeek",
            ["Models:deepseek:Key"] = "test-key",
            ["ActiveModel"] = "qwen",
            ["AgentTelemetry:Level"] = "Metadata",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void AddAguiBaseServices_RegistersFullBaseGraph_AllResolvable()
    {
        using var sp = BuildProvider(BuildMinimalConfig(), "Data Source=agui.db", "Data Source=agui.rag.db");

        // C5 M4：模型 seam = AguiHost 自建工厂/Router 上下文（不再注册老 Service ModelRouter）。工厂读 Models 节，
        // 缺省模型 = ActiveModel（qwen）；RouterChatClient + IActiveModelProvider 均已注册（M4 收口装配面可解析）
        var modelFactory = sp.GetRequiredService<IModelChatClientFactory>();
        Assert.NotNull(modelFactory);
        Assert.Equal("qwen", modelFactory.DefaultModelId);
        Assert.NotNull(sp.GetRequiredService<IActiveModelProvider>());
        Assert.NotNull(sp.GetRequiredService<RouterChatClient>());

        // 购物工具工厂：解析成功即证明其依赖（IServiceScopeFactory + ICurrentUserAccessor + 语义检索）链可解析
        Assert.NotNull(sp.GetRequiredService<CartToolProvider>());

        // 当前用户访问器（ExecuteAsync 流注入 username 的载体）
        Assert.NotNull(sp.GetRequiredService<ICurrentUserAccessor>());

        // RAG 语义检索服务已注册（AddRagService 生效；连接串指向独立向量库在 InitializeAsync 预热时落盘）
        Assert.NotNull(sp.GetRequiredService<IProductSemanticSearch>());

        // 全局默认 chatClient = 工厂 GetDefaultClient()（AguiModelClientFactory 懒建 ActiveModel 底层，不触发网络），
        // 外层 OTel 已内置于工厂每客户端（UseOpenTelemetry(sourceName) → OpenTelemetryChatClient）。
        // C3 语义延续（spec Req8 + 验收标准 5）：全局模型 seam【不含】ReplySanitizingChatClient——记忆服务
        // （IMemoryService 内部经 GetRequiredService<IChatClient>() 取本单例做提取/冲突消解/精排）不能用「面向用户的
        // 商品编号清洗」剥落记忆文本；清洗已隔离到 AGUIShopping 专属 chatClient（Program keyed factory 外包
        // ReplySanitizingChatClient(RouterChatClient)，见 AguiDevUITests KeyedAIAgent_..._CarriesReplySanitizingChatClient 断言）。
        var chatClient = sp.GetRequiredService<IChatClient>();
        Assert.Contains("OpenTelemetryChatClient", chatClient.GetType().Name);
        // 全局纯净：沿 DelegatingChatClient 链 GetService 解析不到清洗中间件（模型 seam 不被展示规则污染）
        Assert.Null(chatClient.GetService(typeof(ReplySanitizingChatClient)));
    }

    [Fact]
    public void AddAguiBaseServices_Defaults_AreAguiDatabases()
    {
        // 缺省连接串回退点：agui.db / agui.rag.db（与老 aishop.db / aishop.rag.db 隔离，spec 验收 5）
        Assert.Equal("Data Source=agui.db", AguiServiceCollectionExtensions.DefaultDbConnection);
        Assert.Equal("Data Source=agui.rag.db", AguiServiceCollectionExtensions.DefaultRagConnection);
    }
}
