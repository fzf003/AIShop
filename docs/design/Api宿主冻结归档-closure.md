# Api 宿主冻结归档 — 独立复核与需求收口

> 变更：`api-freeze-2026-10-05`
> 复核角色：**独立复核者（需求收口）** — 不采信既有 `test-report.md` 结论，全部重做。
> 复核日期：2026-10-05
> 唯一验收依据：`openspec/changes/api-freeze-2026-10-05/specs/hosting/spec.md`

**复核方法**：逐条读源码 → git 层核对（commit 文件集 / 祖先关系 / 目录历史）→ `--filter` 独立复跑替代用例 → **真机**启动 AppHost / Api 观察进程与端点。所有结论均由本次独立证据支撑。

---

## 1. 对账表（①）

| # | 需求（原话，需求文档 §2） | 认领变更 | 变更状态 | 报告说什么 | **复核结论（独立填）** |
|---|---|---|---|---|---|
| R1 | AppHost 不再启动 `api` 资源，只保留 `mcp` 与 `agui`；Api 仍需保留"可手动启动"的能力 | api-freeze-2026-10-05 | 已交付 | 达成 | **达成**（真机复核） |
| R2 | `src/AIShop.Api` 全量保留：不移除工程、不移出 sln、继续参与编译 | 同 | 已交付 | 达成 | **达成** |
| R3 | Api 及其编译依赖的老链代码，此后不得修改 | 同 | 已交付 | 达成（约束型） | **达成**（约束型；本变更未越界） |
| R4 | 删除 7 个 Api 集成测试（含 1 重复文件） | 同 | 已交付 | 达成 | **达成**（附一处**未报告**的覆盖差异，已裁决接受——见 §5） |
| R5 | 保留其余约 22 个非 Api 测试 | 同 | 已交付 | 达成 | **达成** |
| R6 | `aishop.db` / `aishop.rag.db` / `wwwroot/index.html` 保留 | 同 | 已交付 | 达成 | **达成** |
| R7 | 加冻结标记（csproj `<Description>` + `CLAUDE.md` + `AGENTS.md`） | 同 | 已交付 | 达成 | **达成** |

结论：认领账 R1–R7 全部"已交付"，复核 7/7 达成。

---

## 2. 独立复核结果（②）

### R1 — AppHost 不再启动 api 资源 + Api 仍可手动启动 → 达成

**(a) 静态（源码）** `src/AIShop.AppHost/Program.cs`：
- 第 6 行 `builder.AddProject<AIShop_McpServer>("mcp");`
- 第 9 行 `var host = builder.AddProject<AIShop_AguiHost>("agui").WithHttpHealthCheck("/health");`
- 全文件 grep `AddProject|AIShop_Api|"api"|ExcludeFromMcp` → **无任何 `"api"` 资源编排**。

**(b) 真机（场景 2）** 我亲自启动（先按 PID 清掉环境残留的上一次 AppHost 三进程 2924/31920/50300）：
```
dotnet run --project src/AIShop.AppHost --no-build   → 日志 "Distributed application started."
```
枚举实际拉起的进程（tasklist）：
```
AIShop.AppHost.exe    7584
AIShop.AguiHost.exe   13664
AIShop.McpServer.exe  50852
→ 无 AIShop.Api.exe
```
**仅 mcp + agui，无 api** —— R1 场景 2 独立复现。

**(c) 手动启动（R1 后半句）** `dotnet run --project src/AIShop.Api --no-build`：
```
Now listening on: http://localhost:5206
Application started. Press Ctrl+C to shut down.
Hosting environment: Development
Content root path: D:\Hermes\Projects\AIShop\src\AIShop.Api
GET /api/models → HTTP 200
```
测试后按 PID 精确 kill（`taskkill //F //PID 532`），`5206` 已空闲、无 `AIShop.Api.exe` 残留。

### R2 — Api 原位保留且继续编译 → 达成
- 目录 `src/AIShop.Api/` 存在。
- `AIShop.sln:8` 含工程 `AIShop.Api`（`src\AIShop.Api\AIShop.Api.csproj`）。
- `dotnet build AIShop.sln` → **0 警告 0 错误**（33.62s）；构建输出含 `AIShop.Api -> ...\src\AIShop.Api\bin\Debug\net10.0\AIShop.Api.dll`。
- 门禁真实性核实：`Directory.Build.props:6` `TreatWarningsAsErrors=true` → "0 警告"即"0 错误"。

### R3 — Api 及老链依赖冻结、此后不得修改 → 达成（约束型）

