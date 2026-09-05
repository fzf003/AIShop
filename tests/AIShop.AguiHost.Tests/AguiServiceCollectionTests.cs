using AIShop.AguiHost;
using AIShop.Core.Interfaces;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T3 DI 装配测试：AddAguiBaseServices 应注册完整底座图（EF 独立库 + RAG 独立库 + 购物工具/路由/全局 chatClient/用户访问器）。
/// 对应 spec「复用 EF 底座连独立 SQLite 业务库」「AddRagService 语义检索使用独立向量库」装配清单与
/// design §4.2 注册表（CartToolProvider/ModelRouter/ICurrentUserAccessor/默认 IChatClient 均可从容器解析）。
/// </summary>
public sealed class AguiServiceCollectionTests
{
    private static ServiceProvider BuildProvider(IConfiguration config, string dbConnection, string ragConnection)
    {
        var services = new ServiceCollection();
        // IConfiguration 需作为服务注册（宿主由 WebApplicationBuilder 自动注册；裸 ServiceCollection 需手动），
        // 供 ModelRouter 构造函数解析（Models 节读取发生在解析时，本测试不触发网络调用）
        services.AddSingleton(config);
        services.AddAguiBaseServices(config, dbConnection, ragConnection);
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildMinimalConfig()
    {
        // 最小 Models 节：ModelRouter 构造要求存在且至少一个模型（ActiveModel=qwen → 非 DeepSeek 路径，
        // GetDefaultChatClient 构建离线 chatClient 不联网）；AgentTelemetry:Level 供遥测选项绑定
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

        // 模型路由：读 Models 节，激活模型 = ActiveModel（qwen）
        var router = sp.GetRequiredService<ModelRouter>();
        Assert.NotNull(router);
        Assert.Equal("qwen", router.ActiveModel);

        // 购物工具工厂：解析成功即证明其依赖（IServiceScopeFactory + ICurrentUserAccessor + 语义检索）链可解析
        Assert.NotNull(sp.GetRequiredService<CartToolProvider>());

        // 当前用户访问器（ExecuteAsync 流注入 username 的载体）
        Assert.NotNull(sp.GetRequiredService<ICurrentUserAccessor>());

        // RAG 语义检索服务已注册（AddRagService 生效；连接串指向独立向量库在 InitializeAsync 预热时落盘）
        Assert.NotNull(sp.GetRequiredService<IProductSemanticSearch>());

        // 全局默认 chatClient = ModelRouter.GetDefaultChatClient()（非 DeepSeek 路径构建离线客户端，不触发网络）
        Assert.NotNull(sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void AddAguiBaseServices_Defaults_AreAguiDatabases()
    {
        // 缺省连接串回退点：agui.db / agui.rag.db（与老 aishop.db / aishop.rag.db 隔离，spec 验收 5）
        Assert.Equal("Data Source=agui.db", AguiServiceCollectionExtensions.DefaultDbConnection);
        Assert.Equal("Data Source=agui.rag.db", AguiServiceCollectionExtensions.DefaultRagConnection);
    }
}
