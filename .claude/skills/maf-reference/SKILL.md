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
license: MIT
metadata:
  mafVersion: "1.10.0"
  lastReviewed: "2026-06-24"
---

# Microsoft Agent Framework Reference

Source repo: <https://github.com/microsoft/agent-framework/tree/main/dotnet> (read for latest patterns).
Samples: <https://github.com/microsoft/agent-framework/tree/main/dotnet/samples> (参考示例代码).
NuGet packages: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Hosting`,
`Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Mcp`, `Microsoft.Extensions.AI`.

> **.NET 特定指南**：始终使用 `async`/`await` 模式来执行代理操作和工作流；遵循 .NET 类型安全和依赖注入约定；保持服务注册、配置和身份验证与标准的 .NET 托管模式一致；在 .NET 应用程序模型中以惯用的方式使用中间件、上下文提供程序和编排组件；在引入新的 API 或工作流模式之前，先查看最新的 .NET 示例。

---

## 🧊 Freshness Protection — 自动防过时

MAF 处于活跃更新期。每次技能被调用时，**强制运行以下保鲜检查**：

### Step 1: 对比版本号

```bash
# 获取项目实际引用的 MAF 版本
ACTUAL=$(dotnet list package 2>/dev/null | grep Microsoft.Agents.AI | head -1 | awk '{print $NF}')
# 参考文档版本（见 frontmatter）
DOC_VERSION="1.10.0"
```

### Step 2: 根据结果分支

| 情况 | 行动 |
|------|------|
| `$ACTUAL == $DOC_VERSION` | ✅ 版本匹配，正常使用参考文件 |
| `$ACTUAL` 为空（项目无 MAF 包） | ✅ 正常使用，仅提供通用参考 |
| `$ACTUAL` 和 `$DOC_VERSION` 不同 | ⚠️ **版本不匹配** — 执行下面降级策略 |

### Step 3: 版本不匹配时的降级策略

```yaml
# 当版本不匹配时，参考文件可能已过时，执行以下兜底：
1. 警告用户版本差异（示例："项目引用 v2.0.0，参考文档基于 v1.10.0，可能存在差异"）
2. 优先从官方 GitHub 仓库获取最新 API 信息：
   - WebFetch: https://github.com/microsoft/agent-framework/tree/main/dotnet
   - WebFetch: https://www.nuget.org/packages/Microsoft.Agents.AI  # 看最新版本号
3. 对于代码生成，优先使用 GitHub samples 中的模式而非本地参考文件
4. 生成代码后标注已知可能不兼容的部分
```

### Step 4: 定期提醒

如果连续 3 次调用都发现版本不匹配且 `lastReviewed` 超过 30 天，**提醒用户手动审查并更新参考文件**。

---

## When to trigger (proactive)

被调用 /maf-reference 或以下任一条件满足时**自动激活**：

| Signal | How to Detect | Action |
|--------|--------------|--------|
| 项目引用 `Microsoft.Agents.*` 包 | `grep Microsoft.Agents *.csproj` | 检查版本匹配，确认是否需要更新参考 |
| 编写/修改 Agent 代码 | 用户说"写一个 agent"、"创建 workflow"、"添加 MCP 工具" | 进入**代码生成模式** |
| Agent 相关 Bug 排查 | 编译错误、运行时异常、DI 注册失败 | 进入**诊断模式** |
| 用户问 MAF API 问题 | "ChatClientAgent 怎么用"、"GroupChat 支持什么" | 进入**问答路由模式** |

调用优先级：诊断 > 代码生成 > 问答路由。先检查项目状态，再响应具体需求。

---

## Workflow — 被调用时的执行流程

### Phase 1: Context gathering（上下文收集）

```bash
# 1. 检查项目中的 MAF 包版本
dotnet list package | grep Microsoft.Agents

