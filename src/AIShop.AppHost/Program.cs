using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<AIShop_Api>("api");
builder.AddProject<AIShop_McpServer>("mcp");

await builder.Build().RunAsync();
