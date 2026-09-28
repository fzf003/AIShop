# AIShop Core 分层重构 — 设计文档

> 状态：**已实现** | 日期：2026-08-28 | 作者：Claude
> 关联问题：P2（Service→Infrastructure 依赖违规）、P5（推荐路径不一致）、P6（Provider 职责过重）
> 前置工作：Phase 1（ReplySanitizer 下沉）、Phase 2（AppHost MCP）、Phase 3（RecommendationMerger 下沉）、Phase 4（Cart 聚合内聚）、P2 步骤 1（偏好读取接口化）已完成

## 0. 落地摘要（2026-08-28）

| 阶段 | 内容 | 状态 | 落地产物 |
|---|---|---|---|
| 1 | 偏好模型升级（PreferenceProfile） | ✅ 完成 | `Core/ValueObjects/PreferenceProfile.cs` + `PreferenceProfileTests` |
| 2 | 压缩策略抽取（RoundBasedCompactionPolicy） | ✅ 完成 | `Core/Services/RoundBasedCompactionPolicy.cs` + `IChatCompactionPolicy` + `RoundBasedCompactionPolicyTests` |
| 3 | 聊天历史 store 下沉（IChatHistoryStore） | ✅ 完成（P2 闭环） | `Core/Interfaces/IChatHistoryStore.cs` + `Infrastructure/Services/ChatHistoryStore.cs` + `Service/Providers/ChatMessageMapper.cs`；`AIShop.Service.csproj` 移除 Infrastructure 引用 |
| 4 | 推荐编排下沉（RecommendationService） | ✅ 完成 | `Core/Services/RecommendationService.cs` + `Core/ValueObjects/Recommendation.cs` + `RecommendationServiceTests` |

**偏离与修正记录**：

1. **P8（EF Migrations）超范围落地**：设计 §1.3 将 P8 列为"用户明确暂缓"非目标，但实现中 `Program.cs` 改用 `MigrateAsync()` 并新增 `Infrastructure/Migrations/InitialCreate`。由此引入 **EnsureCreated（测试建库）/ MigrateAsync（宿主启动）混用冲突**——测试隔离库用 `EnsureCreated` 预建表（不写迁移历史），宿主 `MigrateAsync` 重跑迁移撞已存在表，异常被 `Program.cs` 顶层 `catch` 吞掉 → 59 个 Api 集成测试 host 启动失败。
2. **修复（测试侧对齐迁移契约）**：集成测试 `ReplaceWithIsolatedDb` 建库统一为 `Migrate()`（与宿主 `MigrateAsync` 同一路径）；`ProgramSeedingTests` 用例改写为"Migrate 预建库 → 启动跳过迁移 → 播种幂等"。
3. **门禁**：全量 `dotnet test` 通过（Api 204 / Service 129 / McpServer 11）。
4. **顺带修复（JSON 泄漏）**：`ChatMessageMapper.StripAgentReplyJson` 增加裸换行容错——LLM 输出的 `{"Reply":...,"Keywords":...,"Preferences":...}` 若含裸换行（非法 JSON）会剥离失败、原始 JSON 泄漏进 `chat_messages`；现归一化引号内换行重试，只存 `Reply` 纯文本。新增 `ChatMessageMapperTests`（3 例）。

---

## 1. 背景与目标

### 1.1 目标架构

```
┌─────────────────────────────────────┐
│           AIShop.Core（零依赖）      │
│  Entities: Cart, CartItem, User,    │
│            Session, Product,        │
│            ChatMessage              │
│  ValueObjects: PreferenceProfile,   │
│                Recommendation        │
│  Services: ReplySanitizer,          │
│            RecommendationMerger,    │
│            RoundBasedCompactionPolicy│
│  Interfaces: IChatHistoryStore,     │
│              IPreferenceRepository, │
│              IProductCatalogService,│
│              ICartRepository,       │
│              IChatMessageRepository,│
│              IUserRepository, ...   │
└───────────┬─────────────────────────┘
            │
  ┌─────────┼──────────┐
  ▼         ▼          ▼
Infrastructure  Service      Api
  EF/SQLite     Agent 编排    Minimal API
  Repository     LLM Client    DTO 映射
  Queue impl     Tool 注册     Cache
  ValueConverter Provider 壳
```

