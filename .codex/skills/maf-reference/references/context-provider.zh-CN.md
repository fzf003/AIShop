# AIContextProvider — 中间件模式

AIContextProvider 是框架的核心扩展机制，相当于中间件。

## 两阶段生命周期

```
LLM 调用前：  InvokingAsync(InvokingContext) → 返回 AIContext { Instructions, Messages, Tools }
              ↓
          LLM 实际调用
              ↓
LLM 调用后：  InvokedAsync(InvokedContext) → 处理结果
```

多个提供者链式执行：每个提供者接收上一个提供者的输出，并追加自己的内容。

## 自定义提供者模板

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class RagProvider(ISearchService search) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var query = context.AIContext.Messages?.LastOrDefault()?.Text ?? "";
        var docs = search.Search(query, topK: 5);

        var ragMessages = docs.Select(d =>
            new ChatMessage(ChatRole.System, $"参考知识：{d.Content}"));

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = ragMessages
        });
    }
}

// 后调用处理：提取偏好、记录日志等
public class PreferenceExtractor : AIContextProvider
{
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct)
    {
        // 存储在 AgentSession.StateBag 中，而非实例字段
        var state = context.Session?.StateBag;
        return default;
    }
}
```

## 消息过滤（默认行为）

| 过滤条件 | 默认值 | 用途 |
|---|---|---|
| `ProvideInputMessageFilter` | 仅外部消息 | 哪些消息传递给 `ProvideAIContextAsync` |
| `StoreInputRequestMessageFilter` | 仅外部消息 | 哪些请求消息传递给 `StoreAIContextAsync` |
| `StoreInputResponseMessageFilter` | 全部 | 哪些响应消息传递给 `StoreAIContextAsync` |

## 状态存储

- **切勿在提供者实例字段中存储会话状态**——一个提供者实例服务于多个会话。
- 使用 `AgentSession.StateBag`，通过 `context.Session?.StateBag` 访问。
- `StateKeys`（默认值：`[typeof(YourProvider).Name]`）必须在所有提供者中保持唯一。

## 注册提供者

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    AIContextProviders = new AIContextProvider[]
    {
        new RoleContextProvider("你是专业购物顾问"),
        new RagProvider(searchService),
        new PreferenceExtractor()
    }
});
```
