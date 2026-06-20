# SqliteChatHistoryProvider 设计文档

## 背景

当前 Agent 使用 `InMemoryChatHistoryProvider`，每次调用都新建 `ChatClientAgent` 和 `AgentSession`，状态不跨请求持久化。同时 ChatEndpoints 手动从 DB 加载**全部历史**拼入上下文，导致：
1. 每次调用上下文不断膨胀（所有历史都传给 LLM）
2. Agent 的会话管理能力未充分利用

## 目标

用 `SqliteChatHistoryProvider` 代替 `InMemoryChatHistoryProvider`，**自动管理 Agent 上下文窗口**：
- 每次只加载**最近 5 条消息**
- 当会话超过 **10 条**时，自动压缩（摘要）旧消息
- 由 Provider 接管历史管理，ChatEndpoints 不再手动加载

---

## 设计

### 涉及的文件

| 文件 | 操作 |
|------|------|
| `src/AIShop.Core/Entities/ChatEntities.cs` | `Session` 新增 `CompactedSummary` 字段 |
| `src/AIShop.Infrastructure/Data/AppDbContext.cs` | `Session` 映射新增 `CompactedSummary` |
| `src/AIShop.Infrastructure/Services/SqliteChatHistoryProvider.cs` | **新建** |
| `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | 改用 `SqliteChatHistoryProvider` |
| `src/AIShop.Api/Features/Chat/ChatEndpoints.cs` | 不再手动加载历史；传 session Id 给 Agent |

---

### 1. Session 实体 — 新增压缩摘要字段

```csharp
// ChatEntities.cs
public sealed class Session
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastActivityAt { get; set; }
    public string? CompactedSummary { get; set; }  // ← 新增
}
```

### 2. SqliteChatHistoryProvider — 核心实现

```csharp
namespace AIShop.Infrastructure.Services;

internal sealed class SqliteChatHistoryProvider(
    IDbContextFactory<AppDbContext> dbFactory,
    IChatClient chatClient,
    Guid sessionId) : IChatHistoryProvider
{
    private const int RecentCount = 5;
    private const int CompactionThreshold = 10;

    public async Task InvokingAsync(AIContext context, AgentSession session, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 1. 加载摘要（如果有）
        var sessionEntity = await db.Sessions.FindAsync([sessionId], ct);
        var summary = sessionEntity?.CompactedSummary;

        // 2. 加载最近 5 条消息
        var recentMessages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Take(RecentCount)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(ct);

        // 3. 组装历史消息
        var history = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(summary))
        {
            history.Add(new ChatMessage(ChatRole.System,
                $"[历史摘要] {summary}"));
        }
        foreach (var m in recentMessages)
        {
            history.Add(new ChatMessage(
                m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                m.Content));
        }

        context.Messages = history;
    }

    public async Task InvokedAsync(AIContext context, AgentSession session, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 检查是否需要压缩
        var totalCount = await db.ChatMessages
            .CountAsync(m => m.SessionId == sessionId, ct);

        if (totalCount <= CompactionThreshold)
            return;

        // 需要压缩：摘要旧消息，仅保留最近 5 条
        var oldMessages = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp)
            .Take(totalCount - RecentCount)
            .ToListAsync(ct);

        var summaryText = await SummarizeAsync(oldMessages, ct);

        // 更新 Session 的摘要字段
        var sessionEntity = await db.Sessions.FindAsync([sessionId], ct);
        if (sessionEntity is not null)
        {
            sessionEntity.CompactedSummary = summaryText;
        }

        // 删除旧消息
        db.ChatMessages.RemoveRange(oldMessages);
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> SummarizeAsync(
        List<Core.Entities.ChatMessage> messages, CancellationToken ct)
    {
        var chatMessages = messages.Select(m => new ChatMessage(
            m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            m.Content)).ToList();

        chatMessages.Add(new ChatMessage(ChatRole.User,
            "请用中文简要总结以上对话的核心内容。"));

        var response = await chatClient.GetResponseAsync(chatMessages, cancellationToken: ct);
        return response.Text ?? "";
    }
}
```

### 3. ShoppingAssistantAgent 改造

使用 `ChatOptions.Instructions` 设置 system prompt，其中 **KeywordMap 从 `ProductCatalog.KeywordMap` 动态生成**，不写死在代码里。

```csharp
public sealed class ShoppingAssistantAgent(
    IChatClient chatClient,
    IDbContextFactory<AppDbContext> dbFactory)
{
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
        {
            lines.Add($"{key} | {string.Join("、", tags)}");
        }

        lines.Add("");
        lines.Add("规则：");
        lines.Add("- 只能从\"关键词\"列选择");
        lines.Add("- 根据用户对话语义做判断（例如\"运动鞋\"→跑步、运动、鞋子）");
        lines.Add("- 无匹配返回空数组");

        return string.Join("\n", lines);
    }

    private static readonly string _instructions = BuildInstructions();

    public async Task<AgentChatResult> RunChatAsync(
        Guid sessionId, string userMessage, CancellationToken ct = default)
    {
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "ShoppingAssistant",
                Description = "智能购物助手",
                ChatOptions = new ChatOptions
                {
                    Instructions = _instructions
                },
                ChatHistoryProvider = new SqliteChatHistoryProvider(
                    dbFactory, chatClient, sessionId)
            },
            loggerFactory: null,
            services: null);

        var session = await agent.CreateSessionAsync(cancellationToken: ct);
        var response = await agent.RunAsync<AgentChatResult>(
            userMessage, session, options: null, cancellationToken: ct);
        return response.Result;
    }
}
```

**关键变化**：
- `_instructions` 从 `ProductCatalog.KeywordMap` 动态构建，KeywordMap 更新时 prompt 自动同步
- 放入 `ChatOptions.Instructions` 作为 System Message，不在用户消息中混入
- `RunChatAsync` 从传 `context` 改为传 `userMessage`（仅当前消息）
- `_instructions` 是 `static readonly`，只构建一次，但 `ProductCatalog.KeywordMap` 当前也是 `static readonly`，运行时不变（若有后续改为动态加载，可改成非静态）

### 4. ChatEndpoints 变更

ChatEndpoints **不再需要手动加载历史到 context 中**，也不再传 KeywordPrompt。Instructions 由 Agent 的 `ChatOptions.Instructions` 作为 System Message 注入。

```csharp
api.MapPost("/chat", async (
    ChatRequest req,
    IUserRepository users,
    ISessionRepository sessions,
    AppDbContext db,
    ShoppingAssistantAgent shoppingAgent,
    CancellationToken ct) =>
{
    // ... user lookup ...

    // 1. 保存用户消息
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "user", Content = req.Message });
    await db.SaveChangesAsync(ct);

    // 2. 调用 Agent（仅传当前消息 + sessionId）
    //    Instructions 和最近历史由 Agent/ChatHistoryProvider 自动注入
    AgentChatResult result;
    try
    {
        result = await shoppingAgent.RunChatAsync(sid, req.Message, ct);
    }
    catch
    {
        result = new AgentChatResult("抱歉...", []);
    }

    // 3. 白名单过滤 + 推荐（与现有逻辑相同）
    var validKeywords = (result.Keywords ?? [])
        .Where(k => ProductCatalog.KeywordMap.ContainsKey(k))
        .Distinct().Take(5).ToArray();

    // ...

    // 4. 保存助手回复
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "assistant", Content = result.Reply });
    await db.SaveChangesAsync(ct);
});
```

`/api/recommendations` 同理，简化为仅传分析专用 prompt。

---

## 数据流对比

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
  SqliteChatHistoryProvider.InvokingAsync():
    查 DB → 加载摘要 + 最近 5 条 → 注入到 context（User/Assistant 消息）
  LLM 收到的完整结构:
    (system) 你是购物助手... + KeywordMap     ← Instructions
    (system) [历史摘要] ...                  ← Provider 注入
    (user) 第 N-4 条消息                     ← Provider 注入（最近5条）
    (assistant) 回复
    ...
    (user) 当前消息                          ← RunChatAsync 传入
  SqliteChatHistoryProvider.InvokedAsync():
    检查总数 > 10 → 是 → 摘要旧消息 + 删除旧消息
```

