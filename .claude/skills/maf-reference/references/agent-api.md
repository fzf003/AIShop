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
| `AgentRunOptions` | `Microsoft.Agents.AI` | Options for agent invocation (`RunAsync`) |

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
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }
    public ChatOptions? ChatOptions { get; set; }
    public IList<IAIContextProvider>? AIContextProviders { get; set; }
}
```

## ChatHistoryProvider / InMemoryChatHistoryProvider

`ChatHistoryProvider` 是抽象基类，`InMemoryChatHistoryProvider` 是内置的内存实现。

```csharp
public abstract class ChatHistoryProvider
{
    // 生命周期方法——由框架调用，子类继承实现
    protected abstract ValueTask<ChatHistory> ProvideChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct);
    protected abstract ValueTask StoreChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct);

    // 辅助方法
    public IReadOnlyList<ChatMessage> GetMessages(AgentSession session);
    public void SetMessages(AgentSession session, IList<ChatMessage> messages);
}

public class InMemoryChatHistoryProvider : ChatHistoryProvider
{
    public InMemoryChatHistoryProvider();
    public InMemoryChatHistoryProvider(InMemoryChatHistoryProviderOptions options);
}

public class InMemoryChatHistoryProviderOptions
{
    public CompactionStrategy? ChatReducer { get; set; }
    public ChatReducerTriggerEvent ReducerTriggerEvent { get; set; }
}

public enum ChatReducerTriggerEvent
{
    BeforeMessagesRetrieval,
    AfterMessageStorage
}
```

> **注意**：`ChatHistoryProvider` 是抽象基类（非接口）。
> 自定义提供者继承 `ChatHistoryProvider` 并重写 `ProvideChatHistoryAsync` / `StoreChatHistoryAsync`。
> 所有状态存储在 `AgentSession.StateBag` 中，切勿存储在实例字段。
> 详细用法见 [chat-history.md](chat-history.md)。

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
    AgentSession? session = null,
    AgentRunOptions? options = null,
    CancellationToken cancellationToken = default);

// Batch history + final input — N API calls internally (one per message)
Task<AgentResponse> RunAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? session = null,
    AgentRunOptions? options = null,
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

## AgentRunOptions

```csharp
public class AgentRunOptions
{
    public ChatOptions? ChatOptions { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public ResponseFormat? ResponseFormat { get; set; }
}
```

## MAAI001 Warning

`EnableNonApprovalRequiredFunctionBypassing` and `EnableMessageInjection` on `ChatClientAgentOptions` are evaluation-only features. Using them triggers `MAAI001` warning and `TreatWarningsAsErrors` will break the build.

```csharp
#pragma warning disable MAAI001
// code using eval-only features
#pragma warning restore MAAI001
```
