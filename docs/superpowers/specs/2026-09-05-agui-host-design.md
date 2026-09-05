# AGUI Host — 独立购物 Agent 宿主 设计

> 日期：2026-09-05
> 状态：设计已确认，待走 OpenSpec/writing-plans 实施
> 框架：net10.0 / C# 14 | MAF 1.20
> 关联：`docs/design/agent-evolution-design.md`、`docs/superpowers/specs/2026-07-05-harness-agent-migration-design.md`

---

## 1. 背景与目标

AIShop 现有对话能力由 `ShoppingAssistantAgent`（HarnessAgent 外壳）+ `AIShop.Api` 手写 SSE 端点 + 自制 `index.html` 承载。本项目要验证 **MAF 1.20 官方标准宿主**这条下一代接入路径：基于 AG-UI 协议搭建一个**独立新 Host**，对外暴露购物 Agent 能力，UI 走 AG-UI 标准（不再沿用自制 UI），为将来 UI 演进/DevUI 铺路。

**目标**：`src/AIShop.AguiHost` 上线后，一条"推荐→加购→查车"的对话能经 AG-UI 协议流式跑通，**复用现有 Service/Infrastructure 底座能力**，老功能零影响。

## 2. 硬约束（用户拍板）

1. `ShoppingAssistantAgent` 与老 `AIShop.Api` 链路**冻结零改动**（不改、不重构、不迁移其内部实现）。
2. 新购物助手 Agent **从零重新设计**为 ChatClientAgent 形态（AG-UI 官方宿主需要消息数组驱动），不迁移旧外壳。
3. **复用底座**：依赖链 `AguiHost → AIShop.Service → AIShop.Infrastructure → AIShop.Core`。AguiHost **直接引用 Service**，沿既有引用链（Service 已引 Infrastructure、Infrastructure 引 Core）传递获得全部底座类型；不孤立自建实现。
4. **数据隔离**：复用 Infra 仓储但连**独立 SQLite**（`agui.db`），老库零接触。
5. 新 Agent / AGUI 装配等新代码集中在 AguiHost 内独立文件夹；需要改动老代码时**单独变更、隔离回归**（本设计 `ShoppingAssistantAgent` 零 diff）。

## 3. MAF 1.20 AG-UI 事实快查（官方源码，非过时 skill）

来源：codebase-memory 本地索引 `E-github-ProActor-aspire13app-agent-framework`（`dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`）。

- 装配：`builder.Services.AddAGUIServer()`；端点：`app.MapAGUIServer("/", agent)`，其中 `agent` 是 `AIAgent`（`chatClient.AsAIAgent(name, instructions)` 的 ChatClientAgent 形态），或 `MapAGUIServer(agentName, pattern)`（keyed DI 解析）。
- 传输：客户端 `POST RunAgentInput` → 服务端 **SSE 事件流**（`TypedResults.ServerSentEvents` / net10 原生）。事件经 `AsAGUIEventStreamAsync` 由 `ChatResponseUpdate` 流转换。
- 会话：`AgentSessionStore` 跨请求持久会话，默认 ephemeral；多用户须 `UseClaimsBasedAgentIsolation`（`Microsoft.Agents.AI.Hosting.AspNetCore`）+ `WithInMemorySessionStore`。ThreadId = 会话续接键（来自 wire，非授权令牌）。
- 宿主包装：`AIHostAgent(agent, sessionStore)` 提供 `GetOrCreateSessionAsync` / `RunStreamingAsync(messages, session)` / `SaveSessionAsync`。
- 官方样例（本地镜像）：`dotnet/samples/02-agents/AGUI`（Step01 GettingStarted → Step05 StateManagement，每步 Server+Client）；`05-end-to-end/AGUIClientServer`。
- 客户端：`Microsoft.Agents.AI.AGUI`（`AGUIChatClient`）。DevUI：进程内 `builder.AddDevUI()`（`Microsoft.Agents.AI.DevUI`）+ Aspire 资源 `Aspire.Hosting.AgentFramework.DevUI.AddDevUI`。