**本变更自身未触碰冻结面。** 7 个 commit 并集（`7bb5ce9/3801d34/a95a699/8d806a4/9b966a1/0e67bdd/883f07a`）涉及路径：
```
docs/design/*（2 需求文档/认领账）
.claude/agent-memory/*（4）
.gitignore
AGENTS.md
CLAUDE.md
Directory.Packages.props
src/AIShop.Api/AIShop.Api.csproj     ← 唯一的 Api 触碰，系 R7 明令的冻结标记
src/AIShop.AppHost/Program.cs
tests/AIShop.Api.Tests/（7 个被删文件）
```
- **不含**任何 `src/AIShop.Service/**` 老链文件（`ShoppingAssistantAgent.cs` / `ModelRouter.cs` / `Clients/` / `Providers/`）。
- 唯一 Api 触碰是 `AIShop.Api.csproj` 的 `<Description>`——R7 显式要求，属本变更的**被授权例外**（R3 语义为"此后(向未来)不得修改"）。

**工作区未提交改动**亦未越界：`M src/AIShop.Service/Agui/AGUIShoppingAgent.cs`（+1 行，提示词示例），位于 `Service/Agui/`——**新链**，显式不受 R3 约束。

**约束落地**：`CLAUDE.md:26` 与 `AGENTS.md:23` 均声明冻结清单 + `Agui/` 例外。

### R4 — 删除 7 个测试 + 不丢覆盖 → 达成（附一处未报告差异）

**子项 1：7 文件确实删除**
- 磁盘 7 文件全部 `MISSING`：`CartEndpointsTests.cs` / `ChatEndpointsWebTests.cs` / `ChatRecommendationsMergeTests.cs` / `ChatRecommendationMergeTests.cs` / `ChatReplySanitizationTests.cs` / `GlobalExceptionHandlerTests.cs` / `ProgramSeedingTests.cs`。
- `git show --stat 3801d34` = **恰好这 7 文件，3028 删行，无其它文件**。

**子项 2：替代覆盖是否真有区分力（核心，我逐类独立读 + `--filter` 复跑）**

| 被删逻辑 | 替代来源 | 我独立核实的区分力 | 独立复跑 |
|---|---|---|---|
| `ReplySanitizer` | `AguiHost.Tests/ReplySanitizingChatClientTests.cs` | **真**：该测试构造 `ReplySanitizingChatClient`（包 `AIShop.Core.Services.ReplySanitizer`），断言在册 id（`#3`/`商品ID为4`/`商品Id:4`）被清、名称价格保留 | 7 通过 |
| `RecommendationMerger` | `Api.Tests/RecommendationMergerTests.cs`(+`RecommendationServiceTests`) | **真**：`ShouldPrepend…/ShouldCapAtFive/ShouldDedupeIgnoringCase` 等直接断言合并结果 | 11 通过 |
| 启动播种 | `AguiHost.Tests/AguiStartupSeedingTests.cs` | **真**：断言 MigrateAsync 建库 + 幂等播种 18+8 商品 & 3 用户 + 独立 RAG 库生成 | 3 通过 |
| 购物车端点 | `AguiHost.Tests/AguiCartEndpointTests.cs` | **真**：AddCart/幂等键/合并数量/400/404 等 16 用例 | 16 通过 |
| 异常中间件 / 重试分类 | Api 独有 | 冻结后无需保护 | — |

替代来源独立复跑合计 **7+3+16+11 = 37 全绿**，与报告一致。

**独立发现的覆盖差异（报告未报）**：读被删 `ChatReplySanitizationTests.cs`（`git show 3801d34^:…`）发现，它还覆盖了替代测试**未覆盖**的行为：
- `ReplySanitizer.IsProductId` 的 **1..18 范围边界**——被删用例 `PostChat_ReplyWithOutOfRangeHashIds…` 断言 `#123456`/`#20`/`#19` **保留**、`#5`/`#18` 删除；替代测试只用 in-range id（`#3`/`#4`），**边界完全无覆盖**。
- `FixedIdPattern` 的 **小写 `商品id:N` 与中文冒号 `商品Id：N`** 变体；替代测试只覆盖大写+英文冒号的 `商品Id:4`。
- `/api/login` 历史清洗（Api 宿主专属，随 Api 冻结）。

全仓 grep（`#123456|#20|#18|商品id:|商品Id：|IsProductId|MaxProductId|超出范围`，`tests/**/*.cs`）**零命中** → 上述边界/变体确已无任何测试覆盖。裁决见 §5。