---

## 压缩行为

| 阶段 | 动作 |
|------|------|
| 1-10 条消息 | 正常，不做任何压缩 |
| 第 11 条保存后 | 摘要最早的 5 条，存到 `Session.CompactedSummary`，删除这 5 条 |
| 后续每超 10 条 | 再次压缩最早的非摘要部分 |

每次 Agent 调用时，LLM 看到的：

```
[历史摘要] 用户之前聊了跑鞋和户外...  ← 压缩摘要
(user) 最近一条消息                      ← 最近 5 条
(assistant) 回复
(user) 当前消息
(Assistant) 你是购物助手... + KeywordMap  ← 指令
```

---

## 边界情况

| 场景 | 处理 |
|------|------|
| 第一次调用，无历史 | `InvokingAsync` 加载空列表，不注入摘要 |
| 压缩摘要时 LLM 调用失败 | `SummarizeAsync` catch → 存储空摘要，不删除旧消息 |
| 并发调用导致重复压缩 | 每次 `InvokedAsync` 重新 `CountAsync`，幂等 |
| 历史只有 6 条 | < 10 条，不触发压缩 |
| 压缩后只剩 5 条 + 摘要 | 下次调用只有 5 条 + 摘要，等累积到 10 条再压 |

---

## 依赖注入

`SqliteChatHistoryProvider` 不注册到 DI（在 Agent 内部创建），但需要：

```csharp
// Program.cs — 注册 IDbContextFactory（如果尚未注册）
builder.Services.AddDbContextFactory<AppDbContext>(...);
```

同时 `ShoppingAssistantAgent` 需要新增 `IDbContextFactory<AppDbContext>` 注入：

```csharp
// Program.cs
builder.Services.AddScoped<ShoppingAssistantAgent>();
```

已在作用域内，`IDbContextFactory` 是单例，可安全注入。

---

## 验收标准

1. 登录后发 3-4 条消息 → `/api/chat` 正常工作，推荐逻辑正常
2. 发 10+ 条消息后 → 检查 DB `ChatMessages` 少于 10 条（旧消息已被压缩删除）
3. `Session.CompactedSummary` 有值（摘要已存储）
4. 发 15+ 条消息后 → 第二次压缩触发，摘要字段更新
