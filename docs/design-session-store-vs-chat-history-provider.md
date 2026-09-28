# SqliteAgentSessionStore 与 SqlChatHistoryProvider 的关系与请求链路

> 状态：说明性文档（2026-09-15 整理，源码坐标已逐条核对）
> 范围：AguiHost 的会话持久化链路。**不含**改动建议，改动走 OpenSpec 变更流程。

## 1. 一句话结论

两者是**平级、互不引用的两个 MAF 扩展点实现**，各管一种持久化粒度，被 MAF 用装饰器串在同一根请求链上；唯一的通信信道是 `AgentSession.StateBag`。

| | `SqliteAgentSessionStore` | `SqlChatHistoryProvider` |
|---|---|---|
| 基类 | MAF `AgentSessionStore`（`Microsoft.Agents.AI.Hosting`） | MAF `ChatHistoryProvider` + 自建 `IChatHistoryCleaner` |
| 粒度 | **整个会话快照**（一段 JSON） | **单条消息**（一行） |
| 落库表 | `agent_sessions(store_id, session_json, updated_at)` | `chat_messages(conversation_id, sequence, message_json, round_id, …)` |
| 库文件 | `agui.sessions.db` | `agui.chat.db` |
| 调用者 | AG-UI 宿主（`MapAGUIServer` 内部，经 `AIHostAgent`） | MAF `ChatClientAgent`（每次 run 前后） |
| 调用时机 | 请求开始 `GetSessionAsync` / 流结束 `SaveSessionAsync` | run 前 `Provide` / run 后 `Store` |

数据流的分工：**消息内容从 wire 来**（AG-UI 客户端每轮重发全量历史），`chat_messages` 是**只增不读的审计 / 召回账本**（`Provide` 恒空），`agent_sessions` 只保 `StateBag` 这类非消息状态。

## 2. 装配与对象图

```
MapAGUIServer(agentName, pattern)
   │
   ├─ GetKeyedService<AgentSessionStore>(agent.Name)
   │      └─ IsolationKeyScopedAgentSessionStore        ← MAF 自动包的一层
   │             └─ SqliteAgentSessionStore             ← 本项目实现
   │
   └─ new AIHostAgent(aiAgent, sessionStore)
          └─ aiAgent                                    ← AGUIShoppingAgent.Create 产物
                 └─ ChatClientAgentOptions.ChatHistoryProvider
                        └─ SqlChatHistoryProvider       ← 本项目实现
```

关键装配点：

- keyed 注册：`SessionStoreDependencyInjection.cs:71`（key = `AGUIShoppingAgent.AgentName`）
- provider 注册：`AguiChatHistoryDependencyInjection.cs:46`，由 `appsettings.json` 的 `Agui:ChatHistoryProvider == "Sql"` 决定是否启用
- provider 挂载：`Program.cs:99`（作为 `chatHistoryProvider` 参数传给 `AGUIShoppingAgent.Create`）

> `IsolationKeyScopedAgentSessionStore` 本可给 `store_id` 加 `{隔离键}::{threadId}` 前缀，但本项目**未注册** `AgentIsolationKeyProvider`（`AguiUsernameForwarder.cs:20` 仅有注释提及），故该层为非 strict、原样透传——store 收到的是裸 AG-UI `ThreadId`。

## 3. 请求时序

以 `POST /`（AG-UI SSE）为例：

| 步 | MAF 侧 | 本项目侧 |
|---|---|---|
| 1 | `MapAGUIServer` 注册端点，`new AIHostAgent(aiAgent, store)` | `Program.cs:135` |
| 2 | `threadId = ThreadId ?? Guid`，隔离层透传 | — |
| 3 | `hostAgent.GetOrCreateSessionAsync(threadId)` → `store.GetSessionAsync(agent, threadId)` | `SqliteAgentSessionStore.cs:97`：`store_id = "{agent.Name}:{用户名}"`；命中则 `DeserializeSessionAsync` 还原 StateBag 并补写 `AguiConversationId`（`:165`）；未命中 / 过期则 `CreateSessionAsync` + 同样打标 |
| 4 | `RunStreamingAsync` → `ChatClientAgent` | `Provide` 恒空（`SqlChatHistoryProvider.cs:197`）→ 模型 + 工具循环（FICC，上限 3）→ `Store`（`:223`）：按位置切当前轮，连同全部 `ResponseMessages` 落 `chat_messages`（`round_id` 递增、单事务、`UNIQUE` 冲突乐观重试 3 次） |
| 5 | `hostAgent.SaveSessionAsync(threadId, session)` | `SqliteAgentSessionStore.cs:70`：压缩（仅 InMemory 路径）→ 清 InMemory 消息 → 剔除 `AGUIShopping-Compaction` 键 → `SerializeSessionAsync` → upsert `agent_sessions` |
| 6 | — | `SessionCleanupService` 后台周期（默认 12h）驱动两个库的 TTL：过期会话行（store） + 过期轮整轮软删（`IChatHistoryCleaner` → provider） |