### R5 — 保留其余约 22 个测试 → 达成
- 目录余 **23 个 `.cs`** = 22 个测试文件 + `GlobalSuppressions.cs`（模块级压制声明，非测试）。
- `git log -- tests/AIShop.Api.Tests/`：本变更仅 `3801d34`（纯删除 7 文件）；`7bb5ce9`/`a95a699` 在该目录**零文件**。
- 逐文件核对其最后一次修改 commit，**全部早于本变更**（`e7b1886`/`e02a543`/`f5eb2e6`/`5034ab1`…），无一本变更引入。
- `git status --short tests/AIShop.Api.Tests/` = 空（无在途改动）。

### R6 — 数据与静态资源保留 → 达成
- 三文件存在：`aishop.db`(229376B, mtime 2026-10-02 22:28:59) / `aishop.rag.db`(2150400B, mtime 2026-10-05 17:36:24) / `wwwroot/index.html`(59752B)。
- **场景 2（AppHost 运行期不写老库）**：我的真机 AppHost 运行期间，两老库 mtime **均未变**（与运行前基线逐字节一致）。
- 附注：我做 R1 后半句时手动启动 Api，`aishop.rag.db` mtime 被更新至 19:34:38（RAG 索引预热）——不违反 R6 场景 2（其 Given 限定"AppHost 运行期间"）。

### R7 — 冻结标记 → 达成
- `src/AIShop.Api/AIShop.Api.csproj:42-43`：`<Description>【已冻结归档】AIShop 老链宿主…新增功能一律落 AIShop.AguiHost…</Description>`。
- `CLAUDE.md`：`:12-13` 依赖方向区分「Api 老链已冻结归档」/「AguiHost 现行主链」；`:16-18` src 树标注；`:25` 「Api 已冻结归档，新功能一律落 AguiHost」；`:26` R3 冻结约束。
- `AGENTS.md`：`:8` 基本分层「Api（已冻结归档）；现行主链…AguiHost」；`:13-15` src 树标注；`:22-23` 冻结陈述 + R3 约束。
- 两文档均已不再单独把 Api 呈现为当前主链。

### 全局门禁 → 达成
- `dotnet build AIShop.sln` → **0 错误 0 警告**（`TreatWarningsAsErrors=true` 已核）。
- `dotnet test AIShop.sln --no-build` → **592 通过 / 0 失败 / 0 跳过**（McpServer 11 · Api 113 · Service 238 · AguiHost 230）。

---

## 3. 查缺结果（③）

| 检查 | 结果 |
|---|---|
| 无人认领的需求 | **无**（R1–R7 全部在认领账内登记） |
| 认领未交付 | **无**（认领账 7 条均"已交付"，复核证实） |
| 交付未落地 | **无**（7 commit 均为 HEAD(`883f07a`) 祖先，核心文件工作区干净） |
| 交付未归档 | **变更尚未 archive**：`openspec/changes/api-freeze-2026-10-05/` 仍在 `changes/` 下（未移入 `archive/`）。此为**正常的下一个流程步骤**（`/openspec-archive-change`），非缺口。需求文档状态已标"已完成"。 |

**结论：无未结局条目。**

---

## 4. 整体验证（④，含跨变更/横向）

本变更性质为"清理与标注"，只动 AppHost 编排 + Api 侧 + 测试删除 + 文档。横向影响面必须真机确认，不得推断：

1. **新链 AguiHost 零影响（真机）**：AppHost 真机启动后，AguiHost 起于 `49676`；`GET /health`→200、`/models`→200、`/products`→200。7 commit 并集**未含** `AguiHost`/`McpServer`/`Core`/`Infrastructure` 任何文件。
2. **老链 Api 仍可启动（真机）**：5206 监听 + `/api/models`→200（R1 后半句，见上）。
3. **两库隔离**：AguiHost 运行只写自身 `src/AIShop.AguiHost/agui*.db`；AppHost 运行期老库 `aishop*.db` 零触碰（mtime 未变）。
4. **无回归**：全量 592/592 全绿（工作区含邻接新链改动 `AGUIShoppingAgent.cs`）。

---

## 5. 缺口清单与裁决（⑤）

> 规则：每条缺口只允许 **要修**（报告编排方开工单）或 **明确接受**（附理由）。以下**无第三态**。

