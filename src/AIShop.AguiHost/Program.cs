#pragma warning disable MAAI001 // ContextWindowCompactionStrategy 为 MAF [Experimental]（上下文压缩 API）
using AIShop.AgentTelemetry;
using AIShop.AguiHost;
using AIShop.Core.Interfaces;
using AIShop.AguiHost.Model;
using AIShop.AguiHost.Recommendation;
using AIShop.Service.Agui;
using AIShop.Service.Tools;
using AIShop.ServiceDefaults;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
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

    // AG-UI wire 兼容（AGUI.Server 0.0.6 ↔ @ag-ui/client 0.0.59）：0.0.6 把可选字段**显式序列化成 null**，
    // 而前端 Zod schema 用 `.optional()`（接受「字段缺失」、**不接受 null**）→ 每轮在首个 RUN_STARTED
    // 就被拒收（现象：UI 提示「网络/服务失败」且无任何回复，与对话内容无关；ZodError 落在
    // parentRunId / input / timestamp / metadata 四个字段上）。
    // net10.0 的 SSE 结果走宿主 JsonOptions 序列化（框架注释：flows through the configured
    // ASP.NET Core JsonSerializerOptions），故在此忽略 null 字段即可对齐两侧协议。
    // 注：这是**全局**设置，同时作用于 /models、/products、/cart 等端点的响应。
    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        options.SerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

    // agui-reco-realtime S5（design §4.4 D 第 2 / 第 4 条）：注册推荐 CUSTOM 事件的流选项映射
    // （content → CustomEvent）与 RecommendationPushContent 的 AIContent JSON 多态派生类型。
    // 【两者缺一不可】漏多态注册会让整条 SSE 流在到达 mapper 之前抛 NotSupportedException 断开。
    // Options 为懒解析、装配顺序不敏感，集中放在 AddAGUIServer 之后便于阅读。
    builder.Services.AddAguiRecommendationStreamOptions();

    // T7 DevUI 开发面板 + OpenAI wire（responses/conversations）IsDevelopment 门：
    // 仅 Development 注册/映射（设计 §4.6、spec 验收 6）——生产不暴露 /devui、/v1/entities 与 OpenAI 会话端点；
    // DevUI 默认 loopback-only（DevUIOptions.AllowRemoteAccess=false，上游库默认），此处 IsDevelopment 门是第二道安全防线。
    // OpenAI wire 供 DevUI 面板发起 Responses/Conversations 会话（官方样例 AgentWebChat / DevUIAspireIntegration 同款）。
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();
        builder.Services.AddDevUI(opt =>
        {
            opt.AllowRemoteAccess = true;

        });
    }

    // 底座 DI 装配（T3 + C5）：AddInfrastructure("Data Source=agui.db") + AddRagService("Data Source=agui.rag.db")
    // + CartToolProvider/自建模型工厂与 RouterChatClient/默认 IChatClient + AgentTelemetryOptions 绑定
    // （缺省连接串见扩展内常量；C5 起不再注册老 Service ModelRouter）。
    // T16 seam（连接串可选读）：业务库/向量库连接串改读可选配置键 Agui:DbConnection / Agui:RagConnection，
    // 缺省（键不存在或为空）→ null → 扩展内常量回退——产品逻辑零变化，仅供宿主级测试注入临时库隔离。
    builder.Services.AddAguiBaseServices(
        builder.Configuration,
        dbConnection: builder.Configuration["Agui:DbConnection"],
        ragConnection: builder.Configuration["Agui:RagConnection"]);

    // agui-client-support T4 CORS（design §6）：仅当配置了有效的 Cors:AllowedOrigins 白名单时才注册命名策略
    // AguiClient；appsettings 不写 Cors 节 → 返回 false → AddCors/UseCors 均不注册，请求管线与现状逐字节一致
    // （开发用 Vite dev proxy、生产用同域反代，浏览器视角同源，默认不需要 CORS）。配置形态错误在此启动期抛出。
    var corsEnabled = builder.Services.AddAguiCors(builder.Configuration);

    // 会话历史持久化（T12）：keyed AgentSessionStore（key = "AGUIShopping"）指向独立会话库 agui.sessions.db，
    // 取代默认 ephemeral（Noop）——MapAGUIServer 按 agent.Name keyed 解析命中，流结束 SaveSessionAsync 落库、
    // 同 ThreadId 下次 GetSessionAsync 还原（重启不丢上下文）。缺省连接串见 AddAguiSessionStore/扩展内常量。
    // T16 seam：会话库连接串改读可选配置键 Agui:SessionConnection，缺省 → null → 扩展内常量回退（行为零变化）。
    // S3：传 builder.Configuration 供绑定 Agui:SessionTtlDays / SessionCleanupIntervalHours / SessionMaxRounds
    // （未提供则回退类默认 30/12/12）。
    builder.Services.AddAguiSessionStore(
        builder.Configuration,
        builder.Configuration["Agui:SessionConnection"]);

    // 聊天历史 provider（design-sql-chat-history-provider §7 迁移路径第 1 条）：按配置 Agui:ChatHistoryProvider
    // 选择——"Sql" → 注册持久化 SqlChatHistoryProvider（独立库 agui.chat.db，行级 chat_messages 表）；
    // 其他 / 未配置 → 不注册，AGUIShopping 沿用 MAF 默认 InMemoryChatHistoryProvider（既有行为零变化）。
    // 连接串可选覆盖键 Agui:ChatConnection（仿 T16 会话库 seam），缺省回退扩展内常量。
    builder.Services.AddAguiChatHistoryProvider(
        builder.Configuration,
        builder.Configuration["Agui:ChatConnection"]);

    // T7 keyed AIAgent 注册：AGUIShopping 以 keyed AIAgent（key = AgentName）注册进 DI（独立于 IsDevelopment 门，
    // AG-UI "/" 端点在所有环境都按名解析）。这是 DevUI /v1/entities 能发现该实体、且 AG-UI 端点不因 keyed 化丢失的前提
    // （实体枚举 = GetKeyedServices<AIAgent>(KeyedService.AnyKey)，见镜像 DevUI EntitiesApiExtensions）。
    builder.Services.AddAIAgent(AGUIShoppingAgent.AgentName, (sp, name) =>
        // agui-reco-realtime S5（design §4.4 D 第 1 条）：装饰器套在 Create 产物的【最外层】（= OpenTelemetryAgent 之外）——
        // 合成更新因此从不进入 MEAI/OTel 的序列化/还原路径（内层会让自定义 AIContent 被拒绝或还原时丢弃）。
        // 【硬要求】AGUIShoppingAgent.Create 的实参、签名、返回语义逐条零改动（可用 `git diff -w` 核对），故既有
        // Create 层装配断言（AGUIShoppingAgentTests / AguiCompactionTests / AguiMemoryTests / AguiToolLoopGuardTests /
        // SqlChatHistoryProviderTests）全部零改动。DelegatingAIAgent 原样转发 Name / GetService / 会话读写 →
        // MapAGUIServer 的 keyed AgentSessionStore 解析与 GetService(typeof(ChatOptions)) 工具集读面不变
        // （由 RecommendationPushMountingTests 在真实宿主上锁定）。
        new RecommendationPushAgent(
            AGUIShoppingAgent.Create(
                new ReplySanitizingChatClient(sp.GetRequiredService<RouterChatClient>()),
                sp.GetRequiredService<CartToolProvider>(),
                sp.GetRequiredService<AgentTelemetryOptions>(),
                memoryService: ResolveMemoryService(sp),
                currentUser: sp.GetRequiredService<ICurrentUserAccessor>(),
                compactionStrategy: sp.GetRequiredService<ContextWindowCompactionStrategy>(),
                chatHistoryProvider: sp.GetService<SqlChatHistoryProvider>(),
                // agui-client-support T7：推荐工具 provider 必须显式传入——本 keyed factory 是 Create 的【唯一生产装配点】，
                // 漏传则该可选参为 null、recommend_products 静默不挂载（编译通过、工具集退化为 8）。用【具名实参】避免与
                // 上方其它可选参按位置错位；由宿主级 RecommendationToolMountingTests（解析 keyed AIAgent 读工具集）锁定。
                recommendationTools: sp.GetRequiredService<RecommendationToolProvider>()),
            sp.GetRequiredService<RecommendationToolProvider>()));

     // 解析 Mem0 记忆服务（IMemoryService）。IMemoryService 单例构造会 new LocalBgeEmbeddingGenerator(modelDir)
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

    // T4 CORS 中间件（仅在配置了有效白名单时注册，design §6.3）：置于 username 注入中间件【之前】——
    // 预检 OPTIONS 在 CORS 中间件内结束（返回 204），不会被读请求体的中间件额外处理。
    // 【L12】再套一层路径闸门（UseWhen）：命名策略只作用于 spec 声明的 4 类路径
    // （GET /models、AG-UI 端点 "/"、/products、/cart*），不像此前那样全站生效 ——
    // 否则白名单 origin 连 /devui、/v1/*、/health 都会被跨源放行（契约外暴露面）。
    // 未启用 CORS 时整段短路，spec「默认不注册 / 零开销」不受影响。
    if (corsEnabled)
        app.UseWhen(
            context => AguiCors.IsCorsScopedPath(context.Request.Path),
            branch => branch.UseCors(AguiCors.PolicyName));

    // T5 username 注入中间件（置于 MapAGUIServer 之前）：AGUI forwarded metadata(username) → ICurrentUserAccessor（缺省 steve）
    app.UseAguiUsernameForwarding();

    // C5 M3 model 注入中间件（置于 username 之后、MapAGUIServer 之前）：AGUI forwardedProps.model → IActiveModelProvider
    // （无 model → 显式 SetActiveModel(null)，「缺省 = ActiveModel」由 RouterChatClient 读取侧解析）。与 username
    // 中间件各自缓冲读同一请求体、互不干扰（AG-UI body 小，二次 JSON 解析可接受，design §6.1）。
    app.UseAguiModelForwarding();

    // T7 keyed MapAGUIServer：按 agentName 从 DI 解析 keyed AIAgent（preview 提供 (string agentName, string pattern)
    // 重载 = GetRequiredKeyedService<AIAgent>(agentName)，见镜像 AGUIEndpointRouteBuilderExtensions），
    // 映射为 AG-UI SSE 端点 "/"，保持 T5 装配语义（MapAGUIServer 请求管线、username 中间件顺序）不回退。
    // 【L3 C 防护】挂 AguiStreamEndpoint：该端点是【有意】走 AG-UI 身份分支的合法写端点（POST /），
    // 与漏挂 AguiClientRestEndpoint 的写端点区分开，避免 username 中间件的漏挂告警对它误报。
    app.MapAGUIServer(AGUIShoppingAgent.AgentName, "/").WithMetadata(new AguiStreamEndpoint());

    // agui-client-support T2：辅助 REST 端点——根级 GET /models（模型清单，数据取自模型工厂、公开可读）。
    // 与 "/"（AG-UI SSE）、/health、/alive、/devui、/v1/* 路由互不冲突；映射顺序不影响请求管线
    // （路由注册与中间件顺序无关，中间件顺序见上方 username/model 注入与 T4 CORS）。
    app.MapSupportEndpoints();

    // agui-client-support T12：根级 GET /products（全量商品目录，公开可读——缺 ?username= 放行且不注入身份、
    // 带了非空值仍校验）。数据源单一 = IProductRepository.GetAll()（与 AI 工具 / 加购校验共用同一份缓存）；
    // 与 "/"、/models、/health、/alive、/devui、/v1/* 路由互不冲突，映射顺序不影响请求管线。
    app.MapProductEndpoints();

    // agui-client-support T13：根级 /cart 组（购物车 REST 端点，身份必填——缺 ?username= 中间件 400 短路、不回落缺省）。
    // 复用既有 ICartRepository / IProductRepository（与 AI 购物工具同一批仓储与实体，非第二套实现），
    // 数据落 AguiHost 自己的业务库 agui.db；与既有路由互不冲突，映射顺序不影响请求管线。
    app.MapCartEndpoints();

    // 健康检查端点（T10）：MapDefaultEndpoints 暴露 /health + /alive（ServiceDefaults），供 Aspire Dashboard
    // 健康探测；走独立路径与 AG-UI "/" 端点、DevUI/OpenAI 路由互不冲突。生产/开发均映射（对齐老宿主 AIShop.Api）。
    app.MapDefaultEndpoints();

    // T7 DevUI 端点映射（IsDevelopment 门）：/devui SPA 面板 + /meta + /v1/entities（实体发现）+ OpenAI wire。
    // 与 MapAGUIServer(AgentName,"/") 路由互不冲突；生产环境不映射即天然关闭（验收 6「非 Development 不暴露」）。
    if (app.Environment.IsDevelopment())
    {
        // DevUI 会话通道：OpenAI Responses/Conversations wire 供 DevUI 面板发起会话（官方样例 AgentWebChat /
        // DevUIAspireIntegration 成对出现）——与上方服务注册配套，勿注释、勿删除（注释会触发 S125，DevUI 会话不可用）。
        // 【L3 C 防护】这三个映射的端点（含 POST /v1/responses、/v1/conversations 等写方法）【有意】走 AG-UI
        // 身份分支（体里没有 forwardedProps → 回落缺省用户），故一并挂 AguiStreamEndpoint，避免被 username
        // 中间件的「漏挂标记的写端点」告警误报（它们是合法端点，不是漏挂）。
        app.MapOpenAIResponses().WithMetadata(new AguiStreamEndpoint());
        app.MapOpenAIConversations().WithMetadata(new AguiStreamEndpoint());
        app.MapDevUI().WithMetadata(new AguiStreamEndpoint());
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
