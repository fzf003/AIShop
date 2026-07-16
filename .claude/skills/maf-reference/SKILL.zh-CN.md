---
name: maf-reference-zh-CN
description: >
  Microsoft Agent Framework (MAF) for .NET 中文参考。在使用 Microsoft.Agents.AI、
  Microsoft.Agents.AI.Workflows、Microsoft.Agents.AI.Mcp 或 Microsoft.Agents.AI.Hosting
  包时使用。涵盖 ChatClientAgent、AIContextProvider、ChatHistoryProvider、工具/函数、
  DelegatingAIAgent、压缩策略、DI/托管注册、工作流编排（Sequential、Handoff、GroupChat、
  Magentic One）、MCP 集成和 A2A。在创建或修改引用 Microsoft.Agents.* NuGet 包的
  .NET 项目中的代理、代理工作流或代理托管时也可使用。
license: MIT
metadata:
  mafVersion: "1.10.0"
  lastReviewed: "2026-06-24"
applyTo: "**/*.cs,**/*.csproj,**/*.md"
---

# Microsoft Agent Framework 参考

源代码仓库：<https://github.com/microsoft/agent-framework/tree/main/dotnet>（阅读最新模式）。
示例代码：<https://github.com/microsoft/agent-framework/tree/main/dotnet/samples>（参考示例代码）。
NuGet 包：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.OpenAI`、`Microsoft.Agents.AI.Hosting`、
`Microsoft.Agents.AI.Workflows`、`Microsoft.Agents.AI.Mcp`、`Microsoft.Extensions.AI`。

> **.NET 特定指南**：始终使用 `async`/`await` 模式来执行代理操作和工作流；遵循 .NET 类型安全和依赖注入约定；保持服务注册、配置和身份验证与标准的 .NET 托管模式一致；在 .NET 应用程序模型中以惯用的方式使用中间件、上下文提供程序和编排组件；在引入新的 API 或工作流模式之前，先查看最新的 .NET 示例。

---

## 🧊 自动保鲜机制 — 防止知识过时

MAF 处于活跃更新期。每次技能被调用时，**强制运行以下保鲜检查**：

### 第一步：对比版本号

> **跨平台说明**：以下同时提供 PowerShell 和 bash 版本，根据当前 shell 环境选用。

```powershell
# PowerShell — 获取项目实际引用的 MAF 版本
$actual = dotnet list package 2>$null `
  | Select-String "Microsoft.Agents.AI" `
  | Select-Object -First 1 `
  | ForEach-Object { $_.ToString().Split(' ')[-1] }
# 参考文档版本（见 frontmatter）
$docVersion = "1.10.0"
```

```bash
# bash — 获取项目实际引用的 MAF 版本
ACTUAL=$(dotnet list package 2>/dev/null | grep Microsoft.Agents.AI | head -1 | awk '{print $NF}')
# 参考文档版本（见 frontmatter）
DOC_VERSION="1.10.0"
```

### 第二步：根据结果分支

| 情况 | 行动 |
|------|------|
| `$ACTUAL == $DOC_VERSION`（或 `$actual -eq $docVersion`） | ✅ 版本匹配，正常使用参考文件 |
| `$ACTUAL` 为空（项目无 MAF 包） | ✅ 正常使用，仅提供通用参考 |
| `$ACTUAL` 和 `$DOC_VERSION` 不同 | ⚠️ **版本不匹配** — 执行下面降级策略 |

### 第三步：版本不匹配时的降级策略

```yaml
# 当版本不匹配时，参考文件可能已过时，执行以下兜底：
1. 警告用户版本差异（示例："项目引用 v2.0.0，参考文档基于 v1.10.0，可能存在差异"）
2. 优先从官方 GitHub 仓库获取最新 API 信息：
   - WebFetch: https://github.com/microsoft/agent-framework/tree/main/dotnet
   - WebFetch: https://www.nuget.org/packages/Microsoft.Agents.AI  # 看最新版本号
3. 对于代码生成，优先使用 GitHub samples 中的模式而非本地参考文件
4. 生成代码后标注已知可能不兼容的部分
```

### 第四步：定期提醒

如果连续 3 次调用都发现版本不匹配且 `lastReviewed` 超过 30 天，**提醒用户手动审查并更新参考文件**。

---

## 触发条件（主动激活）

