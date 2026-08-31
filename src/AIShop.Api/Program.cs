using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Data;
using AIShop.Api.Features.Cart;
using AIShop.Api.Features.Chat;
using AIShop.Api.Middleware;
using AIShop.Service;
using AIShop.Service.Providers;
using AIShop.Service.Tools;
using Microsoft.EntityFrameworkCore;
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Serilog;
using Scalar.AspNetCore;
using AIShop.Api.Features.Mcp;
using AIShop.ServiceDefaults;
using AIShop.AgentTelemetry;
using Microsoft.Extensions.Options;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// Load .env file into environment variables
var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
    DotNetEnv.Env.Load(envPath);

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Console());

    builder.AddServiceDefaults();

    builder.Services.AddOpenApi();
    builder.Services.AddSwaggerGen();
    builder.Services.AddInfrastructure();

    // RAG 底座注册（design §5.6，Task 11）：向量存储/embedding/检索/索引 + 启动预构建。
    // 放在 AddInfrastructure 之后（IRagSearchService 供 CartToolProvider.search_product 混合检索
    // 与 ShoppingAssistantAgent 的 search_knowledge 工具使用），向量库独立 aishop.rag.db（R6）
    builder.Services.AddRag();

    // Register Agent definitions (AIShop.Service/)
    builder.Services.AddSingleton<CartToolProvider>();
    builder.Services.AddSingleton<ModelRouter>();

    // Agent 遥测：绑定 "AgentTelemetry" 配置节，注册 AgentTelemetryOptions 单例
    // （默认 "Level": "Metadata" 生产安全；排查时改 MetadataAndContent 即可见请求/回复内容，无需重编译）
    var agentTelemetrySection = builder.Configuration.GetSection("AgentTelemetry");
    builder.Services.Configure<AgentTelemetryOptions>(agentTelemetrySection);
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value);

    // Add global exception handler
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // Register MCP Product Client — uses Aspire service discovery (http://mcp resolved via WithReference)
    builder.Services.AddHttpClient<McpProductClient>(client =>
    {
#pragma warning disable S1075 // Aspire service name, not a hardcoded URI
#pragma warning disable S5332 // Aspire service discovery URI, not user-facing
        client.BaseAddress = new Uri("http://mcp");
#pragma warning restore S5332
#pragma warning restore S1075
    });

    var app = builder.Build();

    app.MapDefaultEndpoints();

    // Global exception handler middleware
    app.UseExceptionHandler();

    // Auto-migrate SQLite on startup（P8：EnsureCreated + 手写 DDL → EF Migrations）
    // EF.IsDesignTime 跳过：避免 dotnet ef 设计时执行启动 DB 逻辑（否则 HostAbortedException）
    if (!EF.IsDesignTime)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        // Seed users (match login buttons in the UI)
        async Task SeedUser(string username, string displayName)
        {
            if (!await db.Users.AnyAsync(u => u.Username == username))
            {
                db.Users.Add(new User { Username = username, DisplayName = displayName });
            }
        }
        await SeedUser("marla", "Marla");
        await SeedUser("steve", "Steve");
        await SeedUser("fzf003", "fzf003");
        await db.SaveChangesAsync();

        // 幂等播种：空表才写入 18 个种子商品（MigrateAsync 已建表），
        // 覆盖全新库与既有库两路径，重复启动不产生重复行。
        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(ProductSeedData.Products);
            await db.SaveChangesAsync();
        }

        // 预热商品语义搜索：启动时加载 bge 模型 + 构建向量索引（复用上方 scope），
        // 避免首次检索时加载 94MB 模型 / 建索引卡住请求；失败仅 Warning，首次检索懒构建兜底
        try
        {
            await scope.ServiceProvider.GetRequiredService<IProductSemanticSearch>().EnsureIndexedAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RAG 索引预热失败，首次检索将懒构建兜底");
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapScalarApiReference();
    }

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapChatEndpoints();
    app.MapCartEndpoints();

    app.MapGet("/", () => Results.Ok(new { Status = "AIShop API is running" }));

    // Auto-open browser in development（已注释，避免每次启动弹新标签 --fzf-0）

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
