<!-- lastSynced: 2026-06-24, source: conversations.md -->

# Conversational Agent（对话管理）

管理 Agent 与用户之间的多轮对话，包含会话创建、历史管理、上下文维护。

## 命名空间

```csharp
using Microsoft.Agents.AI;
```

## 核心概念

### 会话（Session）

`AgentSession` 是对话的容器。每次 `RunAsync` 在同一个 Session 上调用时，对话历史会持续累积。

```csharp
// 创建新会话
var session = await agent.CreateSessionAsync(cancellationToken: ct);

// 同一 Session 上的多次调用累积历史
var response1 = await agent.RunAsync("你好", session, cancellationToken: ct);
var response2 = await agent.RunAsync("推荐跑鞋", session, cancellationToken: ct);
// Session 中现在包含两轮对话
```

### 会话状态（StateBag）

`AgentSession.StateBag` 存储会话级别状态，包括对话历史、上下文提供器状态等。

```csharp
var state = session.StateBag;

// 读写自定义状态
state["myKey"] = myValue;
var value = state["myKey"];
```

## 对话历史管理

### InMemoryChatHistoryProvider（内存）

适用于无状态服务或开发环境：

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions());

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ShoppingAssistant",
    ChatHistoryProvider = provider
});

var session = await agent.CreateSessionAsync();
// 历史自动管理
```

### 自定义 ChatHistoryProvider

适用于需要持久化历史（数据库、Redis 等）的场景：

```csharp
public sealed class DbChatHistoryProvider : ChatHistoryProvider
{
    protected override async ValueTask<ChatHistory> ProvideChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // 从数据库加载历史（预置到用户消息前）
        return new ChatHistory(await LoadFromDbAsync(context.Session, ct));
    }

    protected override async ValueTask StoreChatHistoryAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // 将新消息持久化到数据库
        await SaveToDbAsync(context.Session, context.Messages, ct);
    }
}
```

> **注意**：所有状态存储在 `AgentSession.StateBag` 中，**切勿存储在实例字段**。

## 上下文窗口压缩

长时间对话会消耗大量 token，需要使用压缩策略：

```csharp
var provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = new SummarizationCompactionStrategy(chatClient),
    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions
        .ChatReducerTriggerEvent.BeforeMessagesRetrieval
});
```

详见 [compaction.md](compaction.md)。

## 上下文提供器（AIContextProvider）

用于在每次 LLM 调用时注入动态上下文：

```csharp
public sealed class PreferenceContextProvider : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        return new AIContext
        {
            Instructions = "根据用户历史偏好推荐商品",
            Messages = [new ChatMessage(ChatRole.System, "用户偏好：运动、户外")]
        };
    }

    protected override async ValueTask StoreAIContextAsync(
        AgentRequestContext context, CancellationToken ct)
    {
        // 状态存 StateBag，不存实例字段
        var state = context.Session?.StateBag;
    }
}
```

详见 [context-provider.md](context-provider.md)。

## 完整的多轮对话示例

```csharp
public sealed class ChatService
{
    private readonly ChatClientAgent _agent;

    public ChatService(IChatClient chatClient)
    {
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            ChatHistoryProvider = new InMemoryChatHistoryProvider()
        });
    }

    public async Task<string> ChatAsync(string message, AgentSession session, CancellationToken ct)
    {
        var response = await _agent.RunAsync(message, session, cancellationToken: ct);
        return response.Text ?? "";
    }
}

// 使用
var service = new ChatService(chatClient);
var session = await _agent.CreateSessionAsync();

var reply1 = await service.ChatAsync("帮我找跑鞋", session, ct);
var reply2 = await service.ChatAsync("还有别的颜色吗？", session, ct);
// Agent 记住前文，"别的颜色"指跑鞋的颜色
```

## 关键约定

- `CreateSessionAsync()` 必须在使用 `RunAsync` 前调用
- 同一 Session 的多次 `RunAsync` 调用共享历史
- 如需清空历史，创建新 Session 或调用 `ChatHistoryProvider` 的 `SetMessages()`
- `InMemoryChatHistoryProvider` 状态存储在 `AgentSession.StateBag`，非实例字段
- 消息顺序：系统消息 → ChatHistoryProvider 历史 → AIContextProvider 消息 → 当前用户消息
- 服务端管理历史的服务（如 Azure AI Foundry）会与本地 ChatHistoryProvider 冲突——见冲突检测规则