被调用 `/maf-reference` 或以下任一条件满足时**自动激活**：

| 信号 | 检测方式 | 行动 |
|------|---------|------|
| 项目引用 `Microsoft.Agents.*` 包 | `grep Microsoft.Agents *.csproj` | 检查版本匹配，确认是否需要更新参考 |
| 编写/修改 Agent 代码 | 用户说"写一个 agent"、"创建 workflow"、"添加 MCP 工具" | 进入**代码生成模式** |
| Agent 相关 Bug 排查 | 编译错误、运行时异常、DI 注册失败 | 进入**诊断模式** |
| 用户问 MAF API 问题 | "ChatClientAgent 怎么用"、"GroupChat 支持什么" | 进入**问答路由模式** |

调用优先级：诊断 > 代码生成 > 问答路由。先检查项目状态，再响应具体需求。

---

## 执行流程 — 被调用时的步骤

### 第一阶段：上下文收集

> **跨平台说明**：以下同时提供 PowerShell 和 bash 命令，根据当前 shell 环境选用。

```powershell
# 1. 检查项目中的 MAF 包版本
dotnet list package | Select-String Microsoft.Agents

# 2. 检查 Agent 定义文件
Get-ChildItem -Path src -Recurse -Filter "*.cs" `
  | Where-Object { $_.Directory.Name -eq "Agents" }

# 3. 检查 DI 注册方式
Select-String -Path src/**/*.cs -Pattern "AddAIAgent|AddChatClient|AddSingletonAgent"
```

```bash
# 1. 检查项目中的 MAF 包版本
dotnet list package | grep Microsoft.Agents

