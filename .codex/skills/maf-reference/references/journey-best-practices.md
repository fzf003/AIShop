# Agent 开发历程最佳实践

> 来源：[Microsoft Agent Framework 开发历程](https://learn.microsoft.com/zh-cn/agent-framework/journey/)
> 本文档将官方开发历程转化为 AIShop 项目的可执行规范。

## 核心理念

### 渐进式复杂度

**每个阶段只添加当前方案需要的复杂度。** 从最简单的模式开始，仅在需求明确时升级。

```
LLM 基础知识 → 从 LLM 到代理 → 添加工具 → 添加技能 → 添加中间件
  → 上下文提供器 → 代理作为工具 → A2A → 工作流
```

**原则**：能用单一 Agent 解决的问题，不要引入工作流。能用工具解决的，不要引入 RAG。

### 智能光谱

```
完全由模型决策                             完全由代码决策
◄──────────────────────────────────────────────────────────────►
│                         │                         │
│  单一 Agent + 工具      │  工作流 + Agent 执行器    │  纯确定性执行器
│  模型决定每一步         │  图控制流程, Agent 处理   │  无 LLM 参与
│                        │  推理密集型步骤           │  纯业务逻辑
```

关键洞察：**你掌控调节旋钮**。对流程中的每一步，决定：
- 模型应该自己决定怎么做？ → 使用 Agent 执行器
- 代码应该确定结果？ → 使用确定性执行器
- 需要人工介入？ → 使用人工审核关卡

---

## 阶段详解与项目规范

### 阶段 1：LLM 基础知识

**适用时机**：理解 LLM 的基本工作原理和限制。

**关键概念**：

| 概念 | 说明 | 项目影响 |
|---|---|---|
| 令牌（Token） | LLM 的基本处理单位，计费和上下文窗口均以此计 | 监控 token 消耗，优化提示长度 |
| 上下文窗口 | 模型一次能处理的最大令牌数 | 对话历史需要压缩策略 |
| 温度（Temperature） | 控制输出随机性 | Agent 应用建议 0–0.3 |
| 工具调用 | 模型生成结构化输出来表示工具调用请求 | 框架自动处理执行循环 |

**AIShop 规范**：
- Agent 温度设置：使用 `ChatOptions.Temperature = 0.2`
- 监控 `ChatResponse.Usage` 追踪 token 消耗
- 使用 `CompactionStrategy` 管理上下文窗口

### 阶段 2：从 LLM 到代理

**适用时机**：需要从单纯的聊天完成升级到有角色和指令的 Agent。

**核心转变**：

```
LLM API 调用（无状态）  →  Agent（有指令、有角色、有状态）
```

**AIShop 规范**：
- 使用 `ChatClientAgent` 而非直接调用 `IChatClient.GetResponseAsync()`
- Agent 必须设置 `Name` 和 `Description` 用于日志和识别
- 指令（Instructions）要清晰、具体、包含行为边界

```csharp
// ✅ 正确：使用 Agent 抽象
var agent = new ChatClientAgent(chatClient, "你是购物助手", "ShoppingAssistant");

// ❌ 避免：直接调用 LLM（除非有特殊理由）
// await chatClient.GetResponseAsync(messages);
```

### 阶段 3：添加工具

**适用时机**：Agent 需要与外部系统交互（查询数据库、搜索商品、调用 API）。

**关键规则**：

| 方面 | 说明 |
|---|---|
| 工具说明质量 | 模型的工具调用准确度取决于 `[Description]` 的质量——写清楚每个参数的含义 |
| 令牌消耗 | 工具定义占用上下文窗口，工具越多，留给对话的令牌越少 |
| 安全边界 | LLM 只生成工具调用请求，框架执行实际代码——这是一个关键安全边界 |
| 参数验证 | 工具参数视为不可信输入，必须做验证 |

**AIShop 规范**：
- 所有工具方法必须带 `[Description]` 和参数 `[Description]`
- 工具签名使用 C# 14 强类型参数，避免使用 `string` 传 JSON
- 工具内做参数校验，不信任 LLM 生成的参数
- 商品搜索等高频工具优先注册为 `AIFunction`

```csharp
// ✅ 正确：完整描述
[Description("搜索商品")]
static ProductDto[] SearchProducts(
    [Description("搜索关键字，如'电子产品'")] string keyword,
    [Description("价格上限，0 表示不限")] decimal maxPrice = 0)
{
    // 参数验证
    ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
    // ...
}
```

### 阶段 4：添加技能

**适用时机**：需要将可重用的 Agent 功能打包成模块。

**核心概念**：技能（Skills）是一组打包的工具、指令和知识，可以被多个 Agent 共享。

**AIShop 规范**：暂未使用，后续可用 `AgentMcpSkillsSource` 从 MCP 服务器发现技能。

### 阶段 5：添加中间件

**适用时机**：需要横切关注点（日志、审计、限流、护栏）。

**可用装饰器**：

| 装饰器 | 用途 |
|---|---|
| `LoggingAgent` | 记录 Agent 输入/输出 |
| `OpenTelemetryAgent` | 添加追踪和指标 |
| `FunctionInvocationDelegatingAgent` | 工具调用中间件（审核、记录） |
| 自定义 `DelegatingAIAgent` | 限流、内容过滤等 |

**AIShop 规范**：
- 生产环境至少启用 `LoggingAgent` 或 `OpenTelemetryAgent`
- 涉及用户数据的工具调用需用 `FunctionInvocationDelegatingAgent` 做审核层
- 限流装饰器用于防止 LLM 过载

### 阶段 6：上下文提供器

**适用时机**：Agent 需要记忆、个性化知识或 RAG。

**核心模式**：

```
ChatHistoryProvider（对话历史）
  + AIContextProvider（动态上下文注入）
    → 完整的 Agent 上下文
```

| 提供器 | 职责 | 存储位置 |
|---|---|---|
| `ChatHistoryProvider` | 加载/保存对话历史 | `AgentSession.StateBag` |
| `AIContextProvider` | 注入指令/消息/工具 | `AgentSession.StateBag`（自定义键） |

**AIShop 规范**：
- 对话历史用 `InMemoryChatHistoryProvider` 管理
- 商品知识推荐用自定义 `AIContextProvider`（如 `PreferenceAnalyzer`）
- 提供器状态存在 `AgentSession.StateBag`，**禁止存在实例字段**

```csharp
// ✅ 正确
public class ProductContextProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(...) { ... }
    protected override ValueTask StoreAIContextAsync(...)
    {
        var state = context.Session?.StateBag; // ✅
        return default;
    }
}

// ❌ 错误：不要存在实例字段
private List<ChatMessage> _myState = new(); // ❌ 不跨会话共享
```

### 阶段 7：代理作为工具

**适用时机**：一个 Agent 需要将任务委托给另一个专门化的 Agent。

**模式**：Agent A 将 Agent B 注册为其工具，当 A 判断任务需要 B 的专长时，将控制权交给 B。

**AIShop 规范**：
- 推荐使用 `HandoffWorkflow` 替代手动"代理作为工具"——更明确控制交接逻辑
- 如需"代理作为工具"模式，确保子 Agent 有明确的 `Description` 供模型判断

### 阶段 8：代理对代理（A2A）

**适用时机**：Agent 需要跨服务或组织边界通信。

**AIShop 规范**：当前项目暂不涉及，留待后续扩展。

### 阶段 9：工作流

**适用时机**：过程的**结构**提前已知（步骤、顺序、决策点固定），不能用单一 Agent 可靠处理。

**选择指南**：

| 问题 | 单一 Agent | 工作流 |
|---|---|---|
| 过程结构是否提前已知？ | 否 — 模型决定 | 是 — 图定义路径 |
| 是否需要在固定点人工介入？ | 工具级审批 | 定义点的显式入口 |
| 是否需要从故障中恢复？ | 重试逻辑 | 检查点恢复 |
| 是否有多个独立并行步骤？ | 不可靠 | 内置并发支持 |

**内置编排模式**：

| 模式 | 用途 |
|---|---|
| 顺序（Sequential） | Agent 按定义顺序逐个执行 |
| 并发（Concurrent） | Agent 并行执行，降低延迟 |
| 切换（Handoff/Switch） | Agent 根据上下文互相传输控制 |
| 群聊（GroupChat） | 多 Agent 在共享对话中协作 |
| Magentic One | 管理器 Agent 动态协调专用 Agent |

**AIShop 规范**：
- 先用单一 Agent + 工具尝试，**工作流是最后的手段**
- 使用工作流时，优先用内置编排模式而非自定义
- 工作流完成后用 `.AsAIAgent()` 包装为 Agent 接口

```csharp
// 工作流选择的决策流程：
// 1. 单一 Agent + 工具能解决？ → 用它
// 2. 需要固定顺序但模型能处理？ → Sequential
// 3. 需要路由到专家？ → Handoff
// 4. 需要并行或复杂恢复？ → Workflow
```

---

## 项目全局规范

### 温度设置

```csharp
new ChatOptions
{
    Temperature = 0.2f,  // Agent 应用建议 0–0.3
    // 创意任务可用 0.7–1.0
}
```

### Token 管理

- 监控 `ChatResponse.Usage.TotalTokenCount`
- 对话历史用 `SummarizationCompactionStrategy` 压缩
- 工具定义尽量精简 `[Description]` 节省 token

### 错误处理

- LLM 可能生成错误的工具调用参数 → 工具内做验证
- LLM 可能产生幻觉 → 关键输出做校验/约束
- LLM 可能不调用工具 → 提示词中强调工具使用

### 安全边界

- 工具参数视为不可信输入
- 用户消息可能包含提示注入
- LLM 输出视为不可信输出，渲染前需消毒

### 监控与可观测性

- 使用 `LoggingAgent` 记录所有 Agent 交互
- 使用 `OpenTelemetryAgent` 收集追踪数据
- 监控 token 消耗和延迟

---

## 参考

- [Agent Framework 开发历程](https://learn.microsoft.com/zh-cn/agent-framework/journey/)
- [Agent Framework 官方文档](https://learn.microsoft.com/zh-cn/agent-framework/) — 完整 API 参考、教程、概念、最佳实践
- [MAF 技能参考](../.codex/skills/maf-reference/SKILL.md)
- [AGENTS.md](../AGENTS.md)
