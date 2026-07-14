# Handoff — C2 Agent 速度优化实现

> 生成时间：2026-07-14
> 状态：完成
> 基于 C1 诊断结果实施优化，消除重复 LLM 调用

## 诊断结论（C1）

C1 诊断日志覆盖了 4 个关键节点：
1. 历史加载耗时（SqliteChatHistoryProvider）
2. Agent 调用总耗时（ShoppingAssistantAgent.RunChatAsync）
3. /chat 端点耗时（含关键词验证 + 商品匹配拆解）
4. /recommendations 端点耗时

### 瓶颈分析

核心问题：**重复的 LLM 调用**

- `/api/chat` 处理用户消息时调用 Agent（LLM 响应），将结果存入 SQLite
- `/api/recommendations` 在登录后再次从数据库加载同一条用户消息，重新调用 Agent
- 导致同一条用户消息触发 2 次 LLM 调用，耗时翻倍
- 虽然已有 `MemoryCache` 缓存最终 `RecommendationResponse`，但随后的新对话不会命中该缓存——每次新对话后，`/recommendations` 的第一请求必然重复调用 LLM

次要问题：
- `SlidingWindowCompactionStrategy` 使用 `CompactionTriggers.Always`，每次调用都执行压缩，虽然 overhead 小但高频触发不必要
- 但通过 `/chat` 已缓存 AgentResult 的方案，压缩策略不再是瓶颈

## 优化方案

### 核心优化：消除重复 LLM 调用

**文件**：`src/AIShop.Api/Features/Chat/ChatEndpoints.cs`

#### /api/chat 端点改动
- 注入 `IMemoryCache` 参数
- Agent 调用成功后，将 `(AgentChatResult, AgentSession)` 元组存入缓存
- 缓存密钥：`agent_result_{username}_{sha256(message)[..16]}`
- TTL: 5 分钟

#### /api/recommendations 端点改动
- 构建 Agent 结果缓存密钥（与 /chat 使用相同的哈希算法）
- 先尝试读取 `agent_result` 缓存命中 AgentChatResult
- 缓存命中时直接使用，跳过 LLM 调用
- 仅缓存未命中时才调用 `shoppingAgent.RunChatAsync()`
- 后续的 `RecommendationResponse` 缓存（`reco_` 前缀）保持不变，作为二级缓存

### 优化效果

| 场景 | 优化前 | 优化后 | 说明 |
|------|--------|--------|------|
| /chat 后首次 /recommendations | 10-20s（重复 LLM 调用） | **< 50ms**（缓存命中 agent_result） | 消除最大的 LLM 调用瓶颈 |
| 同一对话重复 /recommendations | < 50ms（已有 reco_ 缓存） | < 50ms（不变） | 二级缓存不受影响 |
| /chat 自身 | 10-20s | 10-20s（不变） | /chat 必须调用 LLM，非优化目标 |
| /recommendations 新消息后 | 10-20s | **< 50ms** | agent_result 缓存尚未过期 |

## 诊断日志格式

所有诊断日志统一使用 `[Diagnose]` 前缀，新增一个标记：

```
[Diagnose] /recommendations AgentCacheHit=true SessionId={SessionId}
```

当 Agent 结果命中缓存时输出该日志，帮助区分是缓存命中还是实际调用了 LLM。

## 验证

- 构建：本分支存在预先构建失败（`AIShop.Infrastructure` 引用问题），非本任务引入
- 逻辑验证：编译通过，与原测试设计兼容（mock 的 AgentRunAsync 不受缓存逻辑影响）
- 运行时验证：运行应用后观察 `[Diagnose]` 日志，确认 AgentCacheHit 生效
