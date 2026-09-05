using AIShop.AguiHost;
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

    // 底座 DI 装配（T3）：AddInfrastructure("Data Source=agui.db") + AddRagService("Data Source=agui.rag.db")
    // + CartToolProvider/ModelRouter/默认 IChatClient + AgentTelemetryOptions 绑定（缺省连接串见扩展内常量）
    builder.Services.AddAguiBaseServices(builder.Configuration);

    var app = builder.Build();

    // 启动引导（T3）：MigrateAsync + 幂等播种 marla/steve/fzf003 + 18 商品 + RAG 索引预热（失败仅 Warning）
    await AguiServiceCollectionExtensions.InitializeAsync(app.Services);

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
