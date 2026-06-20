# AIContextProvider — Middleware Pattern

AIContextProvider is the framework's core extensibility mechanism, equivalent to middleware.

## Two-phase lifecycle

```
Before LLM call:  InvokingAsync(InvokingContext) → returns AIContext { Instructions, Messages, Tools }
                  ↓
              LLM actual call
                  ↓
After LLM call:   InvokedAsync(InvokedContext) → process results
```

Multiple providers chain: each receives the previous provider's output and appends its own.

## Custom provider template

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class RagProvider(ISearchService search) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var query = context.AIContext.Messages?.LastOrDefault()?.Text ?? "";
        var docs = search.Search(query, topK: 5);

        var ragMessages = docs.Select(d =>
            new ChatMessage(ChatRole.System, $"参考知识：{d.Content}"));

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = ragMessages
        });
    }
}

// Post-invocation: extract preferences, log, etc.
public class PreferenceExtractor : AIContextProvider
{
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct)
    {
        // Store in AgentSession.StateBag, NOT instance fields
        var state = context.Session?.StateBag;
        return default;
    }
}
```

## Message filtering (default behavior)

| Filter | Default | Purpose |
|---|---|---|
| `ProvideInputMessageFilter` | External only | Which messages passed to `ProvideAIContextAsync` |
| `StoreInputRequestMessageFilter` | External only | Which request messages passed to `StoreAIContextAsync` |
| `StoreInputResponseMessageFilter` | All | Which response messages passed to `StoreAIContextAsync` |

## State storage

- **Never store session state in provider instance fields** — one provider instance serves many sessions.
- Use `AgentSession.StateBag` via `context.Session?.StateBag`.
- `StateKeys` (default: `[typeof(YourProvider).Name]`) must be unique across all providers.

## Registering providers

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    AIContextProviders = new AIContextProvider[]
    {
        new RoleContextProvider("你是专业购物顾问"),
        new RagProvider(searchService),
        new PreferenceExtractor()
    }
});
```
