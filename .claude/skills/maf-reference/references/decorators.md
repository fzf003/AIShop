# DelegatingAIAgent — Decorator Pattern

`DelegatingAIAgent` implements the decorator pattern for `AIAgent`, enabling composable agent pipelines.

## Core concept

```
Outer decorator → Inner decorator → ... → Actual Agent (ChatClientAgent)
```

Each decorator can intercept/enhance `RunAsync` and `RunStreamingAsync`, delegating core logic to the inner agent.

## DelegatingAIAgent base class

```csharp
public abstract class DelegatingAIAgent : AIAgent
{
    protected AIAgent InnerAgent { get; }

    // Default pass-through to InnerAgent
    protected override Task<AgentResponse> RunCoreAsync(...)
        => this.InnerAgent.RunAsync(messages, session, options, ct);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(...)
        => this.InnerAgent.RunStreamingAsync(messages, session, options, ct);
}
```

## Built-in decorators

### FunctionInvocationDelegatingAgent

Injects middleware before/after tool calls:

```csharp
agent = agent.WithFunctionInvocationMiddleware(async (agent, context, next, ct) =>
{
    // Before: log, validate, review
    Console.WriteLine($"Tool call: {context.Function.Name}, args: {context.Arguments}");

    var result = await next(context, ct); // execute actual tool

    // After: log result, post-process
    Console.WriteLine($"Tool result: {result}");

    return result;
});
```

### LoggingAgent

Automatically logs agent input/output:

```csharp
agent = agent.WithLogging();
```

### OpenTelemetryAgent

Adds OpenTelemetry tracing/metrics:

```csharp
agent = agent.WithOpenTelemetry();
// Auto-generates Activity and Metrics for agent invocations
```

## Custom decorator

```csharp
public class RateLimitingAgent : DelegatingAIAgent
{
    private readonly SemaphoreSlim _semaphore = new(5);

    public RateLimitingAgent(AIAgent innerAgent) : base(innerAgent) { }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await InnerAgent.RunAsync(messages, session, options, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
```

## Pipeline composition

```csharp
AIAgent agent = new ChatClientAgent(chatClient, "You are helpful", "MyAgent");
agent = new LoggingAgent(agent);            // logging
agent = new RateLimitingAgent(agent);       // rate limiting
agent = agent.WithFunctionInvocationMiddleware(...); // tool middleware
```
