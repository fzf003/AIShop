using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<AIShop_Api>("api").ExcludeFromMcp();
builder.AddProject<AIShop_McpServer>("mcp");
// T11：给 agui 资源接 Aspire 健康探测——T10 已让 AguiHost MapDefaultEndpoints 暴露 /health，
// WithHttpHealthCheck 使 Aspire Dashboard 轮询得到 agui 的健康状态与 HTTP trace（此前无健康状态）。
builder.AddProject<AIShop_AguiHost>("agui").WithHttpHealthCheck("/health");


await builder.Build().RunAsync();
