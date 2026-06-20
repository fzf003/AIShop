# 推荐产品 Agent 设计文档（终稿）

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
用户消息 → 存 DB → 加载完整历史 → 拼接上下文（含 KeywordMap 指令）

一次 RunAsync<AgentChatResult>(context) → { Reply, Keywords }

→ 白名单过滤 keywords → MatchProducts()
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
  → 单次 RunAsync<AgentChatResult>() → { Reply, Keywords }
  → 后端白名单过滤 → MatchProducts() → 推荐/其他分层
  → 前端双列表

PreferenceAnalyzerAgent → 删除
IRecommendationService → 删除
```

---

## 详细设计

### 1. Agent 输出 DTO

```csharp
// Features/Chat/ChatEndpoints.cs 中定义
public sealed record AgentChatResult(
    string Reply,
    string[] Keywords
);
```

### 2. ShoppingAssistantAgent 改造

代码风格与现有代码一致：原地构造，不抽静态字段，单次调用。

```csharp
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AIShop.Api.Agents;

public sealed class ShoppingAssistantAgent(IChatClient chatClient)
{
    public async Task<AgentChatResult> RunChatAsync(string context, CancellationToken ct = default)
    {
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "ShoppingAssistant",
                Description = "智能购物助手",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(
                    new InMemoryChatHistoryProviderOptions())
            },
            loggerFactory: null,
            services: null);

        var session = await agent.CreateSessionAsync(cancellationToken: ct);
        var response = await agent.RunAsync<AgentChatResult>(
            context, session, options: null, cancellationToken: ct);
        return response.Result;
    }
}
```

**关键变化**：
- 返回类型从 `Task<string>` 改为 `Task<AgentChatResult>`
- 单个 `RunAsync<T>()` 调用，非两阶段
- `context` 中已包含完整历史 + instructions + KeywordMap 指令

**错误处理**：调用方 `ChatEndpoints` 负责 try/catch，失败时回退到空 `AgentChatResult`。

### 3. 上下文拼接

```csharp
// ChatEndpoints — 构建上下文时嵌入 KeywordMap 指令
var contextParts = new List<string>();
foreach (var m in history)
    contextParts.Add($"({m.Role}) {m.Content}");
contextParts.Add("""
    你是智能购物助手。用中文回复，简洁友好，帮助用户找到心仪的商品。

    【商品关键词参考】
    当用户表达购物需求时，从下表选择 0-5 个最匹配的关键词：

    关键词 | 覆盖的商品标签
    夹克 | 皮衣、外套、夹克、服装
    鞋子 | 鞋子、跑鞋、靴子、鞋类
    跑步 | 跑步、运动、健身、体育、跑鞋
    ...
    数码 | 数码、科技、电子、无线、充电、穿戴

    规则：
    - 只能从"关键词"列选择
    - 语义理解（"运动鞋"→跑步、运动、鞋子）
    - 无匹配返回空数组
    """);
var context = string.Join("\n", contextParts);
```

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
    if (user is null) return Results.Unauthorized();

    var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
    var sid = Guid.Parse(sessionId);

    // 1. 保存用户消息
    db.ChatMessages.Add(new ChatMessage
    {
        SessionId = sid, Role = "user", Content = req.Message
    });
    await db.SaveChangesAsync(ct);

    // 2. 加载完整历史
    var history = await db.ChatMessages
        .Where(m => m.SessionId == sid)
        .OrderBy(m => m.Timestamp)
        .ToListAsync(ct);

    // 3. 构建上下文（含 KeywordMap 指令）
    var contextParts = new List<string>();
    foreach (var m in history)
        contextParts.Add($"({m.Role}) {m.Content}");
    contextParts.Add("""...KeywordMap 指令...""");
    var context = string.Join("\n", contextParts);

    // 4. 单次结构化调用
    AgentChatResult result;
    try
    {
        result = await shoppingAgent.RunChatAsync(context, ct);
    }
    catch
    {
        result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", []);
    }

    // 5. 白名单过滤 keywords
    var validKeywords = (result.Keywords ?? [])
        .Where(k => ProductCatalog.KeywordMap.ContainsKey(k))
        .Distinct()
        .Take(5)
        .ToArray();

    // 6. 构建推荐响应
    var allProducts = ProductCatalog.All;
    ChatReply chatReply;

    if (validKeywords.Length > 0)
    {
        var matched = ProductCatalog.MatchProducts(validKeywords);
        var matchedIds = new HashSet<int>(matched.Select(p => p.Id));

        var recDtos = matched.Select(ToDto).ToList();
        var otherDtos = allProducts
            .Where(p => !matchedIds.Contains(p.Id))
            .Select(ToDto)
            .ToList();

        chatReply = new ChatReply(result.Reply, recDtos, otherDtos,
            "根据您的兴趣，为您推荐：", HasRecommendation: true);
    }
    else
    {
        var fallback = allProducts.Take(6).Select(ToDto).ToList();
        chatReply = new ChatReply(result.Reply,
            RecommendedProducts: null, OtherProducts: fallback,
            "暂无特定推荐 — 浏览精选商品", HasRecommendation: false);
    }

    // 7. 保存助手回复
    db.ChatMessages.Add(new ChatMessage
    {
        SessionId = sid, Role = "assistant", Content = result.Reply
    });
    await db.SaveChangesAsync(ct);

    return Results.Ok(chatReply);
});
```