> ⚠️ 本地 `maf-reference` skill 参考停在 MAF 1.10，不作为本设计 API 依据；查最新以本地镜像源码为准。

## 4. 总架构

```
新项目 src/AIShop.AguiHost（ASP.NET Core，ProjectReference → AIShop.Service，传递获 Infra/Core）
│
├─ AddSerilog / AddAGUIServer()
├─ 注册（复用底座扩展，均来自既有 Infra/Service，仅用独立连接串）：
│     AddInfrastructure("Data Source=agui.db")      ← EF 仓储 + AppDbContext（独立库）
│     AddMemoryService()                            ← Mem0 记忆（可选挂载）
│     AddRagService()                               ← RAG 语义检索（search_product 用）
│     AddSingleton<CartToolProvider> / <ModelRouter>
│     AddSingleton<IChatClient>(sp => sp.GetRequiredService<ModelRouter>().GetDefaultChatClient())
│     AgentTelemetryOptions 绑定（同 Api/Program.cs）
├─ 新购物 Agent（AguiHost/Agents/ 内，ChatClientAgent）：
│     ModelRouter.GetDefaultChatClient().AsAIAgent("AGUIShopping", instructions)
│         .WithTools(CartToolProvider.CreateTools())     ← 5 购物工具复用
│     （可挂记忆/历史 Provider；会话由 AG-UI AgentSessionStore 管理）
├─ app.MapAGUIServer("/", agent)
│
└─ AppHost：AddProject<AguiHost>("agui")
      消费者（MVP）：官方 AGUIClient（Microsoft.Agents.AI.AGUI）
      后续：DevUI 面板 / Aspire DevUI 资源
```

依赖方向：

```
AIShop.Core  ←  AIShop.Infrastructure  ←  AIShop.Service  ←  AIShop.AguiHost
(纯领域)        (EF/仓储/RAG/Mem0)        (Agent/ModelRouter)  (AGUI 宿主+新 Agent，最外层)
```

AguiHost 直接引用 Service（传递覆盖 Infra/Core）；不修改任何现有项目文件。

## 5. 切分：复用底座 vs AguiHost 自建

| 能力 | 来源 | AguiHost 工作 |
|---|---|---|
| 模型管道 / 多模型 | `Service/ModelRouter`（DeepSeek/清洗中间件链） | `GetDefaultChatClient()` 直接喂新 Agent |
| 商品/购物车 EF 仓储 | `Infrastructure`（Core 接口契约：`ICartRepository`/`IProductCatalogService`） | 仅换连接串 `agui.db` |
| 商品搜索（语义 RAG） | `Infrastructure`（`AddRagService`，bge ONNX） | search_product 复用，需播种+预热索引 |
| 记忆（Mem0） | `Infrastructure`（`AddMemoryService`）+ `Service/Providers` | 可选挂新 Agent |
| 5 购物工具 | `Service/Tools/CartToolProvider` | `.WithTools(CartToolProvider.CreateTools())` 复用 |
| AGUI 宿主 / 新 Agent / 装配 | 无（新建） | `AguiHost/Program.cs` + `AguiHost/Agents/` |

**AguiHost 自建内容仅**：AG-UI 装配、新购物 Agent 定义与人设 Instructions、username 适配（AGUI forwarded metadata → `ICurrentUserAccessor` 或工具上下文）。不重复实现仓储/搜索/记忆/工具。

## 6. 数据与存储

- **独立 SQLite `agui.db`**（老 `aishop.db` / `aishop.rag.db` 不受影响）；复用 Infra `AppDbContext` + EF Migrations（对齐宿主 MigrateAsync 约定，勿用 EnsureCreated）。
- 播种（同老 Seed 语义）：商品（`ProductSeedData`）+ 测试用户 marla/steve/fzf003。
- 语义检索预热：`EnsureIndexedAsync` 对 `agui.db` 商品建向量索引（失败仅 Warning，懒构建兜底——同 Api/Program.cs）。
- MVP 聊天历史持久不强制落库（AG-UI 会话/内存承载）；如需可后续接 `IChatHistoryStore`。