| # | 缺口 / 观察 | 裁决 | 理由 |
|---|---|---|---|
| G1 | **R4 覆盖差异**：替代测试 `ReplySanitizingChatClientTests` 未覆盖 `ReplySanitizer` 的 1..18 范围边界（`#123456`/`#20` 等保留）及小写 `商品id:` / 中文冒号 `商品Id：` 变体；全仓无其它测试覆盖。 | **明确接受** | ① spec R4「不丢覆盖」Then 字面已满足——4 个替代来源存在且绿，第 5 项（异常中间件）Api-only 已接受；② 需求文档 premise 措辞为"**核心逻辑**别处均有覆盖"——ReplySanitizer 的核心行为（清除在册 id、保留名称/价格）确由替代测试覆盖；③ 边界/变体系健壮性边缘，其唯一生产者（冻结的 Api SSE/PostChat 可能吐出"订单号 #123456"）随 Api 冻结不再运行，materiality 低；④ `ReplySanitizer` 属 Core（未冻结），若未来需要补边界用例可另案，不属本冻结变更范围。**报告"覆盖缺口：无"应补注此差异**（见 §7）。 |
| G2 | **R6 措辞张力**：手动启动 Api 会更新 `aishop.rag.db` mtime（RAG 预热）。 | **明确接受** | R6 场景 2 的 Given 限定"**AppHost 运行期间**"，手动启动不属该前提；两库仍保留、未被删除（场景 1 满足）。 |
| G3 | **R3 落地形式**：冻结约束无 CI 级自动门禁，靠文档声明 + 评审。 | **明确接受** | R3 为约束型需求（design §4.4 明示"文档声明 + 评审约定"），design 已登记不引入 CI 守卫（超范围）。 |
| G4 | **AppHost.csproj 仍保留对 Api 的 `ProjectReference`**（`AIShop.AppHost.csproj:19`）。 | **明确接受** | 删资源后不构成悬空引用告警（build 0 警为证），T1 有意的最小改动；design §4.1 已判据。 |
| G5 | **T3 commit `a95a699` 混入会话前 `AGENTS.md` 既有未提交改动**。 | **明确接受** | 报告记用户 2026-10-05 已裁决"接受、不改写历史"；我核实该既有 hunk 完好存在，未回退。 |
| G6 | **范围外**：`AGENTS.md` 底部 `.codex/skills/maf-reference/` 过时路径、`docs/agent-journey-best-practices.md` 引用悬空。 | **明确接受** | design §4.3 明示超出 R7 范围；与 R1–R7 无因果，属另案。 |

**要修项：无。**

---

## 6. 独立发现：与 `test-report.md` 不一致之处

1. **R4 覆盖结论**：报告称"覆盖缺口：**无**"。复核发现一处**未报告的覆盖差异**（G1：ReplySanitizer 的 1..18 边界与小写/中文冒号 fixed-format 变体在替代测试中无覆盖）。裁决为**接受**（理由见 §5），但报告"无缺口"的结论应补注该差异。
2. **DevUI 包归属**：报告将 `Directory.Packages.props` 的 DevUI 升级（`1.20.0-preview.260831.1` → `1.22.0-preview.260918.1`）列为"**非本变更的未提交改动 / 编译影响面**"，并据此声明"全绿 = 工作区绿，未做提交态对照"。复核：该升级**已作为 commit `8d806a4` 落库**（本次 7 commit 之一），故因它而起的"提交态↔工作区"漂移顾虑已消解。
3. 报告的其余核心结论（R1–R7 达成、592 全绿、37 替代来源全绿、运行期老库零写入、AppHost 无 Api 子进程）**经我独立复现，全部成立**。

> 注：报告"工作区绿"判断本身仍成立——工作区另有邻接新链未提交改动 `src/AIShop.Service/Agui/AGUIShoppingAgent.cs`（Service/Agui 新链提示词，本变更无关），我复核的 592 全绿即含该改动后的工作区。

---

## 7. 结论

**收口通过（PASS）。**

- 需求 R1–R7 **7/7 达成**，且均由本次独立证据（源码 / git / 真机）支撑。
- 全局门禁：`dotnet build` 0 错 0 警（`TreatWarningsAsErrors` 已核）、`dotnet test` **592/592 全绿**。
- R4 替代覆盖**确有区分力**（37 用例独立 `--filter` 复跑全绿，逐类读过本体）。
- 无"要修"项；6 条缺口/观察**全部明确接受**（G1 为本次唯一新增发现的覆盖差异，接受理由为 spec 字面满足 + materiality 低）。
- 报告与复核的**唯一实质不一致**是 R4 的覆盖结论（"无缺口" vs 一处已接受差异）与 DevUI 包已落库这一事实澄清——均不改变"收口通过"的总判定。

**后续（非缺口，属正常流程）**：变更归档 `/openspec-archive-change api-freeze-2026-10-05`。