#### POST /api/recommendations（改写）

原 `PreferenceAnalyzerAgent` 删除后，改为直接用 `ShoppingAssistantAgent` 做偏好分析：

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

    var history = await db.ChatMessages
        .Where(m => m.SessionId == Guid.Parse(sessionId))
        .OrderBy(m => m.Timestamp)
        .ToListAsync(ct);

    // 构建分析专用上下文
    var contextParts = new List<string>();
    foreach (var m in history)
        contextParts.Add($"({m.Role}) {m.Content}");
    contextParts.Add("""
        分析上述对话中用户的购物偏好。
        从关键词表中选择 2-6 个最匹配的关键词，只输出 JSON 数组。

        关键词：夹克、鞋子、跑步、音乐、咖啡、健身、瑜伽、烹饪、科技
                阅读、户外、时尚、环保、巧克力、家居、送礼、爱好
                耳机、手表、运动、音频、数码
        """);
    var context = string.Join("\n", contextParts);

    // 只用结构化输出取 keywords（reply 丢弃）
    var result = await shoppingAgent.RunChatAsync(context, ct);
    var preferences = result.Keywords ?? [];

    // 与原有 match 逻辑保持一致
    var matched = ProductCatalog.MatchProducts(preferences);
    var dtos = matched.Select(ToDto).ToList();

    return Results.Ok(new RecommendationResponse(
        dtos.FirstOrDefault(),
        dtos.Skip(1).ToList(),
        $"根据您的偏好{string.Join("、", preferences)}，为您推荐："));
});
```

### 5. ChatReply / RecommendationResponse DTO

```csharp
public sealed record ChatReply(
    string Response,
    List<ProductDto>? RecommendedProducts,
    List<ProductDto>? OtherProducts,
    string? RecMessage,
    bool HasRecommendation
);

// RecommendationResponse 保持不变
public sealed record RecommendationResponse(
    ProductDto? BestMatch,
    List<ProductDto> Other,
    string Message
);
```

### 6. 前端变更

```javascript
function renderRecommendationPanel(data) {
    const msgEl = document.getElementById('recoMessage');
    const container = document.getElementById('recoProducts');
    container.innerHTML = '';

    if (data.hasRecommendation) {
        msgEl.textContent = data.recMessage || '根据您的兴趣推荐：';
        container.innerHTML += '<div class="section-label best">🎯 为您推荐</div>';
        (data.recommendedProducts || []).forEach(p =>
            container.innerHTML += productCard(p, true));

        if (data.otherProducts?.length > 0) {
            container.innerHTML += '<div class="section-label other">📋 其他商品</div>';
            data.otherProducts.forEach(p =>
                container.innerHTML += productCard(p, false));
        }
    } else {
        msgEl.textContent = '😅 ' + (data.recMessage || '暂无特定推荐');
        container.innerHTML += '<div class="section-label other" style="color:#999;">📋 全部商品</div>';
        (data.otherProducts || []).forEach(p => {
            container.innerHTML += `
                <div class="product-card" style="opacity:0.7;">
                    <div style="font-size:0.7rem;color:#999;margin-bottom:4px;">未推荐</div>
                    <div class="product-icon">${p.emoji}</div>
                    <div class="product-name">${p.name}</div>
                    <div class="product-tags">${(p.tags || []).join(' · ')}</div>
                    <div class="product-price">$${p.price.toFixed(2)}</div>
                </div>
            `;
        });
    }
}
```

在 `sendMessage()` 中：

```javascript
// 替换原有的 data.recommendations 处理
renderRecommendationPanel(data);
```

原 `renderRecommendations()` 函数保留，用于 `getRecommendations()` 按钮的 `RecommendationResponse` 渲染。

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
| `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | `RunChatAsync` 返回 `AgentChatResult`，单次 `RunAsync<T>()` |
| `src/AIShop.Api/Agents/PreferenceAnalyzerAgent.cs` | **删除** |
| `src/AIShop.Api/Features/Chat/ChatEndpoints.cs` | `ChatReply` 双列表；`/api/chat` 推荐逻辑重写；`/api/recommendations` 改调用 ShoppingAssistantAgent；新增 `AgentChatResult` DTO |
| `src/AIShop.Api/Program.cs` | 删除 `AddScoped<PreferenceAnalyzerAgent>()` |
| `src/AIShop.Api/wwwroot/index.html` | 推荐面板双列表渲染；renderRecommendations 保留 |
| `src/AIShop.Core/Interfaces/IRepositories.cs` | 删除 `IPreferenceAnalyzer`、`IRecommendationService` |
| `src/AIShop.Infrastructure/Services/RecommendationService.cs` | **删除** |
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
| 全部产品都匹配 | 推荐取前 6，"其他"为空 |
| LLM 回了一个不在 KeywordMap 里的词 | 白名单过滤掉 |

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
