# DI / Hosting Registration

Register agents via `Microsoft.Agents.AI.Hosting` package.

## Namespace

```csharp
using Microsoft.Agents.AI.Hosting;
```

## AddAIAgent — Registration

```csharp
// Basic: name + instructions
builder.AddAIAgent("Assistant", "You are a helpful assistant");

// With custom IChatClient
builder.AddAIAgent("Agent", "Instructions", myChatClient);

// With chat client service key
builder.AddAIAgent("Agent", "Instructions", "my-chat-client-key");

// With description + service key
builder.AddAIAgent("Agent", "Instructions", "Description", chatClientServiceKey: null);

// Custom factory
builder.AddAIAgent("Agent", (sp, name) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    return new ChatClientAgent(chatClient, "Instructions", name);
});
```

## Configure agent (IHostedAgentBuilder)

```csharp
builder.AddAIAgent("Agent", "Instructions")
    .WithInMemorySessionStore()                  // in-memory session store
    .WithSessionStore(new MySessionStore())       // custom session store
    .WithAITool(AIFunctionFactory.Create(GetWeather))  // add tool (instance)
    .WithAITool(sp => new MyTool(sp))            // add tool (factory)
    .WithAITools(tool1, tool2);                  // add multiple tools
```

## Resolve agent

```csharp
// From DI — agents are keyed services
var agent = sp.GetRequiredKeyedService<AIAgent>("Agent");
var agent = host.Services.GetRequiredKeyedService<AIAgent>("AgentName");
```

## Service lifetime

```csharp
// Default: Singleton
builder.AddAIAgent("Agent", "Instructions");                    // Singleton
builder.AddAIAgent("Agent", "Instructions", ServiceLifetime.Scoped);    // Scoped
builder.AddAIAgent("Agent", "Instructions", ServiceLifetime.Transient); // Transient
```

## Key conventions

- All agents are **keyed services** in DI: key = agent name (`string`)
- Tool lifetime must be ≥ agent lifetime (singleton agent cannot use scoped tool)
- `IHostedAgentBuilder` uses `ServiceLifetime.Singleton` by default
- Session stores persist conversation state across requests

## Full example

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddAIAgent("Assistant", "You are helpful")
    .WithInMemorySessionStore()
    .WithAITool(AIFunctionFactory.Create(GetWeather));

var host = builder.Build();
var agent = host.Services.GetRequiredKeyedService<AIAgent>("Assistant");
var session = await agent.CreateSessionAsync();
var response = await agent.RunAsync("Hello", session);
```
