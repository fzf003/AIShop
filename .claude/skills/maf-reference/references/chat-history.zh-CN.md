<!-- lastSynced: 2026-06-24, source: chat-history.md -->

# ChatHistoryProvider

当底层 AI 服务不在服务端存储对话历史时，由 ChatHistoryProvider 管理对话历史。

## 生命周期

```
LLM 调用前：  InvokingAsync → ProvideChatHistoryAsync() → 返回历史消息（预置到用户消息前）
LLM 调用后：  InvokedAsync → StoreChatHistoryAsync() → 持久化新消息
```

## InMemoryChatHistoryProvider（内置）

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    // 可选：压缩策略
    ChatReducer = new SummarizationCompactionStrategy(chatClient),
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
});
```

状态存储在 `AgentSession.StateBag` 中，键为 `"InMemoryChatHistoryProvider"`。

## 访问已存储的消息

```csharp
var provider = new InMemoryChatHistoryProvider();
var messages = provider.GetMessages(session);   // List<ChatMessage>
provider.SetMessages(session, newMessages);     // 覆盖
```

## 与服务端管理历史的冲突

如果 AI 服务返回了 `ConversationId`（例如 Azure AI Foundry）**且**配置了 `ChatHistoryProvider`：

- `ClearOnChatHistoryProviderConflict = true`（默认）→ ChatHistoryProvider 被设置为 null
- `ThrowOnChatHistoryProviderConflict = true`（默认）→ 抛出 `InvalidOperationException`
- `WarnOnChatHistoryProviderConflict = true`（默认）→ 记录警告日志

解决方法：移除 ChatHistoryProvider，或将 `ThrowOnChatHistoryProviderConflict` 设为 false。