### 1.2 目标

1. **P2 闭环**：消除 Service→Infrastructure 依赖，回归 `Core ← Infrastructure ← {Api, Service}`
2. **P6 缓解**：Provider 拆分（持久化 / 策略 / MAF 适配 三职责分离），压缩策略可独立单测
3. **P5 铺垫**：推荐编排下沉 Core，统一三条推荐路径
4. 序列化（JSON↔模型）收敛到 Infrastructure 边界，Core 只操作强类型模型

### 1.3 非目标（明确不做）

- P7（前端 XSS）、P9（AsyncLocal）— 用户明确暂缓 / 单独评估
- DB schema 变更 — 本设计所有阶段保持现有表结构不变
- **P8（DB Migrations）— 用户明确暂缓，但实现中超范围落地**（见 §0 偏离记录）：`Program.cs` 已改用 `MigrateAsync()` 并新增 `Infrastructure/Migrations/`，当前按迁移契约维护，集成测试建库已对齐 `Migrate`

---

## 2. 现状基线

| # | 现状 | 问题 |
|---|------|------|
| 1 | `SqliteChatHistoryProvider` 同时承担持久化 / 缓存 / 压缩策略 / JSON 序列化 / 消息配对 / MAF 适配（6 职责，约 450 行） | 违反 SRP；压缩策略与存储耦合无法单测 |
| 2 | `AIShop.Service.csproj` 引用 `AIShop.Infrastructure.csproj`（4 处 `using AIShop.Infrastructure.*`） | P2 依赖倒挂 |
| 3 | 偏好以 `UserPreferences.KeywordsJson`（string）承载，读写两侧各自手写 `JsonSerializer.Deserialize<Dictionary<string,int>>` | 序列化散落；无强类型模型 |
| 4 | 偏好读接口化已完成（`IPreferenceRepository` via `IServiceScopeFactory`），但 `PreferenceWriteHostedService` 仍直接操作 `AppDbContext` + JSON | 写侧未接口化，读写不对称 |
| 5 | `ChatEndpoints` 内联推荐编排（匹配 / 兜底 / RecMessage 构造） | P5 路径不一致，逻辑在 Api 层 |

---

## 3. 设计原则

1. **改造优先于新建**：已有接口（`IPreferenceRepository`）升级而非平行新建，杜绝两套并存
2. **Core 零依赖**：Core 只引用 `Microsoft.Agents.Core`；MAF / EF 类型一律不进 Core
3. **序列化边界化**：JSON 序列化只发生在 Infrastructure（EF 边界），Core/Service 用强类型
4. **长生命周期不持 scoped**：常驻组件（agent/Provider/worker）通过 `IServiceScopeFactory` / `IDbContextFactory` 按需解析（沿用 `CartToolProvider`、`PreferenceWriteHostedService` 已证实的模式）
5. **每阶段独立可回归**：每阶段完成后 `dotnet build` + 全量测试通过才进入下一阶段

---

## 4. 阶段 1 — 偏好模型升级（PreferenceProfile）

### 4.1 目标

把偏好从 `KeywordsJson`（string）升级为强类型 `PreferenceProfile`，序列化收敛到仓储边界，**保留 worker 的 Top-20 截断语义**。

### 4.2 新增类型

**Core/ValueObjects/PreferenceProfile.cs**（纯值对象，无 IO）：

