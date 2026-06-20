# MAF ChatClientAgent Usage Patterns

## Pattern 1: Single-Turn Agent (1 API call)

Best for: chat endpoints, reply + data extraction in one call.

```csharp
// Create agent
var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "MyAgent",
        Description = "Description",
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions())
    },
    loggerFactory: null,
    services: null
);

var session = await agent.CreateSessionAsync(cancellationToken: ct);

// Single string — 1 LLM API call
var response = await agent.RunAsync(
    "context and instructions",
    session,
    options: null,
    cancellationToken: ct);

var text = response.Text ?? "";
```

## Pattern 2: Batch-Feed History Then Query (1 + N API calls)

Best for: preference analysis that needs full conversation context.

```csharp
var agent = new ChatClientAgent(chatClient, options, null, null);
var session = await agent.CreateSessionAsync(cancellationToken: ct);

// Feed history — N API calls (one per message)
var historyMessages = history.Select(m => new AiChatMessage(
    m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
    m.Content)).ToList();

await agent.RunAsync(historyMessages, session, options: null, cancellationToken: ct);

// Then query — 1 more API call
var response = await agent.RunAsync(
    "analyze preferences",
    session,
    options: null,
    cancellationToken: ct);
```

## Pattern 3: Context String (1 API call, preferred)

Pass full history as a single string to avoid N+1 API calls.

```csharp
// Build context
var contextParts = new List<string>();
foreach (var m in history)
    contextParts.Add($"({m.Role}) {m.Content}");
contextParts.Add("instructions...");
var context = string.Join("\n", contextParts);

// 1 API call
var response = await agent.RunAsync(context, session, options: null, ct);
```

## DI Registration

```csharp
// An agent wrapper class
public sealed class MyAgent(IChatClient chatClient)
{
    public async Task<string> RunAsync(string context, CancellationToken ct = default)
    {
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { ... }, null, null);
        var session = await agent.CreateSessionAsync(cancellationToken: ct);
        var response = await agent.RunAsync(context, session, null, ct);
        return response.Text ?? "";
    }
}

// Registration
builder.Services.AddSingleton<IChatClient>(_ => { /* create */ });
builder.Services.AddScoped<MyAgent>();

// Injection in endpoint
app.MapPost("/chat", async (MyAgent agent, ...) => { ... });
```
