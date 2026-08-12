using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Infrastructure;
using AIShop.Infrastructure.Data;
using AIShop.Api.Agents;
using AIShop.Api.Features.Cart;
using AIShop.Api.Features.Chat;
using AIShop.Api.Middleware;
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

    // Register Agent definitions (Api/Agents/)
    builder.Services.AddScoped<SqliteChatHistoryProvider>();
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

    // Auto-migrate SQLite on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

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

        // 幂等建表兜底：EnsureCreatedAsync 仅当库不存在时创建全部 schema；库已存在时直接跳过、绝不补建新表。
        // 因此任何有历史库的环境（生产/同事/曾运行过）都必须依赖下面的 CREATE TABLE IF NOT EXISTS 补建
        // Products / UserPreferences 两表，否则播种时表不存在 → SqliteException → 启动崩溃。
        // 注意：DDL 以 EF 空库 EnsureCreated 实际生成的 schema 为准照抄（表名/约束名 PascalCase：
        // "Products"/"UserPreferences"/PK_Products/PK_UserPreferences），与 EF 映射 100% 一致，
        // 不得按 tasks.md 早期的小写示例（products/user_preferences）手写，否则播种/查询会因表名不匹配失败。
        // P2 时序：EnsureCreatedAsync + 建表兜底 DDL + 播种全部在同一个 CreateScope() 块、同一个 AppDbContext 实例上执行。
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Products" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Tags" TEXT NOT NULL,
                "Price" TEXT NOT NULL,
                "Emoji" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "UserPreferences" (
                "UserId" TEXT NOT NULL CONSTRAINT "PK_UserPreferences" PRIMARY KEY,
                "KeywordsJson" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        // 幂等播种：空表才写入 18 个种子商品（全新库 EnsureCreated 已建表；既有库兜底建表后走到这里），
        // 覆盖全新库与既有库两路径，重复启动不产生重复行。
        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(ProductSeedData.Products);
            await db.SaveChangesAsync();
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
