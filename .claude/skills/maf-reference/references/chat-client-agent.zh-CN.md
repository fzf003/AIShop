<!-- lastSynced: 2026-06-24, source: chat-client-agent.md -->

# ChatClientAgent

唯一内置的 `AIAgent` 实现。包装 `IChatClient`（来自 `Microsoft.Extensions.AI`）。

## 构造

```csharp
// 简单用法
var agent = new ChatClientAgent(chatClient, instructions: "你是助手", name: "MyAgent");

// 完整选项
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ShoppingAssistant",
    Description = "智能购物助手",
    ChatOptions = new ChatOptions
    {
        Instructions = "你是购物助手",
        Tools = myTools
    },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(),
    AIContextProviders = new[] { ragProvider, preferenceExtractor },
    UseProvidedChatClientAsIs = false,   // 默认值 — 自动包装 IChatClient
});
```

## ChatClientAgentOptions 关键属性

| 属性 | 默认值 | 用途 |
|---|---|---|
| `Name` | null | 代理显示名称 |
| `Description` | null | 多代理场景中使用 |
| `ChatOptions` | null | 默认 ChatOptions（指令、工具等） |
| `ChatHistoryProvider` | `InMemoryChatHistoryProvider()` | 管理对话历史 |
| `AIContextProviders` | null | 上下文提供者列表 |
| `UseProvidedChatClientAsIs` | false | 若为 true，跳过 IChatClient 的自动装饰 |
| `RequirePerServiceCallChatHistoryPersistence` | false | 每次服务调用后持久化历史 |
| `EnableMessageInjection` | false | 允许在函数循环中注入消息 |
| `ClearOnChatHistoryProviderConflict` | true | 如果服务管理历史，自动清除 ChatHistoryProvider |
| `WarnOnChatHistoryProviderConflict` | true | 冲突时记录警告 |
| `ThrowOnChatHistoryProviderConflict` | true | 冲突时抛出异常 |

## RunAsync 内部流程

1. 创建或复用 `AgentSession`
2. `ChatHistoryProvider.InvokingAsync()` → 加载历史（预置到消息前）
3. 链式执行 `AIContextProvider.InvokingAsync()` → 追加 Instructions/Messages/Tools
4. 合并所有上下文，调用 `IChatClient.GetResponseAsync()`
5. 链式执行 `AIContextProvider.InvokedAsync()` → 处理结果
6. `ChatHistoryProvider.InvokedAsync()` → 持久化新消息
7. 返回 `AgentResponse`

## 自动装饰的 IChatClient 管道（当 UseProvidedChatClientAsIs = false 时）

```
FunctionInvokingChatClient              ← 自动执行函数调用
  └─ PerServiceCallChatHistoryPersistingChatClient  ← 可选，当 RequirePerServiceCallChatHistoryPersistence=true 时
     └─ NonApprovalRequiredFunctionBypassingChatClient  ← 可选，当 EnableNonApprovalRequiredFunctionBypassing=true 时
        └─ 原始 IChatClient
```

## 会话对话 ID 处理

- 如果底层服务返回了 `ConversationId`（例如 Azure AI Foundry），会话会存储它。
- 如果配置了 `ChatHistoryProvider` **且** 服务返回了 ConversationId，则会触发冲突检测。
- 默认：`ClearOnChatHistoryProviderConflict = true` → ChatHistoryProvider 被设置为 null。