# 2. 检查 Agent 定义文件
ls src/**/Agents/*.cs 2>/dev/null

# 3. 检查 DI 注册方式
grep -r "AddAIAgent\|AddAIShoppingAgent\|AddChatClient\|AddSingletonAgent" src/ --include="*.cs"
```

根据收集结果：
- **版本不匹配**（参考文档 `1.10.0` vs 项目引用的版本）→ 提示用户版本差异，建议核实 API 变更
- **无 Agent 定义** → 进入**代码生成模式**
- **有 Agent 定义但有编译/运行时问题** → 进入**诊断模式**

### Phase 2: Route to action（路由到具体行动）

| User intent | Action |
|-------------|--------|
| "创建一个新 Agent" | → 代码生成：`ChatClientAgent` 脚手架 + DI 注册 |
| "实现多 Agent 工作流" | → 代码生成：Sequential / Handoff / GroupChat 模板 |
| "添加工具调用" | → 读取 `tools.md` + 生成 AITool 注册代码 |
| "Agent 启动报错" | → 诊断模式：检查 DI、命名空间、配置 |
| "我要用 MCP" | → 读取 `mcp.md` + 生成 MCP 客户端配置 |
| "理解某个概念" | → 问答路由：定位到对应参考文件 |

---

## Phase 3: Reference routing（参考文件路由）

根据用户问题定位到最相关的参考文件，**只读取需要的文件**而非全部：

| 用户问及 | 读取文件 | 关键内容 |
|----------|---------|----------|
| AIAgent / 会话生命周期 | `references/core-abstractions.md` | AIAgent, AIContext, AgentSession |
| ChatClientAgent / 调用 LLM | `references/chat-client-agent.md` | 构造方法, RunAsync, 选项配置 |
| 中间件 / 上下文处理 | `references/context-provider.md` | AIContextProvider 中间件模式 |
| 聊天历史 / 多轮对话 | `references/chat-history.md` | ChatHistoryProvider |
| 对话管理 / StateBag | `references/conversations.md` | Session, StateBag, 对话流程 |
| 工具注册 / Function Calling | `references/tools.md` | AITool 注册, FunctionInvokingChatClient |
| 装饰器 / 拦截器 | `references/decorators.md` | DelegatingAIAgent, 内置装饰器 |
| 上下文压缩 | `references/compaction.md` | 窗口压缩策略 |
| DI 注册 / 托管 | `references/hosting-di.md` | AddAIAgent, 键控服务 |
| 工作流编排 | `references/workflows.md` | Sequential, Handoff, GroupChat, Magentic One |
| MCP 集成 | `references/mcp.md` | MCP 客户端配置 |
| 结构化输出 | `references/structured-outputs.md` | RunAsync\<T\>, JSON Schema |
| 完整 API 速查 | `references/agent-api.md` | 类型速查表 |
| 开发策略 | `references/journey-best-practices.md` | 渐进式复杂度, 智能光谱 |

> **规则**：只读需要的参考文件。读完立即根据内容行动 — 生成代码、诊断问题或回答用户。

---

## Phase 4: Code scaffolding（代码脚手架）

根据需求从以下模板生成代码，**按需组合**：

### 4a. Minimal ChatClientAgent（无 DI）

```csharp
// 适合快速验证、控制台应用、测试
var agent = new ChatClientAgent(chatClient, "你是助手", "MyAgent");
var session = await agent.CreateSessionAsync();
var response = await agent.RunAsync("Hello", session);
```

### 4b. DI 注册的 Agent（ASP.NET Core）

```csharp
// Program.cs
builder.AddAIAgent("ShoppingAgent", "你是一个购物助手");
// 注入使用
var agent = sp.GetRequiredKeyedService<AIAgent>("ShoppingAgent");
```

### 4c. Sequential Workflow

```csharp
var workflow = agents.BuildSequential().Build();
var hostAgent = workflow.AsAIAgent();
```

### 4d. Handoff Workflow

```csharp
var workflow = new HandoffWorkflowBuilder(coordinator)
    .WithHandoff(coordinator, specialist)
    .Build();
```

### 4e. Group Chat

```csharp
var workflow = new GroupChatWorkflowBuilder()
    .AddParticipants(agent1, agent2)
    .Build();
```

### 4f. 带 AITool 的 Agent

```csharp
// 参考 tools.md 获取完整工具注册方式
builder.AddAIAgent("Agent", "你是助手")
    .WithTools(tool1, tool2);
```

> 生成代码后，**立即运行 `dotnet build`** 验证编译通过。

---

## Phase 5: Diagnostics（诊断模式）

当用户遇到 MAF 相关错误时按此检查清单排查：

### DI 注册问题
```
[症状] InvalidOperationException: No service for type 'AIAgent'
[检查] 项目是否调用了 builder.AddAIAgent()？
[检查] 是否用 GetRequiredKeyedService 而非 GetRequiredService？
```

### 命名空间问题
```
[症状] 类型 'ChatClientAgent' 找不到
[检查] using Microsoft.Agents.AI;
[检查] using Microsoft.Extensions.AI;  // IChatClient, AITool, ChatMessage
```

### Session/状态问题
```
[症状] 对话历史不保留
[检查] 是否使用了 InMemoryChatHistoryProvider？
[警告] 状态存储在 AgentSession.StateBag，不要放在实例字段！
```

### MAAI001 警告
```
[症状] 编译警告 MAAI001
[修复] #pragma warning disable MAAI001
         // ... 使用 InvokingContext/InvokedContext 的代码
         #pragma warning restore MAAI001
```

### ChatClientAgent 选项不生效
```
[症状] 修改 ChatClientAgentOptions 后无效果
[原因] ChatClientAgent 在构造时克隆选项对象
[修复] 在构造前设置好所有选项
```

### Function Calling 不触发
```
[症状] Agent 不调用注册的工具
[检查] UseProvidedChatClientAsIs 是否为 false（默认）？
[说明] 默认 false 时框架自动用 FunctionInvokingChatClient 包装
```

---

## Phase 6: Validation（验证）

完成任何代码修改后执行：

```bash
# 1. 编译验证
dotnet build

# 2. 检查 MAF NuGet 版本是否与参考文档版本一致
#    当前参考版本: 1.10.0
dotnet list package | grep Microsoft.Agents
```

---

## Important conventions（重要约定）

- All Agents are keyed services in DI; key = agent name string.
- `InMemoryChatHistoryProvider` stores state in `AgentSession.StateBag`, never in instance fields.
- Multiple providers must have unique `StateKeys`.
- Suppress `MAAI001` with `#pragma warning disable` when using `InvokingContext`/`InvokedContext`.
- `ChatClientAgent` clones `ChatClientAgentOptions` at construction; external edits have no effect.
- Default `UseProvidedChatClientAsIs = false` — framework auto-wraps with `FunctionInvokingChatClient`.

---

## Version drift check（版本漂移检查）

- 当前参考文档基于 MAF **1.10.0**
- 如果项目引用的版本不同，**提示用户**版本差异可能导致 API 不匹配
- 建议定期对照 <https://github.com/microsoft/agent-framework/tree/main/dotnet> 确认最新 API

---

## Key namespace map

| NuGet Package | Primary Namespace | Purpose |
|---|---|---|
| Microsoft.Agents.AI | `Microsoft.Agents.AI` | Core abstractions + ChatClientAgent |
| Microsoft.Agents.AI.Workflows | `Microsoft.Agents.AI.Workflows` | Multi-agent orchestration |
| Microsoft.Agents.AI.Mcp | `Microsoft.Agents.AI.Mcp` | MCP tool integration |
| Microsoft.Agents.AI.Hosting | `Microsoft.Agents.AI.Hosting` | DI registration helpers |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` | IChatClient, AITool, ChatMessage |

## Read order（完整参考阅读顺序）

当需要系统学习 MAF 时按以下顺序阅读参考文件：

1. [core-abstractions.md](references/core-abstractions.md) — AIAgent, AIContext, AgentSession
2. [chat-client-agent.md](references/chat-client-agent.md) — ChatClientAgent construction and RunAsync
3. [context-provider.md](references/context-provider.md) — AIContextProvider middleware pattern
4. [chat-history.md](references/chat-history.md) — ChatHistoryProvider
5. [conversations.md](references/conversations.md) — Multi-turn conversation management (Session, StateBag, dialog flow)
6. [tools.md](references/tools.md) — AITool / function tool registration
7. [decorators.md](references/decorators.md) — DelegatingAIAgent and built-in decorators
8. [compaction.md](references/compaction.md) — Context window compaction strategies
9. [hosting-di.md](references/hosting-di.md) — AddAIAgent, keyed services
10. [workflows.md](references/workflows.md) — Sequential, Handoff, GroupChat, Magentic One
11. [mcp.md](references/mcp.md) — MCP client integration

Also available:
- [agent-api.md](references/agent-api.md) — Quick API type reference
- [nuget-packages.md](references/nuget-packages.md) — Package versions and compatibility
- [usage-patterns.md](references/usage-patterns.md) — Common usage patterns
- [journey-best-practices.md](references/journey-best-practices.md) — Agent development journey best practices
- [structured-outputs.md](references/structured-outputs.md) — Structured output generation

> **Recommended pre-read**: If you're unfamiliar with MAF architecture design, read `journey-best-practices.md` first.

Chinese versions (`.zh-CN.md`) are also available in the same directory for all core references above.
