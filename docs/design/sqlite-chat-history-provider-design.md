# SqliteChatHistoryProvider 设计文档

> 最后更新：2026-06-23 | 状态：与代码同步

## 背景

当前 Agent 使用 `InMemoryChatHistoryProvider`，每次调用都新建 `ChatClientAgent` 和 `AgentSession`，状态不跨请求持久化。同时 ChatEndpoints 手动从 DB 加载**全部历史**拼入上下文，导致：
1. 每次调用上下文不断膨胀（所有历史都传给 LLM）
2. Agent 的会话管理能力未充分利用

## 目标

用 `SqliteChatHistoryProvider` 代替 `InMemoryChatHistoryProvider`，**自动管理 Agent 上下文窗口**：
- 每次自动从 DB 加载最近 20 条消息
- 由 Provider 接管历史加载，ChatEndpoints 不再手动拼接历史

## 设计

### 涉及的文件

| 文件 | 操作 |
|------|------|
| `src/AIShop.Infrastructure/Services/SqliteChatHistoryProvider.cs` | **新建** |
| `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | 改用 `SqliteChatHistoryProvider` |

### 1. SqliteChatHistoryProvider — 核心实现

```csharp
namespace AIShop.Infrastructure.Services;

public class SqliteChatHistoryProvider(
    IDbContextFactory<AppDbContext> dbFactory) : ChatHistoryProvider()
{
    private const int MaxMessages = 20;

    protected override async ValueTask<IEnumerable<AgentChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = GetSessionId(context.Session!);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var messages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Take(MaxMessages)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(cancellationToken);

        return messages.Select(m => new AgentChatMessage(
            m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            m.Content));
    }

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        // Messages are persisted in ChatEndpoints and kept in SQLite forever.
        // ProvideChatHistoryAsync loads only the latest N for the LLM.
        return default;
    }

    private static Guid GetSessionId(AgentSession session)
    {
        if (session.StateBag.TryGetValue<string>("SessionId", out var id, null) && id is not null)
            return Guid.Parse(id);
        throw new InvalidOperationException("SessionId not found in session StateBag.");
    }
}
```

**关键设计决策：**
- `MaxMessages = 20` — 每次只加载最近 20 条，避免上下文膨胀
- **不做压缩** — 不实现摘要/压缩功能，保持简单
- **不删除消息** — `StoreChatHistoryAsync` 为空操作，消息持久化由 `ChatEndpoints` 负责
- 历史按时间降序取最新 N 条，再升序排列交给 LLM

### 2. ShoppingAssistantAgent 改造

使用 `ChatOptions.Instructions` 设置 system prompt，其中 **KeywordMap 从 `ProductCatalog.KeywordMap` 动态生成**。

```csharp
public sealed class ShoppingAssistantAgent
{
    private readonly AIAgent _agent;

    private static readonly string _instructions = BuildInstructions();

    private static string BuildInstructions()
    {
        var lines = new List<string>
        {
            "你是智能购物助手。用中文回复，简洁友好，帮助用户找到心仪的商品。",
            "",
            "【商品关键词参考】",
            "当用户表达购物需求时，从下表选择 0-5 个最匹配的关键词：",
            "",
            "关键词 | 覆盖的商品标签"
        };

        foreach (var (key, tags) in ProductCatalog.KeywordMap)
            lines.Add($"{key} | {string.Join("、", tags)}");

        lines.Add("");
        lines.Add("规则：");
        lines.Add("- 只能从\"关键词\"列选择");
        lines.Add("- 根据用户对话语义做判断（例如\"运动鞋\"→跑步、运动、鞋子）");
        lines.Add("- 无匹配返回空数组");

        return string.Join("\n", lines);
    }