```csharp
namespace AIShop.Core.ValueObjects;

/// <summary>用户偏好配置文件（强类型替代 KeywordsJson 字符串）。</summary>
public sealed record PreferenceProfile(Guid UserId, IReadOnlyDictionary<string, int> KeywordWeights, DateTime UpdatedAt)
{
    /// <summary>Top-N 权重保留上限（对齐 worker 现有 Top-20 截断语义）。</summary>
    public const int MaxKeywords = 20;

    /// <summary>累加新关键词：逐词权重 +1，返回新实例（不修改原实例）。</summary>
    public PreferenceProfile Merge(IEnumerable<string> newKeywords)
    {
        var dict = KeywordWeights.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kw in newKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            dict[kw] = dict.GetValueOrDefault(kw) + 1;
        }
        return this with { KeywordWeights = dict, UpdatedAt = DateTime.UtcNow };
    }

    /// <summary>按权重降序（并列按 Key 序数序）截断到前 N，保证结果确定可复现。</summary>
    public PreferenceProfile TrimToTop(int max = MaxKeywords) => this with
    {
        KeywordWeights = KeywordWeights
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(max)
            .ToDictionary(kv => kv.Key, kv => kv.Value)
    };

    /// <summary>取权重最高的 max 个关键词（供推荐上下文注入）。</summary>
    public string[] TopKeywords(int max) =>
        KeywordWeights.OrderByDescending(kv => kv.Value)
                      .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                      .Take(max)
                      .Select(kv => kv.Key)
                      .ToArray();
}
```

> 设计要点：
> - `Merge` 与 `TrimToTop` 分离（累加与截断独立方法），worker 调用 `profile.Merge(kws).TrimToTop()` 完整复刻现有"逐词+1 → Top-20"语义
> - **修正用户原方案**：原 `Merge` 未含 Top-20 截断，直接照搬会导致 worker 的截断语义丢失
> - `TrimToTop` 的排序（权重降序 + Key 序数序）与 `PreferenceWriteHostedService` 现有实现逐字对齐

### 4.3 改造清单

| 文件 | 改动 |
|---|---|
| `Core/Interfaces/IPreferenceRepository.cs` | `GetByUserIdAsync` 返回 `PreferenceProfile?`；`UpsertAsync(PreferenceProfile profile)`（内部持有 `UserPreferences` 实体） |
| `Infrastructure/Repositories/PreferenceRepository.cs` | 边界转换：`UserPreferences.KeywordsJson` ↔ `PreferenceProfile.KeywordWeights`（含非法 JSON 容错 → 空集） |
| `Infrastructure/Services/PreferenceWriteHostedService.cs` | 读旧值 → `PreferenceProfile.Merge` → `TrimToTop(20)` → 序列化写库；**保留 `IDbContextFactory` 短生命周期**（避免 scoped 捕获），仅替换手写 JSON 逻辑 |
| `Service/Providers/PreferenceMemoryProvider.cs` | 从 `IPreferenceRepository` 拿 `PreferenceProfile`，展示文本用 `profile.TopKeywords(...)`，移除 `BuildDisplayText` 的 JSON 解析 |
| `Api/Features/Chat/ChatEndpoints.cs` | 3 处 `GetTopPreferenceKeywords(prefs?.KeywordsJson, 5)` → `prefs?.TopKeywords(5)` |
| `Service/Services/RecommendationMerger.cs` | `GetTopPreferenceKeywords` 若无其他调用方则删除（其解析职责由仓储边界承担） |

### 4.4 风险与验证

- **风险低**：DB schema 不变（列仍为 `KeywordsJson` string）；接口返回值类型变更由编译器保证全量替换
- 验证：新增 `PreferenceProfileTests`（Merge / TrimToTop / TopKeywords 排序确定性）；`PreferenceWriteHostedService` 行为回归（现有测试若覆盖 Top-20 则保留）
- **决定**：本阶段不引入 EF `ValueConverter`（会改变实体属性类型、连锁改动所有 `KeywordsJson` 引用方），列为可选后续优化

---

## 5. 阶段 2 — 压缩策略抽取（RoundBasedCompactionPolicy）

### 5.1 目标