前端两步中间件（`Program.cs:125-130`，均在 `MapAGUIServer` 之前）：

- `UseAguiUsernameForwarding` → `ICurrentUserAccessor`（AG-UI forwarded metadata 的 username，缺省 `steve`）
- `UseAguiModelForwarding` → `IActiveModelProvider`（forwardedProps.model，缺省走 `ActiveModel`）

## 4. 两个交汇点

**① `AgentSession.StateBag`（唯一通信信道）**

```
SqliteAgentSessionStore ──写──> StateBag["AguiConversationId"] = 用户名
                                        │
                                        └──读──> SqlChatHistoryProvider.DefaultStateInitializer
                                                  → conversation_id = 用户名
```

`AguiSessionStateKeys` 被抽成中立常量类，是为了让写入方（通用会话设施）不反向依赖读取方（具体 provider 实现）。

**② `SaveSessionAsync` 的单向越界**

store 会主动改 provider 的领域：`CompactSessionHistoryAsync`（`SqliteAgentSessionStore.cs:178`，仅对 `InMemoryChatHistoryProvider` 生效）与 `SetInMemoryChatHistory`（`:80`）。这是整条链上唯一的耦合异味，第 5 节记录其中一处疑点。

## 5. 已知代价与待确认事项

### 5.1 会话归属只看用户名，不看 threadId（有意为之）

`ResolveStoreId`（`:149`）与 `ResolveConversationId`（`:172`）都只取用户名：

- 同一用户在 AG-UI 上开多个对话（不同 `ThreadId`）会**共用同一条 `agent_sessions` 行和同一个 `conversation_id`**——上下文互相串。
- 这是 `2547c53`「会话归属改为按用户名」的既定语义，**不是隔离而是合并**。意图已确认，代价需知晓。

### 5.2 `SaveSessionAsync` 清消息的 stateKey 可疑（未实测）

`SqliteAgentSessionStore.cs:80`：

```csharp
session.SetInMemoryChatHistory(new List<ChatMessage>(), stateKey: nameof(SqlChatHistoryProvider));
```

注释写「清除 InMemoryChatHistoryProvider 的消息」，但传入的 key 是 `nameof(SqlChatHistoryProvider)`，而它**正是 `SqlChatHistoryProvider` 自己存 state 的键**（`ProviderSessionState.StateKey = GetType().Name`，见 `SqlChatHistoryProvider.cs:120`）。

按 MAF 源码推导的两重后果（镜像 `AgentSessionExtensions.cs:55`、`AgentSessionStateBag.cs:99`、`AgentSessionStateBagValue`）：

1. **该清的没清**：清 InMemory 消息应使用默认键 `nameof(InMemoryChatHistoryProvider)`。
2. **不该覆盖的被覆盖**：同键条目被写成 `{"messages":[]}`。同进程内无害（类型不匹配时 `TryReadDeserializedValue` 返回 `false` 而非抛，provider 回退读 `AguiConversationId`，归属仍正确）；**跨重启**可疑——快照中该键为 `{"messages":[]}`，恢复时 `GetOrInitializeState` 走 `[JsonConstructor] State(string conversationId)` 而 `conversationId` 缺失，`ThrowIfNullOrWhiteSpace(null)` 会抛，且 `default:` 分支直接反序列化、不捕异常。

> **状态：静态推导，尚未实测。** `appsettings.json` 已开启 `"ChatHistoryProvider": "Sql"`，故非休眠路径。需一个「store 存一轮 → 用 Sql provider 跨实例反序列化恢复」的测试坐实后再定性。

## 附录：源码坐标

本项目：

- `src/AIShop.Service/Agui/SqliteAgentSessionStore.cs`
- `src/AIShop.Service/Agui/SqlChatHistoryProvider.cs`
- `src/AIShop.Service/Agui/AguiSessionStateKeys.cs`
- `src/AIShop.Service/Agui/SessionStoreDependencyInjection.cs`
- `src/AIShop.Service/Agui/AguiChatHistoryDependencyInjection.cs`
- `src/AIShop.Service/Agui/AGUIShoppingAgent.cs`
- `src/AIShop.AguiHost/Program.cs`

MAF 镜像（`E:\github\ProActor\aspire13app\agent-framework\dotnet\src\`）：

- `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore/AGUIEndpointRouteBuilderExtensions.cs`（`:113` keyed 解析、`:148` Get、`:197` Save）
- `Microsoft.Agents.AI.Hosting/AIHostAgent.cs`
- `Microsoft.Agents.AI.Hosting/IsolationKeyScopedAgentSessionStore.cs`
- `Microsoft.Agents.AI.Abstractions/ProviderSessionState{TState}.cs`、`AgentSessionStateBag.cs`、`AgentSessionStateBagValue.cs`、`AgentSessionExtensions.cs`
