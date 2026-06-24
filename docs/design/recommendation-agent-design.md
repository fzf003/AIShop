# 推荐产品 Agent 设计文档（终稿）

> 最后更新：2026-06-23 | 状态：与代码同步

## 概述

让 LLM 根据聊天上下文从 KeywordMap 中选择推荐关键词，后端用 `MatchProducts()` 做确定性产品匹配。**LLM 负责语义理解**（"运动鞋" → "跑步"），**KeywordMap 负责产品匹配**（"跑步" → 跑鞋/瑜伽垫/...）。

输出格式使用 MAF 的 `RunAsync<T>()` 结构化输出，LLM 输出天然为合法 JSON，零解析风险。

与此同时也一并清理不再需要的 `PreferenceAnalyzerAgent` 及相关接口。

---

## 现状问题

**推荐逻辑问题**（`ChatEndpoints.cs:91-103`）：

```csharp
var allText = req.Message + " " + reply;
var foundKeywords = ProductCatalog.KeywordMap.Keys
    .Where(k => allText.Contains(k)).Distinct().ToArray();
var matched = foundKeywords.Length > 0
    ? ProductCatalog.MatchProducts(foundKeywords) : [];
if (matched.Length == 0) matched = ProductCatalog.All.Take(6).ToArray();
```

1. **字符串子串匹配** — Agent 回复"运动鞋"，KeywordMap key 是"跑步"，`"运动鞋".Contains("跑步")` = false，漏配
2. **无推荐标识** — 无匹配时取前 6 个，全部同等展示
3. **单列表** — 推荐/未推荐未分离

**多余依赖问题**：
- `PreferenceAnalyzerAgent` 已被 `ShoppingAssistantAgent`（改造后）的能力覆盖
- `IRecommendationService` / `RecommendationService` 无调用方，可以清理

---

## 数据流

```
用户消息
  → 存 DB（user 消息）
  → ShoppingAssistantAgent.RunChatAsync(sessionId, userMessage)
      → _agent 已预配置 Instructions（含 KeywordMap）
      → SqliteChatHistoryProvider 自动从 DB 加载最近 20 条历史
      → RunAsync<AgentChatResult>(userMessage, session) → { Reply, Keywords }
  → 存 DB（assistant 消息）
  → 白名单过滤 keywords → SplitProducts() / MatchProducts()
    → 有推荐: 推荐排前 + 其他排后
    → 无推荐: 默认列表 + 标记"暂无推荐"
  → 前端双列表渲染
```

---

## 架构变更

### Before

```
ShoppingAssistantAgent.RunChatAsync()
  → 纯文本 reply (string)
  → 后端 KeywordMap.Keys 子串匹配 → 漏配 → 无差异展示

PreferenceAnalyzerAgent — 独立 Agent，负责偏好分析
IRecommendationService — 无调用方
```

### After

```
ShoppingAssistantAgent.RunChatAsync()
  → _agent（构造函数创建，实例级复用）
  → Instructions 从 ProductCatalog.KeywordMap 动态构建
  → SqliteChatHistoryProvider 自动管理历史（最近 20 条）
  → RunAsync<AgentChatResult>() → { Reply, Keywords }
  → 后端白名单过滤 → SplitProducts() → 推荐/其他分层
  → 前端双列表

PreferenceAnalyzerAgent → 删除
IRecommendationService → 删除
```

---

## 详细设计

### 1. Agent 输出 DTO

```csharp
// Features/Chat/ChatEndpoints.cs 中定义
public sealed record AgentChatResult(string Reply, string[] Keywords);
```

### 2. ShoppingAssistantAgent 实现

**关键设计决策：**
- `_agent` 在构造函数中通过 `AsAIAgent()` 创建一次，所有请求复用（非每请求新建）
- `_instructions` 从 `ProductCatalog.KeywordMap` 动态生成，KeywordMap 更新时自动同步
- Instructions 通过 `ChatOptions.Instructions` 作为 System Message 注入
- `SqliteChatHistoryProvider` 接管历史加载，ChatEndpoints 不再手动拼接历史