# 2. 检查 Agent 定义文件
ls src/**/Agents/*.cs 2>/dev/null

# 3. 检查 DI 注册方式
grep -r "AddAIAgent\|AddAIShoppingAgent\|AddChatClient\|AddSingletonAgent" src/ --include="*.cs"
```

根据收集结果：
- **版本不匹配**（参考文档 `1.10.0` vs 项目实际版本）→ 提示用户版本差异
- **无 Agent 定义** → 进入**代码生成模式**
- **有 Agent 定义但有编译/运行时问题** → 进入**诊断模式**

### 第二阶段：路由到具体行动

| 用户意图 | 行动 |
|---------|------|
| "创建一个新 Agent" | → 代码生成：`ChatClientAgent` 脚手架 + DI 注册 |
| "实现多 Agent 工作流" | → 代码生成：Sequential / Handoff / GroupChat 模板 |
| "添加工具调用" | → 读取 `tools.md` + 生成 AITool 注册代码 |
| "Agent 启动报错" | → 诊断模式：检查 DI、命名空间、配置 |
| "我要用 MCP" | → 读取 `mcp.md` + 生成 MCP 客户端配置 |
| "理解某个概念" | → 问答路由：定位到对应参考文件 |

---

### 第三阶段：参考文件路由

根据用户问题定位到最相关的参考文件，**只读取需要的文件**而非全部：

| 用户问及 | 读取文件 | 关键内容 |
|---------|---------|----------|
| AIAgent / 会话生命周期 | `references/core-abstractions.zh-CN.md` | AIAgent, AIContext, AgentSession |
| ChatClientAgent / 调用 LLM | `references/chat-client-agent.zh-CN.md` | 构造方法, RunAsync, 选项配置 |
| 中间件 / 上下文处理 | `references/context-provider.zh-CN.md` | AIContextProvider 中间件模式 |
| 聊天历史 / 多轮对话 | `references/chat-history.zh-CN.md` | ChatHistoryProvider |
| 对话管理 / StateBag | `references/conversations.zh-CN.md` | Session, StateBag, 对话流程 |
| 工具注册 / Function Calling | `references/tools.md` | AITool 注册, FunctionInvokingChatClient |
| 装饰器 / 拦截器 | `references/decorators.md` | DelegatingAIAgent, 内置装饰器 |
| 上下文压缩 | `references/compaction.md` | 窗口压缩策略 |
| DI 注册 / 托管 | `references/hosting-di.md` | AddAIAgent, 键控服务 |
| 工作流编排 | `references/workflows.md` | Sequential, Handoff, GroupChat, Magentic One |
| MCP 集成 | `references/mcp.md` | MCP 客户端配置 |
| 结构化输出 | `references/structured-outputs.zh-CN.md` | RunAsync\<T\>, JSON Schema |
| 完整 API 速查 | `references/agent-api.md` | 类型速查表 |
| 开发策略 | `references/journey-best-practices.md` | 渐进式复杂度, 智能光谱 |

> **规则**：只读需要的参考文件。读完立即根据内容行动 — 生成代码、诊断问题或回答用户。

---

### 第四阶段：代码脚手架

根据需求从以下模板生成代码，**按需组合**：

#### 4a. 最小化 ChatClientAgent（无 DI）

```csharp
// 适合快速验证、控制台应用、测试
var agent = new ChatClientAgent(chatClient, "你是助手", "MyAgent");
var session = await agent.CreateSessionAsync();
var response = await agent.RunAsync("Hello", session);
```

#### 4b. DI 注册的 Agent（ASP.NET Core）

```csharp
// Program.cs
builder.AddAIAgent("ShoppingAgent", "你是一个购物助手");
// 注入使用
var agent = sp.GetRequiredKeyedService<AIAgent>("ShoppingAgent");
```

#### 4c. 顺序工作流

```csharp
// 1. 先创建各个 Agent（需已注入 IChatClient）
var translator = chatClient.AsAIAgent("你是法语翻译", "FrenchTranslator");
var reviewer = chatClient.AsAIAgent("你是英语翻译", "EnglishTranslator");

// 2. 构建顺序工作流
var workflow = new SequentialWorkflowBuilder()
    .AddParticipant(translator)
    .AddParticipant(reviewer)
    .Build();

// 3. 包装为 Agent 接口使用
var hostAgent = workflow.AsAIAgent("translation-workflow", "翻译工作流");
var session = await hostAgent.CreateSessionAsync();
var response = await hostAgent.RunAsync("Hello world", session);
```

#### 4d. 交接工作流

```csharp
// 1. 创建协调员和专家 Agent
var coordinator = chatClient.AsAIAgent(
    "你是客服协调员，将任务转给专家", "Coordinator");
var billing = chatClient.AsAIAgent(
    "你是账单专家", "BillingAgent");
var tech = chatClient.AsAIAgent(
    "你是技术支持专家", "TechSupportAgent");

// 2. 构建交接工作流
var workflow = new HandoffWorkflowBuilder(coordinator)
    .WithHandoff(coordinator, billing)
    .WithHandoff(coordinator, tech)
    .Build();

// 3. 包装为 Agent 接口使用
var agent = workflow.AsAIAgent("CustomerService Workflow");
```

#### 4e. 群聊

```csharp
// 1. 创建参与 Agent
var agent1 = chatClient.AsAIAgent("你是产品经理", "PM");
var agent2 = chatClient.AsAIAgent("你是开发者", "Dev");
var agent3 = chatClient.AsAIAgent("你是设计师", "Designer");

// 2. 构建群聊工作流
var workflow = new GroupChatWorkflowBuilder()
    .AddParticipants(agent1, agent2, agent3)
    .Build();

// 3. 包装为 Agent 接口使用
var agent = workflow.AsAIAgent("Design Discussion");
```

#### 4f. 带 AITool 的 Agent

```csharp
// 参考 tools.md 获取完整工具注册方式
builder.AddAIAgent("Agent", "你是助手")
    .WithTools(tool1, tool2);
```

> 生成代码后，**立即运行 `dotnet build`** 验证编译通过。

---

### 第五阶段：诊断模式

当用户遇到 MAF 相关错误时按此检查清单排查：

#### DI 注册问题
```
[症状] InvalidOperationException: No service for type 'AIAgent'
[检查] ❓ 项目是否调用了 builder.AddAIAgent()？
[检查] ❓ 是否用 GetRequiredKeyedService 而非 GetRequiredService？
```

#### 命名空间问题
```
[症状] ❌ 类型 'ChatClientAgent' 找不到
[检查] ✅ using Microsoft.Agents.AI;
[检查] ✅ using Microsoft.Extensions.AI;  // IChatClient, AITool, ChatMessage
```

#### Session/状态问题
```
[症状] 💬 对话历史不保留
[检查] ❓ 是否使用了 InMemoryChatHistoryProvider？
[警告] ⚠️ 状态存储在 AgentSession.StateBag，不要放在实例字段！
```

#### MAAI001 警告
```
[症状] ⚠️ 编译警告 MAAI001
[修复] #pragma warning disable MAAI001
         // ... 使用 InvokingContext/InvokedContext 的代码
         #pragma warning restore MAAI001
```

#### ChatClientAgent 选项不生效
```
[症状] 🔧 修改 ChatClientAgentOptions 后无效果
[原因] ChatClientAgent 在构造时克隆选项对象
[修复] 在构造前设置好所有选项
```

#### Function Calling 不触发
```
[症状] 🤖 Agent 不调用注册的工具
[检查] ❓ UseProvidedChatClientAsIs 是否为 false（默认）？
[说明] 默认 false 时框架自动用 FunctionInvokingChatClient 包装
```

---

### 第六阶段：验证

完成任何代码修改后执行：

```bash
# 1. 编译验证
dotnet build

# 2. 检查 MAF NuGet 版本是否与参考文档版本一致
#    当前参考版本: 1.10.0
dotnet list package | grep Microsoft.Agents
```

---

## 重要约定

- 所有代理在 DI 中都是键控服务；键 = 代理名称字符串。
- `InMemoryChatHistoryProvider` 将状态存储在 `AgentSession.StateBag` 中，切勿存储在实例字段中。
- 多个提供者必须拥有唯一的 `StateKeys`。
- 使用 `InvokingContext`/`InvokedContext` 时，用 `#pragma warning disable` 抑制 `MAAI001`。
- `ChatClientAgent` 在构造时克隆 `ChatClientAgentOptions`；外部修改不会生效。
- 默认 `UseProvidedChatClientAsIs = false` — 框架自动用 `FunctionInvokingChatClient` 包装。

---

## 版本漂移检查

- 当前参考文档基于 MAF **1.10.0**
- 如果项目引用的版本不同，**提示用户**版本差异可能导致 API 不匹配
- 建议定期对照 <https://github.com/microsoft/agent-framework/tree/main/dotnet> 确认最新 API

---

## 关键命名空间映射

| NuGet 包 | 主命名空间 | 用途 |
|---------|-----------|------|
| Microsoft.Agents.AI | `Microsoft.Agents.AI` | 核心抽象 + ChatClientAgent |
| Microsoft.Agents.AI.Workflows | `Microsoft.Agents.AI.Workflows` | 多代理编排 |
| Microsoft.Agents.AI.Mcp | `Microsoft.Agents.AI.Mcp` | MCP 工具集成 |
| Microsoft.Agents.AI.Hosting | `Microsoft.Agents.AI.Hosting` | DI 注册辅助方法 |
| Microsoft.Extensions.AI | `Microsoft.Extensions.AI` | IChatClient、AITool、ChatMessage |

## 完整参考阅读顺序

当需要系统学习 MAF 时按以下顺序阅读参考文件：

1. [core-abstractions.zh-CN.md](references/core-abstractions.zh-CN.md) — AIAgent、AIContext、AgentSession
2. [chat-client-agent.zh-CN.md](references/chat-client-agent.zh-CN.md) — ChatClientAgent 构造和 RunAsync
3. [context-provider.zh-CN.md](references/context-provider.zh-CN.md) — AIContextProvider 中间件模式
4. [chat-history.zh-CN.md](references/chat-history.zh-CN.md) — ChatHistoryProvider
5. [conversations.zh-CN.md](references/conversations.zh-CN.md) — 多轮对话管理（Session、StateBag、对话流程）
6. [tools.md](references/tools.md) — AITool / 函数工具注册
7. [decorators.md](references/decorators.md) — DelegatingAIAgent 和内置装饰器
8. [compaction.md](references/compaction.md) — 上下文窗口压缩策略
9. [hosting-di.md](references/hosting-di.md) — AddAIAgent、键控服务
10. [workflows.md](references/workflows.md) — Sequential、Handoff、GroupChat、Magentic One
11. [mcp.md](references/mcp.md) — MCP 客户端集成
12. [structured-outputs.zh-CN.md](references/structured-outputs.zh-CN.md) — 结构化输出（`RunAsync<T>`、`ResponseFormat`、JSON Schema）
13. **可选先读** — [journey-best-practices.md](references/journey-best-practices.md) — MAF 开发历程最佳实践（渐进式复杂度、智能光谱、阶段指南）

> **可选先读**：如果你对 MAF 架构设计还不太熟悉，建议先读第 13 项了解整体设计思路和决策原则。
