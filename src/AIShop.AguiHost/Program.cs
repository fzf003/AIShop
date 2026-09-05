using Serilog;

// Serilog bootstrap 日志（应用配置就绪前的最小记录器，模式对齐 AIShop.Api/Program.cs）
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog 正式配置：读 appsettings.json 的 Serilog 节 + 控制台输出
    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Console());

    // AG-UI 服务端装配（空装配：仅注册 AG-UI 宿主基础设施与 JSON 序列化上下文）。
    // T1 阶段不 MapAGUIServer、不注册底座 DI（AddInfrastructure/ModelRouter/Agent 等归 T3/T5）。
    builder.Services.AddAGUIServer();

    var app = builder.Build();

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
