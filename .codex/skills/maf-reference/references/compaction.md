# Context Window Compaction Strategies

When conversation history exceeds the LLM context window, use compaction strategies to reduce history.

## CompactionStrategy interface

```csharp
public interface CompactionStrategy
{
    IReadOnlyList<ChatMessage> Compact(IReadOnlyList<ChatMessage> messages);
}
```

## Built-in strategies

| Strategy | Class | Behavior |
|---|---|---|
| Truncation | `TruncationCompactionStrategy` | Keep last N messages, drop old ones |
| Sliding Window | `SlidingWindowCompactionStrategy` | Keep system message + last N turns |
| Summarization | `SummarizationCompactionStrategy` | Summarize old messages into one summary using LLM |
| Pipeline | `PipelineCompactionStrategy` | Combine multiple strategies in sequence |
| Tool Result | `ToolResultCompactionStrategy` | Prune tool call results |
| Context Window | `ContextWindowCompactionStrategy` | Dynamic truncation based on token count |

## Summarization strategy (most common)

```csharp
using Microsoft.Agents.AI;

var reducer = new SummarizationCompactionStrategy(chatClient, new()
{
    MaxTokens = 2000,
    SummaryInstructions = "Summarize the following conversation concisely"
});

var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = reducer,
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
});
```

## Sliding window strategy

```csharp
var slidingWindow = new SlidingWindowCompactionStrategy
{
    MessageLimit = 10  // keep last 10 messages + system message
};
```

## Pipeline strategy (combine multiple)

```csharp
var pipeline = new PipelineCompactionStrategy(
    new ToolResultCompactionStrategy(),    // first prune tool results
    new SummarizationCompactionStrategy(chatClient) // then summarize
);
```

## Configure on ChatHistoryProvider

```csharp
new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = reducer,
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
        // or AfterMessageStorage
});
```

## Trigger timing

- `BeforeMessagesRetrieval`: Compact before loading history (recommended, reduces tokens per request)
- `AfterMessageStorage`: Compact after storing new messages (good for async background compaction)
