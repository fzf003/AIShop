# AGUI Host — 独立购物 Agent 宿主 设计

> 日期：2026-09-05
> 状态：设计已确认，待走 OpenSpec/writing-plans 实施
> 框架：net10.0 / C# 14 | MAF 1.20
> 关联：`docs/design/agent-evolution-design.md`、`docs/superpowers/specs/2026-07-05-harness-agent-migration-design.md`

---

## 1. 背景与目标

AIShop 现有对话能力由 `ShoppingAssistantAgent`（HarnessAgent 外壳）+ `AIShop.Api` 手写 SSE 端点 + 自制 `index.html` 承载。本项目要验证 **MAF 1.20 官方标准宿主**这条下一代接入路径：基于 AG-UI 协议搭建一个**独立新 Host**，对外暴露购物 Agent 能力，UI 走 AG-UI 标准（不再沿用自制 UI），为将来 UI 演进/DevUI 铺路。

**目标**：`src/AIShop.AguiHost` 上线后，一条"推荐→加购→查车"的对话能经 AG-UI 协议流式跑通，老功能零影响。

## 2. 硬约束（用户拍板）

1. `ShoppingAssistantAgent` 与老 `AIShop.Api` 链路**冻结零改动**（不改、不重构、不迁移其内部实现）。
2. 新购物助手 Agent **从零重新设计**，不搬旧外壳内部逻辑。
3. **隔离 > 复用**：AGUI Host **自建实现 + 轻引 `AIShop.Core`**；不引用 `AIShop.Service` / `AIShop.Infrastructure` 的实现代码。自建实现放 AguiHost 内独立文件夹隔离。
4. 需要改动老代码时**单独变更、隔离回归**（本设计尽量避免）。

## 3. MAF 1.20 AG-UI 事实快查（官方源码，非过时 skill）

来源：codebase-memory 本地索引 `E-github-ProActor-aspire13app-agent-framework`（`dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`）。

- 装配：`builder.Services.AddAGUIServer()`；端点：`app.MapAGUIServer("/", agent)`，其中 `agent` 是 `AIAgent`（`chatClient.AsAIAgent(name, instructions)` 的 ChatClientAgent 形态），或 `MapAGUIServer(agentName, pattern)`（keyed DI 解析）。
- 传输：客户端 `POST RunAgentInput` → 服务端 **SSE 事件流**（`TypedResults.ServerSentEvents` / net10 原生）。事件经 `AsAGUIEventStreamAsync` 由 `ChatResponseUpdate` 流转换。
- 会话：`AgentSessionStore` 跨请求持久会话，默认 ephemeral；多用户须 `UseClaimsBasedAgentIsolation`（`Microsoft.Agents.AI.Hosting.AspNetCore`）+ `WithInMemorySessionStore`。ThreadId = 会话续接键（来自 wire，非授权令牌）。
- 宿主包装：`AIHostAgent(agent, sessionStore)` 提供 `GetOrCreateSessionAsync` / `RunStreamingAsync(messages, session)` / `SaveSessionAsync`——**消息数组驱动**。
- 官方样例（本地镜像）：`dotnet/samples/02-agents/AGUI`（Step01 GettingStarted → Step05 StateManagement，每步 Server+Client）；`05-end-to-end/AGUIClientServer`。
- 客户端：`Microsoft.Agents.AI.AGUI`（`AGUIChatClient`）。DevUI：进程内 `builder.AddDevUI()`（`Microsoft.Agents.AI.DevUI`）+ Aspire 资源 `Aspire.Hosting.AgentFramework.DevUI.AddDevUI`。

> ⚠️ 本地 `maf-reference` skill 参考停在 MAF 1.10，**不作为本设计 API 依据**。

## 4. 总架构

```
新项目 src/AIShop.AguiHost（ASP.NET Core，引用 AIShop.Core + MAF hosting/AGUI/DevUI 包）
│
├─ AddSerilog / AddAGUIServer()
├─ 注册（自建 DI）：
│     IChatClient            ← ActiveModel（Azure-OpenAI 兼容，MVP 单模型）
│     ICartRepository        ← 自建 EF 实现（独立 SQLite）
│     IProductCatalogService ← 自建关键词匹配实现
│     ICurrentUserAccessor   ← 从 AG-UI forwarded metadata 读 username（默认 guest）
├─ 新 Agent：
│     chatClient.AsAIAgent("AGUIShopping", instructions).WithTools(5 购物工具)
├─ app.MapAGUIServer("/", agent)
│
└─ AppHost：AddProject<AguiHost>("agui")
      消费者（MVP）：官方 AGUIClient（Microsoft.Agents.AI.AGUI）
      后续：DevUI 面板 / Aspire DevUI 资源
```

