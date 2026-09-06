using AIShop.AguiHost;
using AIShop.AguiHost.Agents;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Serilog;

// Serilog bootstrap 日志（应用配置就绪前的最小记录器，模式对齐 AIShop.Api/Program.cs）
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// Load .env file into environment variables（可选：模型密钥等敏感配置走 .env/环境变量，同 Api）
var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
    DotNetEnv.Env.Load(envPath);

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog 正式配置：读 appsettings.json 的 Serilog 节 + 控制台输出
    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Console());

    // AG-UI 服务端装配（注册 AG-UI 宿主基础设施与 JSON 序列化上下文；MapAGUIServer 归 T5）
    builder.Services.AddAGUIServer();

    // T7 DevUI 开发面板 + OpenAI wire（responses/conversations）IsDevelopment 门：
    // 仅 Development 注册/映射（设计 §4.6、spec 验收 6）——生产不暴露 /devui、/v1/entities 与 OpenAI 会话端点；
    // DevUI 默认 loopback-only（DevUIOptions.AllowRemoteAccess=false，上游库默认），此处 IsDevelopment 门是第二道安全防线。
    // OpenAI wire 供 DevUI 面板发起 Responses/Conversations 会话（官方样例 AgentWebChat / DevUIAspireIntegration 同款）。
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();
        builder.Services.AddDevUI();
    }

    // 底座 DI 装配（T3）：AddInfrastructure("Data Source=agui.db") + AddRagService("Data Source=agui.rag.db")
    // + CartToolProvider/ModelRouter/默认 IChatClient + AgentTelemetryOptions 绑定（缺省连接串见扩展内常量）
    builder.Services.AddAguiBaseServices(builder.Configuration);

    // T7 keyed AIAgent 注册：AGUIShopping 以 keyed AIAgent（key = AgentName）注册进 DI（独立于 IsDevelopment 门，
    // AG-UI "/" 端点在所有环境都按名解析）。这是 DevUI /v1/entities 能发现该实体、且 AG-UI 端点不因 keyed 化丢失的前提
    // （实体枚举 = GetKeyedServices<AIAgent>(KeyedService.AnyKey)，见镜像 DevUI EntitiesApiExtensions）。
    builder.Services.AddKeyedSingleton<AIAgent>(AGUIShoppingAgent.AgentName, (sp, _) =>
        AGUIShoppingAgent.Create(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<CartToolProvider>()));

    var app = builder.Build();

    // 启动引导（T3）：MigrateAsync + 幂等播种 marla/steve/fzf003 + 18 商品 + RAG 索引预热（失败仅 Warning）
    await AguiServiceCollectionExtensions.InitializeAsync(app.Services);

    // T5 username 注入中间件（置于 MapAGUIServer 之前）：AGUI forwarded metadata(username) → ICurrentUserAccessor（缺省 guest）
    app.UseAguiUsernameForwarding();

    // T7 keyed MapAGUIServer：按 agentName 从 DI 解析 keyed AIAgent（preview 提供 (string agentName, string pattern)
    // 重载 = GetRequiredKeyedService<AIAgent>(agentName)，见镜像 AGUIEndpointRouteBuilderExtensions），
    // 映射为 AG-UI SSE 端点 "/"，保持 T5 装配语义（MapAGUIServer 请求管线、username 中间件顺序）不回退。
    app.MapAGUIServer(AGUIShoppingAgent.AgentName, "/");

    // T7 DevUI 端点映射（IsDevelopment 门）：/devui SPA 面板 + /meta + /v1/entities（实体发现）+ OpenAI wire。
    // 与 MapAGUIServer(AgentName,"/") 路由互不冲突；生产环境不映射即天然关闭（验收 6「非 Development 不暴露」）。
    if (app.Environment.IsDevelopment())
    {
        app.MapDevUI();
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
