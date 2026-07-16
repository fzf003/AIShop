<!-- lastSynced: 2026-06-24, source: core-abstractions.md -->

# 核心抽象

## AIAgent（抽象基类）

所有代理都派生自 `AIAgent`。关键成员：

```csharp
public abstract partial class AIAgent
{
    string Id { get; }                           // 自动生成的 GUID 或自定义值
    virtual string? Name { get; }                // 显示名称
    virtual string? Description { get; }         // 用途描述
    static AgentRunContext? CurrentRunContext { get; }  // AsyncLocal，跨 await 传递

    ValueTask<AgentSession> CreateSessionAsync(CancellationToken ct = default);
    Task<AgentResponse> RunAsync(string message, AgentSession? session = null, ...);
    Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, ...);
    IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(...);
}
```

**RunAsync 重载**（全部委托给 `RunCoreAsync`）：
1. `RunAsync()` — 无输入，使用已有上下文
2. `RunAsync(string message)` — 包装为 `ChatMessage(ChatRole.User, message)`
3. `RunAsync(ChatMessage message)` — 单条消息
4. `RunAsync(IEnumerable<ChatMessage> messages)` — 核心实现，设置 `CurrentRunContext`

## AIContext（上下文容器）

```csharp
public sealed class AIContext
{
    string? Instructions;              // 临时系统指令（临时性）
    IEnumerable<ChatMessage>? Messages; // 消息（可能成为历史记录的一部分）
    IEnumerable<AITool>? Tools;         // 临时工具（临时性）
}
```

- `Instructions` 和 `Tools` 是临时性的——仅对当前调用有效。
- `Messages` 会成为对话历史的一部分。

## AgentSession

```csharp
// 包装带状态的对话会话
AgentSession session = await agent.CreateSessionAsync();
session.StateBag;  // 供提供者存储每个会话状态的字典
```

## AgentResponse

```csharp
public sealed class AgentResponse(ChatResponse chatResponse)
{
    string? Text { get; }              // 聚合文本
    string? AgentId { get; set; }
    string? ContinuationToken { get; set; }
    ChatResponse ChatResponse { get; } // 原始底层响应
}
```

## ChatRole 和 ChatMessage

来自 `Microsoft.Extensions.AI`：

```csharp
new ChatMessage(ChatRole.System, "指令");
new ChatMessage(ChatRole.User, "你好");
new ChatMessage(ChatRole.Assistant, "你好！有什么可以帮助你的？");
new ChatMessage(ChatRole.Tool, "结果");  // 工具结果
```
