---
name: maf-reference
description: >
  Microsoft Agent Framework (MAF) for .NET reference. Use when working with
  Microsoft.Agents.AI, Microsoft.Agents.AI.Workflows, Microsoft.Agents.AI.Mcp,
  or Microsoft.Agents.AI.Hosting packages. Covers ChatClientAgent, AIContextProvider,
  ChatHistoryProvider, tools/functions, DelegatingAIAgent, compaction strategies,
  DI/hosting registration, workflow orchestration (Sequential, Handoff, GroupChat,
  Magentic One), MCP integration, and A2A. Also use when creating or modifying
  agents, agent workflows, or agent hosting in a .NET project that references
  Microsoft.Agents.* NuGet packages.
---

# Microsoft Agent Framework Reference

Source repo: `E:\github\ProActor\aspire13app\agent-framework` (read for latest patterns).
NuGet packages: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Hosting`,
`Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Mcp`, `Microsoft.Extensions.AI`.

## Read order

When working with MAF, read the relevant reference files in this order:

1. [core-abstractions.md](references/core-abstractions.md) — AIAgent, AIContext, AgentSession
2. [chat-client-agent.md](references/chat-client-agent.md) — ChatClientAgent construction and RunAsync
3. [context-provider.md](references/context-provider.md) — AIContextProvider middleware pattern
4. [chat-history.md](references/chat-history.md) — ChatHistoryProvider
5. [conversations.md](references/conversations.md) — Multi-turn conversation management (Session, StateBag, dialog flow)
6. [tools.md](references/tools.md) — AITool / function tool registration
6. [decorators.md](references/decorators.md) — DelegatingAIAgent and built-in decorators
7. [compaction.md](references/compaction.md) — Context window compaction strategies
8. [hosting-di.md](references/hosting-di.md) — AddAIAgent, keyed services
9. [workflows.md](references/workflows.md) — Sequential, Handoff, GroupChat, Magentic One
10. [mcp.md](references/mcp.md) — MCP client integration

Also available:
- [agent-api.md](references/agent-api.md) — Quick API type reference
- [nuget-packages.md](references/nuget-packages.md) — Package versions and compatibility
- [usage-patterns.md](references/usage-patterns.md) — Common usage patterns
- [journey-best-practices.md](references/journey-best-practices.md) — Agent development journey best practices (progressive complexity, intelligence spectrum, phase-by-phase guide)
- [structured-outputs.md](references/structured-outputs.md) — Structured output generation (`RunAsync<T>`, `ResponseFormat`, JSON Schema)

> **Recommended pre-read**: If you're unfamiliar with MAF architecture design, read `journey-best-practices.md` first to understand the overall design philosophy and decision principles.

Chinese versions (`.zh-CN.md`) are also available in the same directory for all core references above.

## Key namespace map

| NuGet Package | Primary Namespace | Purpose |
|---|---|---|
| Microsoft.Agents.AI | `Microsoft.Agents.AI` | Core abstractions + ChatClientAgent |
| Microsoft.Agents.AI.Workflows | `Microsoft.Agents.AI.Workflows` | Multi-agent orchestration |
| Microsoft.Agents.AI.Mcp | `Microsoft.Agents.AI.Mcp` | MCP tool integration |
| Microsoft.Agents.AI.Hosting | `Microsoft.Agents.AI.Hosting` | DI registration helpers |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` | IChatClient, AITool, ChatMessage |

## Quick patterns

```csharp
// Minimal agent (no DI)
var agent = new ChatClientAgent(chatClient, "你是助手", "MyAgent");
var session = await agent.CreateSessionAsync();
var response = await agent.RunAsync("Hello", session);

// DI-registered agent
builder.AddAIAgent("Agent", "你是助手");
var agent = sp.GetRequiredKeyedService<AIAgent>("Agent");

// Sequential workflow
var workflow = agents.BuildSequential().Build();
var hostAgent = workflow.AsAIAgent();

// Handoff workflow
var workflow = new HandoffWorkflowBuilder(coordinator)
    .WithHandoff(coordinator, specialist)
    .Build();

// Group chat
var workflow = new GroupChatWorkflowBuilder()
    .AddParticipants(agent1, agent2)
    .Build();
```

## Important conventions

- All Agents are keyed services in DI; key = agent name string.
- `InMemoryChatHistoryProvider` stores state in `AgentSession.StateBag`, never in instance fields.
- Multiple providers must have unique `StateKeys`.
- Suppress `MAAI001` with `#pragma warning disable` when using `InvokingContext`/`InvokedContext`.
- `ChatClientAgent` clones `ChatClientAgentOptions` at construction; external edits have no effect.
- Default `UseProvidedChatClientAsIs = false` — framework auto-wraps with `FunctionInvokingChatClient`.
