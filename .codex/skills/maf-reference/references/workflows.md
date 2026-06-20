# Workflow Orchestration

Multi-agent orchestration via `Microsoft.Agents.AI.Workflows`.

## Namespace

```csharp
using Microsoft.Agents.AI.Workflows;
```

## SequentialWorkflow

Agents execute sequentially, each receiving the previous agent's output:

```csharp
var agent1 = chatClient.AsAIAgent("You are a French translator", "FrenchTranslator");
var agent2 = chatClient.AsAIAgent("You are an English translator", "EnglishTranslator");

var workflow = new SequentialWorkflowBuilder()
    .AddParticipant(agent1)
    .AddParticipant(agent2)
    .Build();

var hostAgent = workflow.AsAIAgent("translation-workflow", "Translation Workflow");
var session = await hostAgent.CreateSessionAsync();
var response = await hostAgent.RunAsync("Hello world", session);
```

## HandoffWorkflow

Coordinator agent hands off tasks to specialized agents:

```csharp
var coordinator = chatClient.AsAIAgent(
    "You are a客服 coordinator, hand off tasks to specialists", "Coordinator");
var billing = chatClient.AsAIAgent("You are a billing specialist", "BillingAgent");
var tech = chatClient.AsAIAgent("You are a tech support specialist", "TechSupportAgent");

var workflow = new HandoffWorkflowBuilder(coordinator)
    .WithHandoff(coordinator, billing)
    .WithHandoff(coordinator, tech)
    .Build();

var agent = workflow.AsAIAgent("CustomerService Workflow");
```

## GroupChat

Multiple agents participate in the same conversation, coordinated by a group chat manager:

```csharp
var agent1 = chatClient.AsAIAgent("You are a PM", "PM");
var agent2 = chatClient.AsAIAgent("You are a Developer", "Dev");
var agent3 = chatClient.AsAIAgent("You are a Designer", "Designer");

var workflow = new GroupChatWorkflowBuilder()
    .AddParticipants(agent1, agent2, agent3)
    .Build();

var agent = workflow.AsAIAgent("Design Discussion");
```

## MagenticOne

Orchestrator agent dynamically plans tasks and manages workers:

```csharp
var orchestrator = chatClient.AsAIAgent("You are the orchestrator", "Orchestrator");
var worker1 = chatClient.AsAIAgent("You are a search expert", "Searcher");
var worker2 = chatClient.AsAIAgent("You are a coding expert", "Coder");

var workflow = new MagenticWorkflowBuilder(orchestrator)
    .AddParticipant(worker1)
    .AddParticipant(worker2)
    .Build();

var agent = workflow.AsAIAgent("MagenticOne Workflow");
```

## Workflow As Agent

After building, call `.AsAIAgent()` to use like any `AIAgent`:

```csharp
var workflow = builder.Build();
var agent = workflow.AsAIAgent("workflow-agent", "Workflow Agent");
var session = await agent.CreateSessionAsync();

// Supports streaming
await foreach (var update in agent.RunStreamingAsync("input"))
{
    Console.WriteLine(update);
}
```

## Custom executor (low-level)

```csharp
Func<string, string> toUpper = s => s.ToUpperInvariant();
var uppercase = toUpper.BindAsExecutor("Uppercase");

class ReverseExecutor : Executor<string, string>("Reverse")
{
    public override ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(string.Concat(message.Reverse()));
}

var builder = new WorkflowBuilder(uppercase);
builder.AddEdge(uppercase, new ReverseExecutor()).WithOutputFrom(new ReverseExecutor());
var workflow = builder.Build();

await using var run = await InProcessExecution.RunAsync(workflow, "Hello");
```

## Key conventions

- Agents in workflows need `Description` — used for handoff/orchestration decisions
- `HandoffWorkflow` coordinator needs explicit handoff instructions
- `GroupChat` uses RoundRobin manager by default
- Use `.WithOutputFrom(executor)` to mark the final output node