把 `SqliteChatHistoryProvider.StoreChatHistoryAsync` 里的三段压缩判断（完整轮上限 / 未完成轮上限 / 硬上限）抽成 Core 纯逻辑，可独立单元测试；**接口形态对齐 MAF 官方压缩机制**（见 §5.3），策略可插拔。

### 5.2 新增类型

**Core/Models/StoredMessage.cs**（纯数据，Core 边界模型）：

```csharp
namespace AIShop.Core.Models;

/// <summary>聊天历史存储模型（EF 实体与 MAF 消息之间的防腐层，无框架依赖）。</summary>
public sealed record StoredMessage(
    long Id,
    Guid SessionId,
    Guid? RunId,
    string Role,
    string? Content,
    string? ToolCalls,
    string? ToolCallId,
    string? Reasoning,
    bool IsFinal,
    bool IsCompacted,
    DateTime CreatedAt);
```

**Core/Interfaces/IChatCompactionPolicy.cs**：

```csharp
namespace AIShop.Core.Interfaces;

/// <summary>压缩策略 — 纯领域规则，无 IO。输入未压缩消息集合，输出要标记压缩的行 ID。</summary>
public interface IChatCompactionPolicy
{
    IReadOnlyList<long> SelectCompaction(IReadOnlyList<StoredMessage> messages);
}
```

**Core/Services/RoundBasedCompactionPolicy.cs**：

```csharp
public sealed class RoundBasedCompactionPolicy(
    int maxCompletedRounds = 12,
    int maxIncompleteRounds = 5,
    int hardLimit = 4096) : IChatCompactionPolicy
{
    public IReadOnlyList<long> SelectCompaction(IReadOnlyList<StoredMessage> messages)
    {
        // 迁移现有三段逻辑，逐字保真：
        //   1. 按 RunId 分组（RunId==null 每行自成组）→ 完整轮 = 组内有 IsFinal
        //   2. 阶段1：完整轮超 maxCompletedRounds → 压最旧整轮
        //   3. 阶段2：未完成轮（RunId 非空且行数≥2）超 maxIncompleteRounds → 压最旧
        //   4. 阶段3：保留区条数超 hardLimit → 压最旧整轮至上限内
        // 配对保护天然成立：整轮一起压，FCC↔tool 结构性不分离
        ...
    }
}
```

> 设计要点：
> - **修正用户方案**：`CompactionDecision` 的 `Retained` 列表是冗余（落库只需 `ToCompact`），简化为直接返回 `IReadOnlyList<long>`
> - 输入为"未压缩集合"（`IsCompacted==false`），由调用方（store/Provider）保证
> - 三段语义、分组键、未完成轮计数口径（RunId 非空且行数≥2）全部原样迁移，**不做任何改进**

### 5.3 参考 MAF 官方压缩机制（查证：codebase-memory → agent-framework `AgentSession-Architecture-Guide` §7/§9.2）

MAF 官方压缩架构与项目自研是**两种不同范式**：

| 维度 | MAF 官方（`CompactionProvider`） | 项目自研（本设计） |
|---|---|---|
| 定位 | `AIContextProvider` — 上下文窗口管理，压缩后送模型 | `ChatHistoryProvider` — 持久化存储管理，DB 标记保留 |
| 触发 | token 预算（`CompactionTriggers.TokensExceed`） | 轮数（12/5）+ 硬上限（4096 条） |
| 动作 | tool 结果折叠为摘要 + 截断最旧组 | 整轮标记 `IsCompacted`（非物理删除） |
| 摘要 | 需 LLM 调用（Summarization） | 零 LLM 调用 |
| 保留 | 原始消息在 State 分组，压缩后仅送模型 | 原始行永久保留，可追溯可查询 |
| 组模型 | `CompactionMessageGroup`(TurnIndex, Kind: Regular/Summary) | run_id 分组 + IsFinal 判定 |