    public ShoppingAssistantAgent(IChatClient chatClient, IDbContextFactory<AppDbContext> dbFactory)
    {
        _agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "ShoppingAssistant",
            Description = "智能购物助手",
            ChatOptions = new ChatOptions
            {
                Instructions = _instructions
            },
            ChatHistoryProvider = new SqliteChatHistoryProvider(dbFactory),
            ThrowOnChatHistoryProviderConflict = false
        });
    }

    public async Task<AgentChatResult> RunChatAsync(
        Guid sessionId, string userMessage, CancellationToken ct = default)
    {
        var session = await _agent.CreateSessionAsync(ct);
        session.StateBag.SetValue("SessionId", sessionId.ToString());

        var response = await _agent.RunAsync<AgentChatResult>(
            userMessage, session,
            options: new AgentRunOptions
            {
                ResponseFormat = ChatResponseFormatJson.ForJsonSchema<AgentChatResult>()
            },
            cancellationToken: ct);
        return response.Result;
    }
}
```

**关键变化：**
- `_agent` 在构造函数中通过 `AsAIAgent()` 创建，实例级复用
- `_instructions` 从 `ProductCatalog.KeywordMap` 动态构建
- Instructions 通过 `ChatOptions.Instructions` 作为 System Message 注入
- `SqliteChatHistoryProvider` 在 Agent 构造时注入 `dbFactory`，自动加载历史
- `RunChatAsync` 从传 `context` 改为传 `userMessage`（仅当前消息）

### 3. ChatEndpoints 变更

ChatEndpoints **不再需要手动加载历史到 context 中**。Instructions 由 Agent 的 `ChatOptions.Instructions` 作为 System Message 注入。

```csharp
api.MapPost("/chat", async (..., ShoppingAssistantAgent shoppingAgent, ...) =>
{
    // 1. 获取 Agent 结构化响应（历史由 SqliteChatHistoryProvider 自动加载）
    AgentChatResult result;
    try
    {
        result = await shoppingAgent.RunChatAsync(sid, req.Message, ct);
    }
    catch
    {
        result = new AgentChatResult("抱歉...", []);
    }

    // 2. 保存用户消息 + 助手回复
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "user", Content = req.Message });
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "assistant", Content = result.Reply });
    await db.SaveChangesAsync(ct);

    // 3. 白名单过滤 + 推荐（与现有逻辑相同）
    // ...
});
```

---

## 数据流

### Before

```
ChatEndpoints:
  加载 ALL 历史 → 拼接成 context string → 传给 Agent
Agent:
  InMemoryChatHistoryProvider（未使用）
  RunAsync(context) → LLM 拿到全部历史
```

### After

```
ChatEndpoints:
  存用户消息到 DB
  传 "当前消息" + sessionId → Agent

Agent 构造时:
  Instructions = "你是购物助手... + KeywordMap"  ← System Message

Agent 运行时:
  SqliteChatHistoryProvider.ProvideChatHistoryAsync():
    查 DB → 加载最近 20 条 → 注入到 context（User/Assistant 消息）
  LLM 收到的完整结构:
    (system) 你是购物助手... + KeywordMap     ← Instructions
    (user) 第 N-19 条消息                     ← Provider 注入（最近20条）
    (assistant) 回复
    ...
    (user) 当前消息                           ← RunChatAsync 传入
  SqliteChatHistoryProvider.StoreChatHistoryAsync():
    空操作 — 消息已在 ChatEndpoints 中持久化
```

---

## 边界情况

| 场景 | 处理 |
|------|------|
| 第一次调用，无历史 | `ProvideChatHistoryAsync` 返回空列表 |
| 历史超过 20 条 | 取最近 20 条，旧消息保留在 DB 但不再传给 LLM |
| StateBag 中缺少 SessionId | 抛出 `InvalidOperationException` |
| 并发调用 | `IDbContextFactory` 每次创建新 DbContext，天然隔离 |

---

## 验收标准

1. 登录后发 3-4 条消息 → `/api/chat` 正常工作，推荐逻辑正常
2. 发 20+ 条消息后 → LLM 只看到最近 20 条，旧消息保留在 DB
3. 消息不丢失 → DB 中消息数 = 发送总数
