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

## Custom CompactionStrategy

Implement the `CompactionStrategy` interface for custom logic:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class KeywordFilterCompactionStrategy : CompactionStrategy
{
    private readonly HashSet<string> _keywords;
    private readonly int _maxMessages;

    public KeywordFilterCompactionStrategy(
        IEnumerable<string> keywords, int maxMessages = 50)
    {
        _keywords = new(keywords.Select(k => k.ToLowerInvariant()));
        _maxMessages = maxMessages;
    }

    public IReadOnlyList<ChatMessage> Compact(IReadOnlyList<ChatMessage> messages)
    {
        // 保留包含关键字的系统/用户消息 + 最近的 N 条
        var important = messages.Where(m =>
            m.Role == ChatRole.System ||
            _keywords.Any(k => m.Text?.Contains(k, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();

        // 追加最近的消息确保上下文不丢失
        var recent = messages.TakeLast(_maxMessages);
        return important.Concat(recent).Distinct().ToList();
    }
}

// 使用
var customReducer = new KeywordFilterCompactionStrategy(
    keywords: ["价格", "退款", "投诉"], maxMessages: 30);
```

## Trigger timing

- `BeforeMessagesRetrieval`: Compact before loading history (recommended, reduces tokens per request)
- `AfterMessageStorage`: Compact after storing new messages (good for async background compaction)