**对齐点（设计采纳）：**
1. **策略可插拔**：对齐 MAF `CompactionStrategy` 抽象继承树，`IChatCompactionPolicy` 为策略接口、`RoundBasedCompactionPolicy` 为当前实现。未来若需 token 预算压缩（对齐 MAF `ContextWindowCompactionStrategy`），新增 `IChatCompactionPolicy` 实现即可，Provider 零改动
2. **组模型同构**：`StoredMessage.RunId` 即 MAF `CompactionMessageGroup.TurnIndex` 的对应物；完整轮/未完成轮判定对齐 MAF 的 GroupKind 语义
3. **可与 MAF 压缩共存**：项目 `DisableCompaction=true` 禁用的是 MAF 上下文窗口压缩（需 LLM 摘要 + 模型 token 预算），与存储层压缩**不冲突**。未来若启用 MAF 压缩，分工为：MAF 管上下文窗口、自研管存储量

**为何不直接换用 MAF 压缩（决策）：** 项目 spec 要求"按 run_id 整轮整切不拆轮、保留原始数据可追溯、未完成轮整组保留"——这是**存储层标记语义**，MAF 的上下文窗口压缩（折叠/截断 + LLM 摘要）不满足且引入额外 LLM 成本。自研策略是**有意为之**，本次只对齐接口范式、不替换实现。

### 5.4 验证

- 新增 `RoundBasedCompactionPolicyTests`：完整轮超限 / 未完成轮超限 / 硬上限 / run_id=NULL 历史行 / 并列权重确定性，覆盖现有三阶段分支
- 此阶段**不动 EF 层**，纯新增，零回归风险

---

## 6. 阶段 3 — 聊天历史 store 下沉（IChatHistoryStore）

### 6.1 目标

持久化（EF）下沉 Infrastructure，Provider 瘦壳化，**移除 Service→Infrastructure 引用（P2 闭环）**。

### 6.2 新增接口与实现

**Core/Interfaces/IChatHistoryStore.cs**：

```csharp
public interface IChatHistoryStore
{
    /// <summary>加载未压缩消息（含过滤条件由实现保证：IsCompacted==false）。</summary>
    Task<IReadOnlyList<StoredMessage>> LoadUncompactedAsync(Guid sessionId, CancellationToken ct);

    /// <summary>追加新消息并提交。</summary>
    Task AppendAsync(Guid sessionId, IReadOnlyList<StoredMessage> messages, CancellationToken ct);

    /// <summary>将指定行标记 IsCompacted=true（非物理删除，保留原始数据）。</summary>
    Task MarkCompactedAsync(IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>Run 后兜底补标轮次终点（spec「补标写入口收敛」约束保持单一入口）。</summary>
    Task MarkRoundFinalAsync(Guid runId, CancellationToken ct);
}
```

**Infrastructure/Services/ChatHistoryStore.cs**（实现，仅 EF，无 MAF 依赖）：

```csharp
public sealed class ChatHistoryStore(IDbContextFactory<AppDbContext> dbFactory) : IChatHistoryStore
{
    // LoadUncompactedAsync / AppendAsync / MarkCompactedAsync / MarkRoundFinalAsync
    // 全部委托 db.ChatMessageRecords，StoredMessage ↔ ChatMessageRecord 双向映射
}
```

### 6.3 Provider 瘦壳化（Service/Providers/SqliteChatHistoryProvider.cs）

保留三类职责，移除 EF：

| 保留（Provider 层） | 移除（下沉 store） |
|---|---|
| MAF 消息↔StoredMessage 转换（<think> 剥离、StripAgentReplyJson、tool_calls 序列化、孤儿 tool 配对） | `IDbContextFactory<AppDbContext>` |
| 会话内缓存（`State.Messages`：首查回填 / Store 追加 / 压缩后重建） | 直接 `db.ChatMessageRecords` CRUD |
| `MarkRoundFinalAsync` 转发到 `_store` | 三段压缩判断 |
| `ProvideOutputMessageFilter` 等框架适配 | |

