# ShoppingAssistantAgent → HarnessAgent 迁移设计

> 状态：已确认 | 日期：2026-07-05

## 一、目标

将 `ShoppingAssistantAgent` 从手动构建的 `ChatClientAgent` 迁移到 MAF `HarnessAgent`，并新增两项能力：
1. **OpenTelemetry 可观测性** — 在 Aspire Dashboard 中可视化 Agent 调用链路
2. **会话偏好记忆** — 同一次对话中主动记住并应用用户偏好

## 二、背景

### 当前架构

```
ChatEndpoints → ShoppingAssistantAgent → chatClient.AsAIAgent()
                   ├── BuildInstructions()     静态注入关键词表
                   ├── SqliteChatHistoryProvider  加载20条历史
                   └── RunAsync<AgentChatResult>  结构化输出
```

### 目标架构

```
ChatEndpoints → ShoppingAssistantAgent → HarnessAgent (框架内置管线)
                   ├── HarnessInstructions         基础指令 + 关键词表
                   ├── SqliteChatHistoryProvider   保留 SQLite 历史（20→2）
                   ├── PreferenceMemoryProvider    🆕 会话偏好记忆
                   ├── OpenTelemetry               ✅ 自动开启
                   └── RunAsync<AgentChatResult>   保留结构化输出
```

## 三、HarnessAgent 能力清单

| 能力 | 是否开启 | 理由 |
|------|---------|------|
| OpenTelemetry 追踪 | ✅ 默认开 | 核心目标 |
| 工具审批 (ToolApproval) | ❌ | 购物助手无工具 |
| 循环执行 (LoopEvaluator) | ❌ | 购物对话单轮即可 |
| 网络搜索 (WebSearch) | ❌ | 暂不需要 |
| 文件记忆 (FileMemory) | ❌ | 无文件交互场景 |
| 文件访问 (FileAccess) | ❌ | 无文件交互场景 |
| Todo 提供者 | ❌ | 购物需求足够简单 |
| Agent 技能 | ❌ | 暂不需要 |
| 消息注入 | ✅ 默认开 | 框架自动处理 |
| 聊天持久化 | ✅ 默认开 | 框架自动处理 |

## 四、变更清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/AIShop.Api/AIShop.Api.csproj` | 改 | +`Microsoft.Agents.AI.Harness` (1.10.0) |
| `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | 重写 | 迁到 `new HarnessAgent(chatClient, options)` |
| `src/AIShop.Api/Agents/PreferenceMemoryProvider.cs` | 🆕 新建 | 会话偏好注入 ContextProvider |
| `src/AIShop.Api/Features/Chat/ChatEndpoints.cs` | 改 | `AgentChatResult` 加 `Preferences` 字段；偏好持久化到 StateBag |
| `src/AIShop.Infrastructure/Services/SqliteChatHistoryProvider.cs` | 改 | 加载上限 20 → 2 |
| `src/AIShop.ServiceDefaults/Extensions.cs` | 改 | +Agent OTel Source `Experimental.Microsoft.Agents.AI` |

## 五、数据模型

### AgentChatResult（扩展）

```csharp
public sealed record AgentChatResult(
    string Reply,
    string[] Keywords,
    string[]? Preferences   // 🆕 Agent 从对话中提取的偏好
);
```

### PreferenceMemoryProvider

```csharp
public class PreferenceMemoryProvider : AIContextProvider
{
    public override Task<string> ProvideContextAsync(
        AIContextProviderContext context, CancellationToken ct = default)
    {
        var prefs = context.Session?.StateBag.GetValue<string>("Preferences");
        if (string.IsNullOrWhiteSpace(prefs))
            return Task.FromResult(string.Empty);

        return Task.FromResult($"""
            【已知用户偏好】
            {prefs}

            请在推荐商品时优先考虑以上偏好。
            """);
    }
}
```

### 偏好生命周期

```
POST /api/chat
  → PreferenceMemoryProvider 从 StateBag 读取 "Preferences"，注入 prompt
  → Agent 回复 + 提取 Preferences[]
  → 端点层: session.StateBag.SetValue("Preferences", ...)  ← 持久化到 StateBag
  → 同一会话下次请求自动带出
  → 会话结束自动清除（StateBag 生命周期 = 会话）
```

## 六、可观测性

### 追踪链路

```
HTTP POST /api/chat                         ← AspNetCore 自动产出
  └── invoke_agent ShoppingAssistant        ← OpenTelemetryAgent 产出
        │  Tag: gen_ai.agent.name = "ShoppingAssistant"
        │  Tag: gen_ai.operation.name = "invoke_agent"
        │
        └── chat (LLM call)                 ← OpenTelemetryChatClient 产出
             │  Tag: gen_ai.request.model = "gpt-4.1"
             │  Tag: gen_ai.usage.input_tokens
             │  Tag: gen_ai.usage.output_tokens
             │  Duration
```

### 关键配置

ServiceDefaults 需额外监听 Agent Source，否则 Dashboard 不显示 Agent Span：

```csharp
tracing.AddSource("Experimental.Microsoft.Agents.AI");
```

### Dashboard 查看路径

启动 AppHost → Dashboard (`https://localhost:17099`) → **Traces** 页面 → 按服务 `api` 过滤 → 展开 `invoke_agent ShoppingAssistant`

## 七、不做的事（明确排除）

- 不跨会话记忆偏好（仅会话内）
- 不用循环执行、网络搜索、文件记忆、Todo
- Program.cs DI 注册不变（`AddScoped<ShoppingAssistantAgent>()`）
- 历史加载从 20 减到 2，不改变其他 SQLite 行为
