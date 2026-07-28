using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
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
