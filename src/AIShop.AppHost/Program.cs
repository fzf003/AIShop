using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var mcp = builder.AddProject<AIShop_McpServer>("mcp")
    .WithHttpEndpoint(port: 6500).ExcludeFromMcp();

builder.AddProject<AIShop_Api>("api")
    .WithReference(mcp);

await builder.Build().RunAsync();
