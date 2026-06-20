# MAF Reference: Agent API

## Key Types

| Type | Namespace | Description |
|------|-----------|-------------|
| `ChatClientAgent` | `Microsoft.Agents.AI` | Main agent class wrapping an `IChatClient` |
| `ChatClientAgentOptions` | `Microsoft.Agents.AI` | Options for `ChatClientAgent` |
| `InMemoryChatHistoryProvider` | `Microsoft.Agents.AI` | In-memory chat history storage |
| `InMemoryChatHistoryProviderOptions` | `Microsoft.Agents.AI` | Options for in-memory history provider |
| `AgentResponse` | `Microsoft.Agents.AI` | Response from agent `RunAsync` |
| `AgentSession` | `Microsoft.Agents.AI` | Agent session for maintaining conversation state |

## ChatClientAgent

```csharp
public ChatClientAgent(
    IChatClient chatClient,
    ChatClientAgentOptions options,
    ILoggerFactory? loggerFactory = null,
    IServiceProvider? services = null)
```

## ChatClientAgentOptions

```csharp
public class ChatClientAgentOptions
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public IChatHistoryProvider? ChatHistoryProvider { get; set; }
    public ChatOptions? ChatOptions { get; set; }
    public IList<IAIContextProvider>? AIContextProviders { get; set; }
}
```

## IChatHistoryProvider / InMemoryChatHistoryProvider

```csharp
public interface IChatHistoryProvider
{
    Task<IList<ChatMessage>> GetChatHistoryAsync(string sessionId, CancellationToken ct = default);
    Task OnChatHistoryAsync(string sessionId, IList<ChatMessage> history, CancellationToken ct = default);
}

public class InMemoryChatHistoryProvider : IChatHistoryProvider
{
    public InMemoryChatHistoryProvider(InMemoryChatHistoryProviderOptions options);
}

public class InMemoryChatHistoryProviderOptions
{
    // In-memory store — no persistence config needed
}
```

## AgentSession

```csharp
public class AgentSession
{
    public string Id { get; }
    // ...
}
```

## RunAsync Overloads

```csharp
// String input — single turn, 1 API call
Task<AgentResponse> RunAsync(
    string input,
    AgentSession session,
    AgentRunOptions? options,
    CancellationToken cancellationToken = default);

// Batch history + final input — N API calls internally (one per message)
Task<AgentResponse> RunAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession session,
    AgentRunOptions? options,
    CancellationToken cancellationToken = default);
```

**Important**: `RunAsync(IEnumerable<ChatMessage>)` internally makes **one API call per message**. For bulk context, pass a single string with all history concatenated instead.

## AgentResponse

```csharp
public class AgentResponse
{
    public IList<ChatMessage> Messages { get; }
    public string? Text { get; }
    public string? AgentId { get; }
    public string? ResponseId { get; }
    public string? ContinuationToken { get; }
    public DateTime CreatedAt { get; }
    public string? FinishReason { get; }
    public UsageData? Usage { get; }
}
```

## MAAI001 Warning

`EnableNonApprovalRequiredFunctionBypassing` and `EnableMessageInjection` on `ChatClientAgentOptions` are evaluation-only features. Using them triggers `MAAI001` warning and `TreatWarningsAsErrors` will break the build.

```csharp
#pragma warning disable MAAI001
// code using eval-only features
#pragma warning restore MAAI001
```
