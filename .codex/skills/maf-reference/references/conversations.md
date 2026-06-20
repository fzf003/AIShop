# Conversational Agent

Manage multi-turn conversations between agents and users, including session creation, history management, and context maintenance.

## Namespace

```csharp
using Microsoft.Agents.AI;
```

## Core Concepts

### Session

`AgentSession` is the container for a conversation. Each `RunAsync` call on the same session accumulates history.

```csharp
// Create a new session
var session = await agent.CreateSessionAsync(cancellationToken: ct);

// Multiple calls on the same session accumulate history
var response1 = await agent.RunAsync("Hello", session, cancellationToken: ct);
var response2 = await agent.RunAsync("Recommend running shoes", session, cancellationToken: ct);
// Session now contains two turns of conversation
```

### StateBag

`AgentSession.StateBag` stores session-level state, including conversation history, context provider state, etc.

```csharp
var state = session.StateBag;

// Read/write custom state
state["myKey"] = myValue;
var value = state["myKey"];
```

## Conversation History Management

### InMemoryChatHistoryProvider

Suitable for stateless services or development:

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions());

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ShoppingAssistant",
    ChatHistoryProvider = provider
});

var session = await agent.CreateSessionAsync();
// History managed automatically
```

### Custom ChatHistoryProvider

Suitable for persisting history (database, Redis, etc.):

```csharp
public sealed class DbChatHistoryProvider : ChatHistoryProvider
{
    protected override async ValueTask<ChatHistory> ProvideChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // Load history from database (prepended before user messages)
        return new ChatHistory(await LoadFromDbAsync(context.Session, ct));
    }

    protected override async ValueTask StoreChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // Persist new messages to database
        await SaveToDbAsync(context.Session, context.Messages, ct);
    }
}
```

> **Note**: All state must be stored in `AgentSession.StateBag`, **never in instance fields**.

## Context Window Compaction

Long conversations consume significant tokens. Use compaction strategies:

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = new SummarizationCompactionStrategy(chatClient),
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
});
```

See [compaction.md](compaction.md) for details.

## AIContextProvider

Used to inject dynamic context on each LLM call:

```csharp
public sealed class PreferenceContextProvider : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        return new AIContext
        {
            Instructions = "Recommend products based on user preferences",
            Messages = [new ChatMessage(ChatRole.System, "User preferences: sports, outdoor")]
        };
    }

    protected override async ValueTask StoreAIContextAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // Store state in StateBag, not instance fields
        var state = context.Session?.StateBag;
    }
}
```

See [context-provider.md](context-provider.md) for details.

## Complete Multi-turn Example

```csharp
public sealed class ChatService
{
    private readonly ChatClientAgent _agent;

    public ChatService(IChatClient chatClient)
    {
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "Smart shopping assistant",
            ChatHistoryProvider = new InMemoryChatHistoryProvider()
        });
    }

    public async Task<string> ChatAsync(string message, AgentSession session, CancellationToken ct)
    {
        var response = await _agent.RunAsync(message, session, cancellationToken: ct);
        return response.Text ?? "";
    }
}

// Usage
var service = new ChatService(chatClient);
var session = await _agent.CreateSessionAsync();

var reply1 = await service.ChatAsync("Find running shoes", session, ct);
var reply2 = await service.ChatAsync("Any other colors?", session, ct);
// Agent remembers context — "other colors" refers to running shoes
```

## Key Conventions

- `CreateSessionAsync()` must be called before `RunAsync`
- Multiple `RunAsync` calls on the same Session share history
- To clear history, create a new Session or call `ChatHistoryProvider.SetMessages()`
- `InMemoryChatHistoryProvider` state is stored in `AgentSession.StateBag`, not instance fields
- Message order: system messages → ChatHistoryProvider history → AIContextProvider messages → current user message
- Services with server-side history management (e.g., Azure AI Foundry) conflict with local ChatHistoryProvider — see conflict detection rules
