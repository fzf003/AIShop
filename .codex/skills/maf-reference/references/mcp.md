# MCP Client Integration

Integrate MCP server tools into agents via `Microsoft.Agents.AI.Mcp`.

## Namespace

```csharp
using Microsoft.Agents.AI.Mcp;
using ModelContextProtocol.Client;
```

## Basic usage: List MCP tools and register with agent

```csharp
// Create and connect MCP client
await using var mcpClient = await McpClientFactory.CreateAsync(
    new McpClientOptions
    {
        ClientInfo = new() { Name = "MyAgent", Version = "1.0.0" }
    },
    new StdioClientTransport(new ProcessStartInfo("dotnet", "run --project ./mcp-server"))
);

// List tools (with long-running task support)
var tools = await mcpClient.ListAgentToolsWithTaskSupportAsync();
// Or basic listing
var basicTools = await mcpClient.ListToolsAsync();

// Register with agent
var aiFunctions = tools.Cast<AIFunction>().ToList();
var agent = chatClient.AsAIAgent("I am a helpful assistant", tools: aiFunctions);
```

## Discover Agent Skills from MCP server

If the MCP server follows the Agent Skills convention (`skill://index.json`):

```csharp
var source = new AgentMcpSkillsSource(mcpClient);
var builder = new AgentSkillsProviderBuilder();
builder.AddSource(source);
```

## McpTaskOptions — Long-running task support

For tools declaring `ToolTaskSupport.Required`, the framework wraps them with task awareness:

```csharp
var tools = await mcpClient.ListAgentToolsWithTaskSupportAsync(
    new McpTaskOptions
    {
        PollingInterval = TimeSpan.FromSeconds(2),
        Timeout = TimeSpan.FromMinutes(10)
    });
```

## Complete integration example

```csharp
// 1. Create MCP client
await using var mcpClient = await McpClientFactory.CreateAsync(
    options,
    new StdioClientTransport(new ProcessStartInfo("node", "server.js"))
);

// 2. Get tools
var tools = await mcpClient.ListAgentToolsWithTaskSupportAsync();

// 3. Create agent with MCP tools
var aiTools = tools.Cast<AIFunction>().ToList();
var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "McpAgent",
        ChatOptions = new ChatOptions { Tools = aiTools }
    }
);

// 4. Use
var response = await agent.RunAsync("Please execute the MCP tool");
```

## Key conventions

- MCP tools are passed to LLM via `IChatClient`'s `ChatOptions.Tools`
- Long-running tools (`ToolTaskSupport.Required`) are auto-polled by the framework
- Requires `ModelContextProtocol` NuGet package for `McpClientFactory`
- MCP server must be running and accessible before use
