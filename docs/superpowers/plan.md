# 推荐产品 Agent 开发计划

## 背景

当前聊天推荐采用 `KeywordMap.Keys` 子串匹配，LLM 不参与推荐决策。Agent 回复"运动鞋"时匹配不到 KeywordMap key"跑步"，导致漏配。`PreferenceAnalyzerAgent` 的功能已被改造后的 `ShoppingAssistantAgent` 覆盖，一并清理。

## 目标

改造 ShoppingAssistantAgent 输出结构化结果 `{ Reply, Keywords }`，后端据此做推荐/未推荐分层展示。同时清理不再需要的依赖。

---

## 任务拆分

### Task 1：Core — 清理接口定义

**文件：** `src/AIShop.Core/Interfaces/IRepositories.cs`

**操作：**
- 删除 `IPreferenceAnalyzer` 接口
- 删除 `IRecommendationService` 接口

**验收标准：** 接口文件中仅保留 `IUserRepository`、`ISessionRepository`、`IProductRepository`

---

### Task 2：Infrastructure — 清理实现和注册

**文件：**
- `src/AIShop.Infrastructure/Services/RecommendationService.cs` — 删除
- `src/AIShop.Infrastructure/DependencyInjection.cs` — 删除 `IRecommendationService` 注册

**验收标准：** `dotnet build` 无 `RecommendationService` 相关错误

---

### Task 3：ShoppingAssistantAgent 改造

**文件：** `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs`

**操作：**
- 新增引用 `using Microsoft.Extensions.AI;`
- `RunChatAsync` 返回类型从 `Task<string>` 改为 `Task<AgentChatResult>`
- 单次 `RunAsync<AgentChatResult>()` 调用
- 代码风格与现有一致：原地构造，不抽静态字段

**输出 DTO（定义在 ChatEndpoints.cs）：**
```csharp
public sealed record AgentChatResult(string Reply, string[] Keywords);
```

**验收标准：** `RunChatAsync` 返回 `{ Reply, Keywords }`

---

### Task 4：删除 PreferenceAnalyzerAgent

**文件：** `src/AIShop.Api/Agents/PreferenceAnalyzerAgent.cs` — 删除

**文件：** `src/AIShop.Api/Program.cs`
- 删除 `builder.Services.AddScoped<PreferenceAnalyzerAgent>()`

**验收标准：** 无 PreferenceAnalyzerAgent 引用残留

---

### Task 5：改造 ChatEndpoints

**文件：** `src/AIShop.Api/Features/Chat/ChatEndpoints.cs`

**子任务 5a — DTO 变更**
- 新增 `AgentChatResult` DTO
- `ChatReply` 增加：
  - `RecommendedProducts`：推荐产品列表
  - `OtherProducts`：其他产品列表
  - `HasRecommendation`：是否有推荐标识
- 保留原 `RecommendationResponse` 不动

**子任务 5b — /api/chat 推荐逻辑重写**
- 上下文末尾嵌入含 KeywordMap 的 system prompt
- 调用 `RunChatAsync` 拿 `AgentChatResult`
- 白名单过滤：`.Where(k => KeywordMap.ContainsKey(k)).Distinct().Take(5)`
- 有推荐 → `MatchProducts()` + 双列表
- 无推荐 → 默认 6 个 + `HasRecommendation=false`
- try/catch 保护

**子任务 5c — /api/recommendations 改写**
- 删除 `PreferenceAnalyzerAgent` 注入
- 改为注入 `ShoppingAssistantAgent`
- 传分析专用 prompt

**子任务 5d — 新增 ToDto() 方法**
```csharp
private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);
```

**验收标准：** `/api/chat` 返回 `recommendedProducts`、`otherProducts`、`hasRecommendation`；`/api/recommendations` 返回不变

---

### Task 6：前端改造

**文件：** `src/AIShop.Api/wwwroot/index.html`

**子任务 6a — 新增渲染函数**
```javascript
function renderRecommendationPanel(data) { ... }
```
- `hasRecommendation=true` → 推荐区 + 其他区
- `hasRecommendation=false` → 灰标 + "暂无特定推荐"

**子任务 6b — 接入 sendMessage**
- 将 `renderRecommendations(data.recommendations, data.recMessage)` 替换为 `renderRecommendationPanel(data)`

**注意：** 保留原 `renderRecommendations()` 函数，`getRecommendations()` 按钮不受影响

**验收标准：** 聊天时推送双列表；"给我推荐商品"按钮行为不变

---

### Task 7：编译验证

```bash
dotnet build
```

**验收标准：** 0 错误，0 警告

---

### Task 8：手动验证

启动应用并测试三个场景：
1. **有推荐：** 登录 → "我想买双跑鞋" → 跑鞋绿色高亮，其他在"其他"区
2. **无推荐：** 登录 → "今天天气怎么样" → 灰标默认列表 + "暂无特定推荐"
3. **推荐按钮：** 登录 → 几轮对话后点"给我推荐商品" → 行为不变

**验收标准：** 三个场景全部通过

---

## 依赖关系

```
Task 1 ──→ Task 2 ──→ Task 4
                    ↘
Task 3 ──→ Task 5 ──→ Task 6 ──→ Task 7 ──→ Task 8
          ↗
    (Task 1, 4 需先完成)
```

| Task | 阻塞 | 被阻塞 |
|------|------|--------|
| 1 Core 清理 | — | 2 |
| 2 Infrastructure 清理 | 1 | — |
| 3 ShoppingAssistantAgent 改造 | — | 5 |
| 4 删除 PreferenceAnalyzerAgent | 1, 2 | 5 |
| 5 ChatEndpoints 改造 | 3, 4 | 6 |
| 6 前端改造 | 5 | 7 |
| 7 编译验证 | 6 | 8 |
| 8 手动验证 | 7 | — |

## 不涉及的文件

- `src/AIShop.Core/Entities/` — 实体不动
- `src/AIShop.Infrastructure/Data/AppDbContext.cs` — 不动
- `src/AIShop.Infrastructure/Services/ProductCatalog.cs` — 复用 `MatchProducts()`，不动
- `AGENTS.md` — 不动
- `tests/` — 后续安排
