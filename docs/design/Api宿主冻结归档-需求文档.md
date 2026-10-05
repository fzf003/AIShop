# Api 宿主冻结归档 — 需求文档

> 项目：AIShop 智能购物助手
> 版本：v1 | 日期：2026-10-05
> 状态：待实现

---

## 1. 概述

当前仓库同时运行两条业务链：

| | 老链 `AIShop.Api` | 新链 `AIShop.AguiHost` |
|---|---|---|
| 聊天 | 手写 SSE `/api/chat` | AG-UI SSE `/` |
| 购物车 | `/api/cart/{username}`（6 端点） | `/cart?username=`（5 端点） |
| 商品 | `{products:[…]}` | 裸数组 |
| 错误体 | `{"error":"用户不存在"}` | `{"detail":"User not found"}` |
| 前端 | `wwwroot/index.html` | `src/AIShop.Web`（React） |
| 业务库 | `aishop.db` | `agui.db` |

两条链功能重叠：REST 契约已经漂移，用户 / 购物车数据互不互通。

**决策依据**：Api 无真实使用方，且今后不再对其增改。

**本需求的目标**：把这个"冻结归档"的决策落成可执行条目，并消除由此产生的不一致。

---

## 2. 需求条目

### R1 运行时停用

AppHost 不再启动 `api` 资源，只保留 `mcp` 与 `agui`。

- Api 仍需保留"可手动启动"的能力（`dotnet run --project src/AIShop.Api`）

### R2 代码原位保留

`src/AIShop.Api` 全量保留：不移除工程、不移出 sln、**继续参与编译**。

### R3 冻结约束

Api 及其编译依赖的老链代码，此后不得修改：

- `src/AIShop.Api/**`
- `src/AIShop.Service` 的老链部分：`ShoppingAssistantAgent`、`ModelRouter`、`Clients/`、`Providers/`

**约束原因**：Api 要求"保留且可编译"，而它编译依赖上述代码。改任何一处都可能让 Api 编译失败——这等于把 Service 的老链部分一并冻结。

> 这是本需求**最重要的连带影响**：Service 将长期保持"老链 + 新链"混装。新链部分（`Service/Agui/`）不受此约束，仍可调整。

### R4 删除 Api 集成测试

`tests/AIShop.Api.Tests` 中，以下 7 个文件测的是 Api 宿主行为，随 Api 冻结一并删除：

| 文件 | 内容 |
|---|---|
| `CartEndpointsTests.cs` | 购物车端点（14 用例） |
| `ChatEndpointsWebTests.cs` | chat / login / models / 重试 / 流式（30 用例） |
| `ChatRecommendationsMergeTests.cs` | 推荐合并（10 用例） |
| `ChatRecommendationMergeTests.cs` | **与上者重复的旧版**（2 用例） |
| `ChatReplySanitizationTests.cs` | 回复清洗（8 用例） |
| `GlobalExceptionHandlerTests.cs` | 异常中间件（4 用例） |
| `ProgramSeedingTests.cs` | 启动播种（4 用例） |

**前提（已核查）**：这些测试顺带覆盖的核心逻辑，别处均有覆盖，删除不丢覆盖：

| 被覆盖的逻辑 | 别处来源 |
|---|---|
| 回复清洗 `ReplySanitizer` | `AguiHost.Tests/ReplySanitizingChatClientTests` |
| 推荐合并 `RecommendationMerger` | 同工程 `RecommendationMergerTests`（保留） |
| 启动播种 | `AguiHost.Tests/AguiStartupSeedingTests` |
| 购物车端点 | `AguiHost.Tests/AguiCartEndpointTests` |
| 异常中间件 / 重试分类 | Api 独有，冻结后无需保护 |

### R5 保留非 Api 测试

`tests/AIShop.Api.Tests` 其余约 22 个测试文件**保留**——它们实际测的是 Core / Infrastructure / AgentTelemetry / ServiceDefaults 的活代码。

工程名与实际内容不符属既有的命名错配，**不在本需求范围**。

### R6 数据与静态资源保留

- `aishop.db` / `aishop.rag.db`：保留（停止写入，成为历史快照）
- `src/AIShop.Api/wwwroot/index.html`：原位保留

### R7 冻结标记

为防后续会话（人或 Agent）把 Api 误当现行路径，须加显式标记：

- `src/AIShop.Api/AIShop.Api.csproj` 加 `<Description>` 标注冻结状态，以及
- `CLAUDE.md` / `AGENTS.md` 写明"Api 已冻结归档，新功能一律落 AguiHost"

> 现状反例：现有文档声称架构为 "Core → Infrastructure → Api" 三层，未提 AguiHost——每个新会话都要重新辨析哪条是主链。

---

## 3. 改动范围

### 涉及

| 对象 | 操作 |
|---|---|
| `src/AIShop.AppHost/Program.cs` | 移除 api 资源 |
| `tests/AIShop.Api.Tests/` 的 7 个文件 | 删除 |
| `src/AIShop.Api/AIShop.Api.csproj` | 加冻结说明 |
| `CLAUDE.md` / `AGENTS.md` | 写明冻结状态 |

### 不涉及

- 不删 `src/AIShop.Api` 工程
- 不删 `aishop.db` / `aishop.rag.db`
- 不删 `wwwroot/index.html`
- 不改 `tests/AIShop.Api.Tests` 的其余 22 个文件
- 不改 `Service` 老链代码
- 不做 `Api.Tests` 工程改名 / 拆分（另案）
- 不做 Api 与新链的契约统一（Api 冻结，无意义）

---

## 4. 验收标准

- [ ] AppHost 启动后只有 `mcp` 与 `agui` 两个资源
- [ ] `dotnet build` 0 错误 0 警告（Api 仍在 sln 内且可编译）
- [ ] `dotnet test` 全绿（删除 7 个文件后无失败）
- [ ] `aishop.db` / `aishop.rag.db` 仍在磁盘上
- [ ] `wwwroot/index.html` 仍在
- [ ] Api 冻结标记已就位（csproj + 文档）
- [ ] `dotnet run --project src/AIShop.Api` 仍能启动

---

## 5. 待确认

| # | 项 | 说明 |
|---|---|---|
| 1 | R7 冻结标记的形式 | **已定**：csproj `<Description>` + `CLAUDE.md` + `AGENTS.md`（2026-10-05 裁决） |
| 2 | 需求文档与认领账的存放位置 | **已定**：`docs/design/` 移出 `.gitignore`，需求文档与认领账纳入版本控制（2026-10-05 裁决） |
