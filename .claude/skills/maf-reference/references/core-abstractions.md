# Core Abstractions

## AIAgent (abstract base class)

All agents derive from `AIAgent`. Key members:

```csharp
public abstract partial class AIAgent
{
    string Id { get; }                           // auto-generated GUID or custom
    virtual string? Name { get; }                // display name
    virtual string? Description { get; }         // purpose description
    static AgentRunContext? CurrentRunContext { get; }  // AsyncLocal, flows across awaits

    ValueTask<AgentSession> CreateSessionAsync(CancellationToken ct = default);
    Task<AgentResponse> RunAsync(string message, AgentSession? session = null, ...);
    Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, ...);
    IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(...);
}
```

**RunAsync overloads** (all delegate to `RunCoreAsync`):
1. `RunAsync()` — no input, uses existing context
2. `RunAsync(string message)` — wraps in `ChatMessage(ChatRole.User, message)`
3. `RunAsync(ChatMessage message)` — single message
4. `RunAsync(IEnumerable<ChatMessage> messages)` — core implementation, sets `CurrentRunContext`

## AIContext (context container)

```csharp
public sealed class AIContext
{
    string? Instructions;              // temporary system instructions (transient)
    IEnumerable<ChatMessage>? Messages; // messages (may become permanent in history)
    IEnumerable<AITool>? Tools;         // temporary tools (transient)
}
```

- `Instructions` and `Tools` are transient — apply only to current invocation.
- `Messages` become permanent in conversation history.

## AgentSession

```csharp
// Wraps a conversation session with state
AgentSession session = await agent.CreateSessionAsync();
session.StateBag;  // dictionary for providers to store per-session state
```

## AgentResponse

```csharp
public sealed class AgentResponse(ChatResponse chatResponse)
{
    string? Text { get; }              // aggregated text
    string? AgentId { get; set; }
    string? ContinuationToken { get; set; }
    ChatResponse ChatResponse { get; } // raw underlying response
}
```

## ChatRole and ChatMessage

From `Microsoft.Extensions.AI`:

```csharp
new ChatMessage(ChatRole.System, "instructions");
new ChatMessage(ChatRole.User, "hello");
new ChatMessage(ChatRole.Assistant, "hi there");
new ChatMessage(ChatRole.Tool, "result");  // tool result
```
