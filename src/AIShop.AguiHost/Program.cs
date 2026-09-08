using AIShop.AgentTelemetry;
using AIShop.AguiHost;
using AIShop.AguiHost.Agents;
using AIShop.AguiHost.Model;
using AIShop.Core.Interfaces;
using AIShop.Service.Tools;
using AIShop.ServiceDefaults;
using Mem0Sharp;
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

    // 可观测性（T10）：对齐老宿主 AIShop.Api，在 AddSerilog 后接入 ServiceDefaults——
    // OTLP trace/metric/log（Aspire Dashboard 上报）+ 标准 HttpClient 弹性/服务发现 + 健康检查注册。
    // OTEL_EXPORTER_OTLP_ENDPOINT 由 Aspire 注入，未设置时自动跳过 OTLP exporter（本地零开销）。
    builder.AddServiceDefaults();

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

    // 底座 DI 装配（T3 + C5）：AddInfrastructure("Data Source=agui.db") + AddRagService("Data Source=agui.rag.db")
    // + CartToolProvider/自建模型工厂与 RouterChatClient/默认 IChatClient + AgentTelemetryOptions 绑定
    // （缺省连接串见扩展内常量；C5 起不再注册老 Service ModelRouter）
    builder.Services.AddAguiBaseServices(builder.Configuration);

    // 会话历史持久化（T12）：keyed AgentSessionStore（key = "AGUIShopping"）指向独立会话库 agui.sessions.db，
    // 取代默认 ephemeral（Noop）——MapAGUIServer 按 agent.Name keyed 解析命中，流结束 SaveSessionAsync 落库、
    // 同 ThreadId 下次 GetSessionAsync 还原（重启不丢上下文）。缺省连接串见 AddAguiSessionStore/扩展内常量。
    builder.Services.AddAguiSessionStore();

    // T7 keyed AIAgent 注册：AGUIShopping 以 keyed AIAgent（key = AgentName）注册进 DI（独立于 IsDevelopment 门，
    // AG-UI "/" 端点在所有环境都按名解析）。这是 DevUI /v1/entities 能发现该实体、且 AG-UI 端点不因 keyed 化丢失的前提
    // （实体枚举 = GetKeyedServices<AIAgent>(KeyedService.AnyKey)，见镜像 DevUI EntitiesApiExtensions）。
    builder.Services.AddKeyedSingleton<AIAgent>(AGUIShoppingAgent.AgentName, (sp, _) =>
        AGUIShoppingAgent.Create(
            // C3（回复清洗隔离到 agent 专属链）+ C5 M4（agui-model-switch）：AGUIShopping 专属 chatClient =
            // new ReplySanitizingChatClient(RouterChatClient)——回复清洗（C3）仍在 agent 专属链外层，覆盖 Router
            // 切到的任一模型输出（含流式增量：离开本 Agent 前经 Core ReplySanitizer 清洗商品编号；清洗只作用于
            // assistant 输出的 TextContent，工具调用/工具结果 FRC 原样透传——模型内部仍见商品编号用于 add_to_cart）；
            // RouterChatClient（C5）在清洗之下，按本轮 forwardedProps.model（无 model → ActiveModel 缺省）把对话
            // 委托到 IModelChatClientFactory 对应模型底层（spec Req7：单一 keyed agent 不变，仅底层换 Router）。
            // 记忆链路（IMemoryService）走全局纯净 seam（工厂 GetDefaultClient，见 AddAguiBaseServices），不被展示规则污染。
            new ReplySanitizingChatClient(sp.GetRequiredService<RouterChatClient>()),
            sp.GetRequiredService<CartToolProvider>(),
            // T11（agent 遥测埋点）：把底座绑定（AgentTelemetry 配置节）的遥测选项传给 Create，
            // 让 AGUIShopping 返回前经 AgentTelemetry.Instrument 包装（对齐老 ShoppingAssistantAgent L188）
            sp.GetRequiredService<AgentTelemetryOptions>(),
            // T13（Mem0 跨会话记忆）：记忆服务（可空，解析失败降级不挂记忆）+ 当前用户访问器。
            // 当前用户访问器非空解析：AGUI username 中间件（UseAguiUsernameForwarding）在 run 前 SetCurrentUser，
            // MemoryContextProvider 依赖它按用户读写记忆（缺省用户 = seed fzf003，见 AguiUsernameForwarder）。
            memoryService: ResolveMemoryService(sp),
            currentUser: sp.GetRequiredService<ICurrentUserAccessor>()));

    // T13：解析 Mem0 记忆服务（IMemoryService）。IMemoryService 单例构造会 new LocalBgeEmbeddingGenerator(modelDir)
    // ——构造即 new InferenceSession(model.onnx) 加载本地 bge ONNX 模型（~94MB，Models/bge-small-zh-v1.5）；
    // 输出目录缺模型或加载异常时解析会抛异常，此处 try/catch 降级为不挂记忆 provider（返回 null → Create 仅挂压缩），
    // 其余购物功能不受影响（对齐 RAG 预热降级语义），避免 AguiHost 启动/首次装配即崩。
    IMemoryService? ResolveMemoryService(IServiceProvider sp)
    {
        try
        {
            return sp.GetService<IMemoryService>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "记忆服务解析失败（本地 bge 模型缺失/加载异常），AGUIShopping 降级为不挂记忆 provider");
            return null;
        }
    }

    var app = builder.Build();

    // 启动引导（T3）：MigrateAsync + 幂等播种 marla/steve/fzf003 + 18 商品 + RAG 索引预热（失败仅 Warning）
    await AguiServiceCollectionExtensions.InitializeAsync(app.Services);

    // T5 username 注入中间件（置于 MapAGUIServer 之前）：AGUI forwarded metadata(username) → ICurrentUserAccessor（缺省 guest）
    app.UseAguiUsernameForwarding();

    // C5 M3 model 注入中间件（置于 username 之后、MapAGUIServer 之前）：AGUI forwardedProps.model → IActiveModelProvider
    // （无 model → 显式 SetActiveModel(null)，「缺省 = ActiveModel」由 RouterChatClient 读取侧解析）。与 username
    // 中间件各自缓冲读同一请求体、互不干扰（AG-UI body 小，二次 JSON 解析可接受，design §6.1）。
    app.UseAguiModelForwarding();

    // T7 keyed MapAGUIServer：按 agentName 从 DI 解析 keyed AIAgent（preview 提供 (string agentName, string pattern)
    // 重载 = GetRequiredKeyedService<AIAgent>(agentName)，见镜像 AGUIEndpointRouteBuilderExtensions），
    // 映射为 AG-UI SSE 端点 "/"，保持 T5 装配语义（MapAGUIServer 请求管线、username 中间件顺序）不回退。
    app.MapAGUIServer(AGUIShoppingAgent.AgentName, "/");

    // 健康检查端点（T10）：MapDefaultEndpoints 暴露 /health + /alive（ServiceDefaults），供 Aspire Dashboard
    // 健康探测；走独立路径与 AG-UI "/" 端点、DevUI/OpenAI 路由互不冲突。生产/开发均映射（对齐老宿主 AIShop.Api）。
    app.MapDefaultEndpoints();

    // T7 DevUI 端点映射（IsDevelopment 门）：/devui SPA 面板 + /meta + /v1/entities（实体发现）+ OpenAI wire。
    // 与 MapAGUIServer(AgentName,"/") 路由互不冲突；生产环境不映射即天然关闭（验收 6「非 Development 不暴露」）。
    if (app.Environment.IsDevelopment())
    {
        // DevUI 会话通道：OpenAI Responses/Conversations wire 供 DevUI 面板发起会话（官方样例 AgentWebChat /
        // DevUIAspireIntegration 成对出现）——与上方服务注册配套，勿注释、勿删除（注释会触发 S125，DevUI 会话不可用）。
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
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