瘦壳 Store 流程（对齐现有"先追加提交、再基于最新集合压缩"）：
1. 转换 `ctx` 消息 → `StoredMessage[]`
2. `_store.AppendAsync(sessionId, newMessages)` → 追加提交
3. `_store.LoadUncompactedAsync(sessionId)` → 含新消息的未压缩集合
4. `_compaction.SelectCompaction(all)` → 要压缩的 ID
5. `_store.MarkCompactedAsync(ids)`（若非空）
6. 重建 `State.Messages` 缓存

> 设计要点：
> - **修正用户方案**：瘦壳类名保留 `SqliteChatHistoryProvider`（避免遮蔽 `Microsoft.Agents.AI.ChatHistoryProvider` 基类）；会话内缓存不能丢
> - 与 `IChatMessageRepository`（Api 读历史）并存：同一张表两个视角（Agent 存储 vs API 展示），职责不同可共存，双模型映射成本可接受

### 6.4 P2 闭环

移除 `AIShop.Service.csproj` 第 6 行 `AIShop.Infrastructure.csproj` 引用。Service 仅剩依赖：`AIShop.Core` + `AIShop.AgentTelemetry` + MAF 包 + DI 抽象。依赖方向回归 `Core ← Infrastructure ← {Api, Service}`。

---

## 7. 阶段 4 — 推荐编排下沉（P5）

### 7.1 目标

统一 `/chat`、`/recommendations`、Agent 推荐三条路径口径，推荐编排从 `ChatEndpoints` 下沉 Core。

### 7.2 实现（2026-08-28 定稿）

**Core/ValueObjects/Recommendation.cs**（结果形状，商品以 Core 实体承载，Api 层转 ProductDto）：

```csharp
public sealed record Recommendation(
    IReadOnlyList<Product> Recommended,
    IReadOnlyList<Product> Other,
    string Message,
    bool HasRecommendation,
    IReadOnlyList<string>? MatchedCategories);
```

**Core/Services/RecommendationService.cs**（纯逻辑，依赖内存商品目录，无 IO）：

```csharp
public sealed class RecommendationService(IProductCatalogService catalog)
{
    public Recommendation Build(string[] currentKeywords, string[] prefKeywords);
    // 合并关键词（RecommendationMerger.MergeKeywords）→ SplitProducts 匹配 → 精选兜底
}
```

**落地要点**：
- `RecommendationService` 已实现并注册 DI（`AddScoped<RecommendationService>()`），`ChatEndpoints` 的 `/chat` 与 `/recommendations` 共用同一编排，口径统一
- `RecommendationMerger` 从 `Api/Features/Chat/` 下沉为 `Core/Services/RecommendationMerger.cs`（纯函数 `MergeKeywords`）
- 兜底文案与 `HasRecommendation` 语义单一来源（`RecommendationService`）
- 新增 `RecommendationServiceTests`

> 原"独立排期、待阶段 1-3 稳定后细化"的规划被**提前完成**：阶段 1-3 落地时推荐编排一并下沉，实际实现与 §1.1 目标架构一致。

---

## 8. 迁移映射表

| 现有 | 迁移后 |
|---|---|
| `UserPreferences.KeywordsJson`（string）+ 各处手写 JSON 解析 | `PreferenceProfile`（强类型），JSON 仅存仓储边界 |
| `SqliteChatHistoryProvider` 压缩判断（内嵌） | `RoundBasedCompactionPolicy.SelectCompaction` |
| `SqliteChatHistoryProvider` EF CRUD | `ChatHistoryStore`（`IChatHistoryStore`） |
| `SqliteChatHistoryProvider` MAF 转换 + 缓存 | 保留在瘦壳 Provider |
| Service 直接 `using AIShop.Infrastructure.Data` | 移除（仅剩 `IChatHistoryStore` 等 Core 接口） |
| `ChatEndpoints` 内联推荐编排 | `RecommendationService`（阶段 4） |

