using Projects;

var builder = DistributedApplication.CreateBuilder(args);
 
builder.AddProject<AIShop_Api>("api");
 

await builder.Build().RunAsync();
