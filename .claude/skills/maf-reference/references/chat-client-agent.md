<!-- lastSynced: 2026-06-24 -->

# ChatClientAgent

The only built-in `AIAgent` implementation. Wraps `IChatClient` (from `Microsoft.Extensions.AI`).

## Construction

```csharp
// Simple
var agent = new ChatClientAgent(chatClient, instructions: "你是助手", name: "MyAgent");

// Full options
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ShoppingAssistant",
    Description = "智能购物助手",
    ChatOptions = new ChatOptions
    {
        Instructions = "你是购物助手",
        Tools = myTools
    },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(),
    AIContextProviders = new[] { ragProvider, preferenceExtractor },
    UseProvidedChatClientAsIs = false,   // default — auto-wraps IChatClient
});
```

## ChatClientAgentOptions key properties

| Property | Default | Purpose |
|---|---|---|
| `Name` | null | Agent display name |
| `Description` | null | Used in multi-agent scenarios |
| `ChatOptions` | null | Default ChatOptions (instructions, tools, etc.) |
| `ChatHistoryProvider` | `InMemoryChatHistoryProvider()` | Manages conversation history |
| `AIContextProviders` | null | List of context providers |
| `UseProvidedChatClientAsIs` | false | If true, skip auto-decoration of IChatClient |
| `RequirePerServiceCallChatHistoryPersistence` | false | Persist history after each service call |
| `EnableMessageInjection` | false | Allow message injection into function loop |
| `ClearOnChatHistoryProviderConflict` | true | Auto-clear ChatHistoryProvider if service manages history |
| `WarnOnChatHistoryProviderConflict` | true | Log warning on conflict |
| `ThrowOnChatHistoryProviderConflict` | true | Throw on conflict |

## RunAsync internal flow

1. Create or reuse `AgentSession`
2. `ChatHistoryProvider.InvokingAsync()` → load history (prepended to messages)
3. Chain `AIContextProvider.InvokingAsync()` → append Instructions/Messages/Tools
4. Merge all context, call `IChatClient.GetResponseAsync()`
5. Chain `AIContextProvider.InvokedAsync()` → process results
6. `ChatHistoryProvider.InvokedAsync()` → persist new messages
7. Return `AgentResponse`

## Auto-decorated IChatClient pipeline (when UseProvidedChatClientAsIs = false)

```
FunctionInvokingChatClient              ← auto-executes function calls
  └─ PerServiceCallChatHistoryPersistingChatClient  ← optional, when RequirePerServiceCallChatHistoryPersistence=true
     └─ NonApprovalRequiredFunctionBypassingChatClient  ← optional, when EnableNonApprovalRequiredFunctionBypassing=true
        └─ original IChatClient
```

## Session conversation ID handling

- If the underlying service returns a `ConversationId` (e.g. Azure AI Foundry), the session stores it.
- If a `ChatHistoryProvider` is configured AND the service returns a ConversationId, conflict detection triggers.
- Default: `ClearOnChatHistoryProviderConflict = true` → ChatHistoryProvider is set to null.