```csharp
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using AIShop.Api.Features.Chat;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;

namespace AIShop.Api.Agents;

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

**关键点：**
- `_agent` = 实例字段，构造一次，所有请求复用
- `_instructions` = static readonly，KeywordMap 只在构造函数中读取一次（当前 ProductCatalog 也是 static readonly，运行时不变）
- `RunChatAsync` 只需 `sessionId` + `userMessage`，不传历史（Provider 自动加载）
- `ChatResponseFormatJson.ForJsonSchema<AgentChatResult>()` 确保 LLM 输出合法 JSON

**错误处理**：调用方 `ChatEndpoints` 负责 try/catch，失败时回退到空 `AgentChatResult`。

### 3. SqliteChatHistoryProvider

详情见 `sqlite-chat-history-provider-design.md`。核心行为：
- `ProvideChatHistoryAsync`：从 DB 加载最近 20 条消息，不压缩
- `StoreChatHistoryAsync`：空操作（消息已在 ChatEndpoints 中持久化）
- 通过 `session.StateBag["SessionId"]` 获取会话标识

### 4. ChatEndpoint 变更

#### POST /api/chat

```csharp
api.MapPost("/chat", async (
    ChatRequest req,
    IUserRepository users,
    ISessionRepository sessions,
    AppDbContext db,
    ShoppingAssistantAgent shoppingAgent,
    CancellationToken ct) =>
{
    var user = await users.GetByUsernameAsync(req.Username, ct);
    if (user is null)
        return Results.Unauthorized();

    var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
    var sid = Guid.Parse(sessionId);

    // 1. 获取 Agent 结构化响应（历史由 SqliteChatHistoryProvider 自动加载）
    AgentChatResult result;
    try
    {
        result = await shoppingAgent.RunChatAsync(sid, req.Message, ct);
    }
    catch
    {
        result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", []);
    }

    // 2. 保存用户消息 + 助手回复到 SQLite
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "user", Content = req.Message });
    db.ChatMessages.Add(new ChatMessage { SessionId = sid, Role = "assistant", Content = result.Reply });
    await db.SaveChangesAsync(ct);

    // 3. 白名单过滤 keywords
    var validKeywords = (result.Keywords ?? [])
        .Where(k => ProductCatalog.KeywordMap.ContainsKey(k))
        .Distinct()
        .Take(5)
        .ToArray();

    // 4. 构建推荐响应（使用 SplitProducts 实现推荐/其他分层）
    ChatReply chatReply;

    if (validKeywords.Length > 0)
    {
        var (recommended, others) = ProductCatalog.SplitProducts(validKeywords);
        var recDtos = recommended.Select(ToDto).ToList();
        var otherDtos = recommended.Length == 0
            ? ProductCatalog.All.Take(6).Select(ToDto).ToList()
            : others.Take(12).Select(ToDto).ToList();

        chatReply = new ChatReply(result.Reply, recDtos, otherDtos,
            "根据您的兴趣，为您推荐：", HasRecommendation: true,
            recDtos.Select(p => p.Category).Distinct().ToArray());
    }
    else
    {
        var fallback = ProductCatalog.All.Take(6).Select(ToDto).ToList();
        chatReply = new ChatReply(result.Reply,
            RecommendedProducts: null,
            OtherProducts: fallback,
            "暂无特定推荐 — 浏览精选商品",
            HasRecommendation: false,
            MatchedCategories: null);
    }

    return Results.Ok(chatReply);
});
```

#### POST /api/recommendations

"给我推荐商品"按钮：读取最近用户消息 → 交给 Agent 提取关键词 → MatchProducts 匹配。

```csharp
api.MapPost("/recommendations", async (
    RecommendationRequest req,
    IUserRepository users,
    ISessionRepository sessions,
    AppDbContext db,
    ShoppingAssistantAgent shoppingAgent,
    CancellationToken ct) =>
{
    var user = await users.GetByUsernameAsync(req.Username, ct);
    if (user is null) return Results.Unauthorized();

    var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
    var sid = Guid.Parse(sessionId);

    var lastUserMessage = await db.ChatMessages
        .Where(m => m.SessionId == sid && m.Role == "user")
        .OrderByDescending(m => m.Timestamp)
        .Select(m => m.Content)
        .FirstOrDefaultAsync(ct);

    if (lastUserMessage is null)
        return Results.Ok(new RecommendationResponse(null, [], "暂无对话历史，请先聊天。", null));

    var result = await shoppingAgent.RunChatAsync(sid, lastUserMessage, ct);
    var preferences = result.Keywords ?? [];

    var matched = ProductCatalog.MatchProducts(preferences);
    var dtos = matched.Select(ToDto).ToList();

    var matchedCategories = dtos.Select(p => p.Category).Distinct().ToArray();

    return Results.Ok(new RecommendationResponse(
        dtos.FirstOrDefault(),
        dtos.Skip(1).ToList(),
        $"根据您的偏好{string.Join("、", preferences)}，为您推荐：",
        matchedCategories));
});
```

### 5. ChatReply / RecommendationResponse DTO

```csharp
public sealed record ChatReply(
    string Response,
    List<ProductDto>? RecommendedProducts,
    List<ProductDto>? OtherProducts,
    string? RecMessage,
    bool HasRecommendation,
    string[]? MatchedCategories
);

