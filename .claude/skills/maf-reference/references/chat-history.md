<!-- lastSynced: 2026-06-24 -->

# ChatHistoryProvider

Manages conversation history when the underlying AI service does not store it server-side.

## Lifecycle

```
Before LLM call:  InvokingAsync → ProvideChatHistoryAsync() → returns history messages (prepended to user messages)
After LLM call:   InvokedAsync → StoreChatHistoryAsync() → persist new messages
```

## InMemoryChatHistoryProvider (built-in)

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    // Optional: compression strategy
    ChatReducer = new SummarizationCompactionStrategy(chatClient),
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
});
```

State lives in `AgentSession.StateBag` under key `"InMemoryChatHistoryProvider"`.

## Access stored messages

```csharp
var provider = new InMemoryChatHistoryProvider();
var messages = provider.GetMessages(session);   // List<ChatMessage>
provider.SetMessages(session, newMessages);     // overwrite
```

## Conflict with service-managed history

If the AI service returns a `ConversationId` (e.g. Azure AI Foundry) AND a `ChatHistoryProvider` is configured:

- `ClearOnChatHistoryProviderConflict = true` (default) → ChatHistoryProvider is set to null
- `ThrowOnChatHistoryProviderConflict = true` (default) → throws `InvalidOperationException`
- `WarnOnChatHistoryProviderConflict = true` (default) → logs warning

Resolution: either remove the ChatHistoryProvider, or set `ThrowOnChatHistoryProviderConflict = false`.