## 7. 新购物 Agent 设计（AguiHost/Agents/AGUIShoppingAgent.cs）

- **形态**：`AsAIAgent("AGUIShopping", instructions)`（ChatClientAgent）+ `.WithTools(...)`；会话由 AG-UI `AgentSessionStore`（ThreadId）管理，无自管 session 字典、无 `StateBag` 偏好注入。
- **模型**：来自 `ModelRouter` 的 chatClient（默认/指定模型）；多模型能力随底座可用（是否暴露 UI 切换属后续）。
- **工具**：复用 `CartToolProvider.CreateTools()`（search_product/add_to_cart/update_cart_quantity/get_cart_summary/remove_from_cart），行为与老 Agent 同源。
- **人设/回复**：按 AG-UI 对话语境设计——自然语言为主、工具驱动；不复用旧 `Reply+Keywords+Preferences` JSON 协议（那是面向旧前端+偏好记忆的协议）。
- **记忆**：可选挂 Mem0 `MemoryContextProvider`（复用）；默认 MVP 可不挂（AG-UI 会话上下文足够）。
- **用户身份**：AGUI `RunAgentInput` forwarded metadata（`username`）→ `ICurrentUserAccessor`（工具经其取当前用户，同 ShoppingAssistantAgent 的 `SetCurrentUser` 语义）；缺省 `guest`。

## 8. 非目标（明确排除，避免范围蔓延）

- 不迁移、不修改 `ShoppingAssistantAgent`；不把旧 Agent 的 HarnessAgent 装配复制到 AguiHost。
- AG-UI Step02-05 高级能力（HITL 人工审批、前端/后端工具渲染、共享状态/预测式更新、DevUI 面板、Aspire DevUI 资源）= **后续路线**，不进首版。
- 首版不含 AGUI 专属的用户画像记忆（如复用 Mem0 则只做会话记忆的可用验证）。
- 不触碰 `AIShop.Api` / `AIShop.McpServer` 任何端点与配置；不迁移聊天历史落库。

## 9. 验收标准（可测）

1. `dotnet build AIShop.sln`（含新 AguiHost）0 警告 0 错误
2. 起 AguiHost：官方 AGUIClient 发 `"推荐跑步鞋"` → **SSE 流式文本 + 触发 `search_product`**（语义检索命中商品，无崩溃）
3. 对话 `"把第 1 个加购物车"` → `add_to_cart` 生效；`get_cart_summary` 反映（SQLite `agui.db` 可见）
4. **回归零影响**：老测试全绿 + `git diff -- src/AIShop.Service/ShoppingAssistantAgent.cs` 为空
5. 数据隔离：`agui.db` 独立生成，老 `aishop.db` / `aishop.rag.db` 无变化

## 10. 后续路线（不进首版）

AG-UI Step02 工具渲染 → Step03 前端工具 → Step04 HITL → Step05 状态管理 → DevUI 面板 / Aspire DevUI 资源 → 多模型 UI 切换 → 聊天历史落库 / 记忆画像。

## 11. 风险与取舍

- **复用底座的新风险**：AguiHost 与老世界共享 Infra/Service，底座变更会传导编译影响；以 `ShoppingAssistantAgent` 零 diff + 独立连接串作为回归护栏。
- AG-UI 为 preview 包（1.20.0-preview.*）：API 可能小步变动，实施时以本地镜像源码为准。
- 记忆/历史 MVP 从简（内存会话）；首版目标是"宿主 + 完整购物链路"跑通。
- 复用而非自建 → 依赖完整性靠传递引用；实现时应显式确认 Service 引用链满足所需类型（必要时按需补直接引用，仍遵循第 2 节依赖方向）。