public sealed record RecommendationResponse(
    ProductDto? BestMatch,
    List<ProductDto> Other,
    string Message,
    string[]? MatchedCategories
);
```

### 6. Program.cs — DI 注册

```csharp
// 注册 ShoppingAssistantAgent（Scoped，与请求生命周期一致）
builder.Services.AddScoped<ShoppingAssistantAgent>();

// 注册 MCP Product Client（通过 Aspire 服务发现 http://mcp）
builder.Services.AddHttpClient<McpProductClient>(client =>
{
    client.BaseAddress = new Uri("http://mcp");
});
```

---

## 清理清单

### 删除

| 删除 | 原因 |
|------|------|
| `src/AIShop.Api/Agents/PreferenceAnalyzerAgent.cs` | 被 ShoppingAssistantAgent 覆盖 |
| `src/AIShop.Core/Interfaces/IRepositories.cs` 中的 `IPreferenceAnalyzer` 和 `IRecommendationService` | 无调用方 |
| `src/AIShop.Infrastructure/Services/RecommendationService.cs` | 无调用方 |
| `src/AIShop.Infrastructure/DependencyInjection.cs` 中 `IRecommendationService` 的注册 | 无调用方 |

### 不需要删除

| 保持 | 原因 |
|------|------|
| `AGENTS.md` | 用户要求保留 |
| `IProductRepository` | `/api/products` 和 Chat 端点仍使用 |
| `IUserRepository` / `ISessionRepository` | Chat 端点仍使用 |

---

## 涉及文件清单

| 文件 | 变更 |
|------|------|
| `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | `RunChatAsync` 返回 `AgentChatResult`；`_agent` 实例复用；Instructions 动态构建 |
| `src/AIShop.Api/Agents/PreferenceAnalyzerAgent.cs` | **删除** |
| `src/AIShop.Api/Features/Chat/ChatEndpoints.cs` | `ChatReply` 双列表；`/api/chat` 推荐逻辑重写；`/api/recommendations` 改调用 ShoppingAssistantAgent；新增 `AgentChatResult` DTO |
| `src/AIShop.Api/Program.cs` | 删除 `AddScoped<PreferenceAnalyzerAgent>()` |
| `src/AIShop.Api/wwwroot/index.html` | 推荐面板双列表渲染；renderRecommendations 保留 |
| `src/AIShop.Core/Interfaces/IRepositories.cs` | 删除 `IPreferenceAnalyzer`、`IRecommendationService` |
| `src/AIShop.Infrastructure/Services/RecommendationService.cs` | **删除** |
| `src/AIShop.Infrastructure/Services/SqliteChatHistoryProvider.cs` | **新增** — 从 DB 加载最近 20 条历史 |
| `src/AIShop.Infrastructure/DependencyInjection.cs` | 删除 `IRecommendationService` 注册 |
| `tests/AIShop.Api.Tests/` | 新增 Chat 端点集成测试 |

---

## 边界情况

| 场景 | 处理 |
|------|------|
| `RunAsync<T>()` 抛出异常 | try/catch → `new AgentChatResult("抱歉...", [])` |
| Keywords 为 null | `?? []` |
| Keywords 不在 KeywordMap 中 | 白名单 `Where(k => KeywordMap.ContainsKey(k))` 丢弃 |
| 所有关键词都被过滤 | `validKeywords` 为空 → 默认列表 + 标记 |
| 同一关键词多次出现 | `Distinct()` 去重 |
| LLM 选了超过 5 个关键词 | `Take(5)` 兜底 |
| 全部产品都匹配 | 推荐取全部，"其他"为空 |
| LLM 回了一个不在 KeywordMap 里的词 | 白名单过滤掉 |
| MCP 调用失败 | 静默降级，返回空数组，不影响主流程 |

---

## 匹配场景对照

| 用户说 | LLM 理解 → 选的关键词 | 推荐产品 |
|--------|---------------------|---------|
| "买**跑鞋**" | ["跑步","鞋子"] | 跑鞋、瑜伽垫、运动手表 |
| "**运动鞋**" | ["跑步","运动","鞋子"] | 同上（语义理解）|
| "**听歌**" | ["音乐","音频"] | 耳机、唱片机 |
| "想**看书**" | ["阅读"] | 悬疑小说 |
| "**天冷**穿什么" | ["夹克"] | 经典皮夹克 |
| "今天天气怎么样" | [] | 默认 6 个 + 灰标 |

---

## 参考

- [Producing Structured Outputs with agents (Microsoft Learn)](https://learn.microsoft.com/en-gb/agent-framework/agents/structured-outputs?pivots=programming-language-csharp)
- `RunAsync<T>()` — `AIAgent` 基类泛型方法，返回 `AgentResponse<T>`，`response.Result` 为已反序列化类型实例
- MAF 参考文件：`.claude/skills/maf-reference/references/`