依赖方向（同现有规则）：`AguiHost → AIShop.Core`（纯领域）；不依赖 Service/Infrastructure。

## 5. 依赖切分：轻引 Core vs 自建

| 层 | 轻引 `AIShop.Core` | 自建（AguiHost 内 `Data/` + `Features/` 隔离） |
|---|---|---|
| 实体 | `Product`、`Cart`/`CartItem`、`User` | — |
| 接口契约 | `ICartRepository`、`IProductCatalogService`、`ICurrentUserAccessor` | — |
| 静态/逻辑 | `ProductSeedData`（商品）、`ProductKeywordMap`（关键词）、`ReplySanitizer`（纯静态清洗） | — |
| 实现 | — | 独立 EF `DbContext` + 独立 SQLite（如 `agui.db`）；EF 版 `ICartRepository`；关键词版 `IProductCatalogService`；username 访问器；5 工具 |

**MVP 对齐折扣**：旧 `search_product` 走 RAG 语义检索（bge ONNX）；隔离自建下 MVP 用**关键词匹配**降级（本地种子检索），语义检索列入后续路线。回复协议不复用旧的 `Reply+Keywords+Preferences` JSON（面向旧前端+偏好记忆）；新 Agent 以自然语言回复 + AG-UI 流式。

## 6. 数据与存储

- 独立 SQLite `agui.db`（老库 `aishop.db` 不受影响）；EF Migrations 建表（对齐宿主 MigrateAsync 约定，勿用 EnsureCreated）。
- 播种：商品（复用 `ProductSeedData`）+ 测试用户（marla/steve/fzf003，同老 Seed 语义）。
- 表：Products / Carts / CartItems / Users（MVP 会话历史可不落库，由 AGUI 会话/内存承载）。

## 7. 新购物 Agent 设计

- **形态**：`AsAIAgent("AGUIShopping", instructions)`（ChatClientAgent）+ `.WithTools(...)`；会话由 AG-UI `AgentSessionStore` 管理（ThreadId），无自管 session 字典。
- **模型**：MVP 单模型（配置节 ActiveModel，Azure-OpenAI 兼容 `IChatClient`）；多模型热切换不在 MVP。
- **工具**（名称/语义对齐旧工具，封装自建仓储）：
  - `search_product(keyword)` → `IProductCatalogService`
  - `add_to_cart(productId, quantity)` / `update_cart_quantity` / `get_cart_summary` / `remove_from_cart` → `ICartRepository`
- **人设**：中文简洁购物助手；Instructions 含工具规则与"工具调用后必回话"等护栏（参考旧设计思路，不复刻其实现）。
- **记忆**：MVP 不做跨会话画像；本轮上下文由 AGUI 会话历史提供。
- **用户身份**：`RunAgentInput` forwarded metadata（如 `username`）→ `ICurrentUserAccessor` 实现；缺省 `guest`。

## 8. 非目标（明确排除，避免范围蔓延）

- 不做多模型热切换、不做 RAG 语义检索、不做跨会话画像记忆
- 不迁移旧 `ShoppingAssistantAgent` 外壳、不重复其 JSON 输出协议
- DevUI 面板 / Aspire DevUI 资源 / HITL / 前端工具渲染 / Step02-05 能力 = **后续路线**，不进首版
- 不触碰 `AIShop.Api` / `AIShop.McpServer` 任何端点与配置

## 9. 验收标准（可测）

1. `dotnet build AIShop.sln`（含新 AguiHost）0 警告 0 错误
2. 起 AguiHost：官方 AGUIClient 发 `"推荐跑步鞋"` → **SSE 流式文本 + 触发 `search_product`**（无崩溃、轮次标记正常）
3. 对话 `"把第 1 个加购物车"` → `add_to_cart` 生效；`get_cart_summary` 反映（SQLite `agui.db` 可见）
4. **回归零影响**：老测试全绿 + `git diff -- src/AIShop.Service/ShoppingAssistantAgent.cs` 为空
5. 数据隔离：`agui.db` 独立，老 `aishop.db` 不变

## 10. 后续路线（不进首版）

DevUI 开发面板 → 多模型路由 → RAG 语义检索 → 跨会话记忆 → AG-UI HITL / 工具渲染 → Aspire DevUI 编排资源。

## 11. 风险与取舍

- 隔离自建代价：商品检索精度 MVP 降级为关键词；模型单例复用链需在 Host 重配（老的在 Service 内）。
- AG-UI 为 preview 包（1.20.0-preview.*）：API 可能小步变动，实施时以本地镜像源码为准。
- 会话内存态：MVP 不持久化聊天历史，重启即失；可接受（旁路验证场景）。