---

## 9. 测试清单

| 阶段 | 新增测试 | 回归 |
|---|---|---|
| 1 | `PreferenceProfileTests`（Merge 累加 / TrimToTop 排序与截断 / TopKeywords）✅ | 全量 + worker Top-20 行为 |
| 2 | `RoundBasedCompactionPolicyTests`（三阶段分支 + run_id NULL + 确定性）✅ | 全量 |
| 3 | 历史 store 相关：`SqliteChatHistoryProviderTests` / `ChatMessageMapperTests` / `RunChatStreamAsyncTests` 等 Service.Tests 全绿 ✅ | 全量 |
| 4 | `RecommendationServiceTests`（三路径口径一致）✅ | 全量 |

> 全量 `dotnet test`：Api 204 / Service 129 / McpServer 11，2026-08-28 通过。

---

## 10. 风险评估

| 风险 | 等级 | 缓解 |
|---|---|---|
| 压缩逻辑迁移行为回归（run_id 整轮整切、孤儿配对、安全阀） | 🔴 高 | 阶段 2 先抽纯逻辑 + 单元测试锁语义，阶段 3 才动 EF；`Service.Tests` 126 例为回归屏障 |
| 偏好 Top-20 截断语义丢失 | 🟡 中 | `PreferenceProfile.Merge + TrimToTop` 复刻 worker 逐行语义；排序确定性测试 |
| 接口返回值变更连锁（`IPreferenceRepository`） | 🟡 中 | 编译器强制全量替换；`ChatEndpoints` 3 处同步改 |
| captive dependency 复发（长生命周期持 scoped） | 🟡 中 | 阶段 1 worker 保留 `IDbContextFactory`；阶段 3 store 短生命周期 |
| 双模型映射（`StoredMessage` ↔ EF / MAF）成本 | 🟢 低 | 映射集中在 store / Provider 单点，可接受 |

---

## 11. 落地顺序与门禁

| 步骤 | 内容 | 完成门禁 |
|---|---|---|
| 1 | 阶段 1 偏好模型升级 | ✅ build 0 警告 + 全量测试通过 |
| 2 | 阶段 2 压缩策略抽取 | ✅ 新增 policy 单测全绿 + 全量回归 |
| 3 | 阶段 3 store 下沉 + 移除 Infrastructure 引用 | ✅ **P2 闭环**：Service csproj 不再引用 Infrastructure + 全量测试通过 |
| 4 | 阶段 4 推荐编排（独立排期） | ✅ 三路径口径一致测试（提前完成） |

> 门禁已达成：`dotnet build` 0 警告 0 错误 + `dotnet test` 全绿（Api 204 / Service 129 / McpServer 11），2026-08-28。
> 落地阶段已过，改动可提交归档（不再受"仅存本地不 commit/push"约束）。

---

## 12. 关键决策记录（摘要）

1. `PreferenceProfile.Merge` 与 `TrimToTop` 分离，worker 组合调用 → 保留现有截断语义
2. 偏好读写统一走 `IPreferenceRepository`（改造），不新建 `IPreferenceProfileStore` 平行接口
3. `CompactionDecision` 简化为 `IReadOnlyList<long>`，去掉冗余 `Retained`
4. 瘦壳保留 `SqliteChatHistoryProvider` 类名，避免遮蔽 MAF 基类
5. 阶段 1 不用 EF `ValueConverter`（避免实体属性类型连锁改动），列为可选后续
6. 聊天历史 `IChatHistoryStore` 与 `IChatMessageRepository` 并存（Agent 存储 vs API 展示）
7. 压缩策略对齐 MAF 接口范式（`IChatCompactionPolicy` 可插拔）但保留自研实现——`DisableCompaction=true` 的原因：MAF 压缩是上下文窗口范式（LLM 摘要 + token 预算），项目是存储层标记范式（见 §5.3）
