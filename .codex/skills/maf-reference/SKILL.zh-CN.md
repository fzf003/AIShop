---
name: maf-reference-zh-CN
description: >
  Microsoft Agent Framework (MAF) for .NET 中文参考。在使用 Microsoft.Agents.AI、
  Microsoft.Agents.AI.Workflows、Microsoft.Agents.AI.Mcp 或 Microsoft.Agents.AI.Hosting
  包时使用。涵盖 ChatClientAgent、AIContextProvider、ChatHistoryProvider、工具/函数、
  DelegatingAIAgent、压缩策略、DI/托管注册、工作流编排（Sequential、Handoff、GroupChat、
  Magentic One）、MCP 集成和 A2A。在创建或修改引用 Microsoft.Agents.* NuGet 包的
  .NET 项目中的代理、代理工作流或代理托管时也可使用。
---

# Microsoft Agent Framework 参考

源代码仓库：`E:\github\ProActor\aspire13app\agent-framework`（阅读最新模式）。
NuGet 包：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.OpenAI`、`Microsoft.Agents.AI.Hosting`、
`Microsoft.Agents.AI.Workflows`、`Microsoft.Agents.AI.Mcp`、`Microsoft.Extensions.AI`。

## 阅读顺序

使用 MAF 时，按以下顺序阅读相关参考文件：

1. [core-abstractions.zh-CN.md](references/core-abstractions.zh-CN.md) — AIAgent、AIContext、AgentSession
2. [chat-client-agent.zh-CN.md](references/chat-client-agent.zh-CN.md) — ChatClientAgent 构造和 RunAsync
3. [context-provider.zh-CN.md](references/context-provider.zh-CN.md) — AIContextProvider 中间件模式
4. [chat-history.zh-CN.md](references/chat-history.zh-CN.md) — ChatHistoryProvider
5. [conversations.zh-CN.md](references/conversations.zh-CN.md) — 多轮对话管理（Session、StateBag、对话流程）
6. [tools.md](references/tools.md) — AITool / 函数工具注册
6. [decorators.md](references/decorators.md) — DelegatingAIAgent 和内置装饰器
7. [compaction.md](references/compaction.md) — 上下文窗口压缩策略
8. [hosting-di.md](references/hosting-di.md) — AddAIAgent、键控服务
9. [workflows.md](references/workflows.md) — Sequential、Handoff、GroupChat、Magentic One
10. [mcp.md](references/mcp.md) — MCP 客户端集成
11. [structured-outputs.zh-CN.md](references/structured-outputs.zh-CN.md) — 结构化输出（`RunAsync<T>`、`ResponseFormat`、JSON Schema）
12. **可选先读** — [journey-best-practices.md](references/journey-best-practices.md) — MAF 开发历程最佳实践（渐进式复杂度、智能光谱、阶段指南）

> **可选先读**：如果你对 MAF 架构设计还不太熟悉，建议先读第 12 项了解整体设计思路和决策原则。

## 关键命名空间映射

| NuGet 包 | 主命名空间 | 用途 |
|---|---|---|
| Microsoft.Agents.AI | `Microsoft.Agents.AI` | 核心抽象 + ChatClientAgent |
| Microsoft.Agents.AI.Workflows | `Microsoft.Agents.AI.Workflows` | 多代理编排 |
| Microsoft.Agents.AI.Mcp | `Microsoft.Agents.AI.Mcp` | MCP 工具集成 |
| Microsoft.Agents.AI.Hosting | `Microsoft.Agents.AI.Hosting` | DI 注册辅助方法 |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` | IChatClient、AITool、ChatMessage |

## 快速模式

```csharp
// 最小化代理（无 DI）
var agent = new ChatClientAgent(chatClient, "你是助手", "MyAgent");
var session = await agent.CreateSessionAsync();
var response = await agent.RunAsync("Hello", session);

// DI 注册的代理
builder.AddAIAgent("Agent", "你是助手");
var agent = sp.GetRequiredKeyedService<AIAgent>("Agent");

// 顺序工作流
var workflow = agents.BuildSequential().Build();
var hostAgent = workflow.AsAIAgent();

// 交接工作流
var workflow = new HandoffWorkflowBuilder(coordinator)
    .WithHandoff(coordinator, specialist)
    .Build();

// 群聊
var workflow = new GroupChatWorkflowBuilder()
    .AddParticipants(agent1, agent2)
    .Build();
```

## 重要约定

- 所有代理在 DI 中都是键控服务；键 = 代理名称字符串。
- `InMemoryChatHistoryProvider` 将状态存储在 `AgentSession.StateBag` 中，切勿存储在实例字段中。
- 多个提供者必须拥有唯一的 `StateKeys`。
- 使用 `InvokingContext`/`InvokedContext` 时，用 `#pragma warning disable` 抑制 `MAAI001`。
- `ChatClientAgent` 在构造时克隆 `ChatClientAgentOptions`；外部修改不会生效。
- 默认 `UseProvidedChatClientAsIs = false` — 框架自动用 `FunctionInvokingChatClient` 包装。
