# chat_messages 轮次边界设计（run_id）

> 状态：已定稿（2026-08-18 决策项全部确认，待走 OpenSpec 变更流程进入实现）
> 日期：2026-08-17（2026-08-18 决策回填）
> 变更名：`chat-round-boundary`（OpenSpec 变更已建目录，待设计定稿后再走流程）

## 1. 背景与问题

`chat_messages` 表当前**只有消息粒度，没有「轮次」抽象**。一行 = 一条带 role 的独立消息（user / assistant / tool / system），一次完整交互会写入多行（例如调工具时：user → assistant(FCC) → tool → assistant），且一次 Agent Run 的多个 FICC 迭代会分多次 Store 追加。这带来三个问题：

### 问题 1：压缩时会把一轮对话拆散

当前压缩是「按消息条数硬切」：保留每个 session 最新的 50 条消息，更旧的标记 `is_compacted=true`（`SqliteChatHistoryProvider.StoreChatHistoryAsync`）。

- 窗口按**条数**切，不感知轮次，边界可能落在某一轮的中间
- 当前只保护一个边界：压缩区第一条若为 `assistant(FCC)` 则连带压缩其 `tool`；若为 `tool` 则把前一条 `assistant(FCC)` 移出压缩区（保留）
- 结果：FCC↔tool **配对**不会断，但**一轮**可能被劈开——例如边界落在 `user` 与 `assistant` 之间时，`user` 被压掉、`assistant` 留下，保留区历史从一句孤零零的回复开始，语义不完整

### 问题 2：无法在数据层判断「一轮是否完成」

表里没有 round / turn / is_final 字段。判断轮次完成只能靠运行时（`await RunChatAsync` 返回），数据层只能靠推断（最后一条是纯文本 assistant），且当 `MaximumIterationsPerRequest=3` 耗尽时，最后一条是带 tool_calls 的 assistant，连推断都失效。

### 问题 3：孤儿 tool 配对检查依赖「相邻 id」推断

当前配对保护用 `Id == firstCompressId ± 1` 判断相邻关系。历史遗留的碎片数据（双重 FICC 产生的重复消息、孤儿 tool）会让相邻 id 并不是配对的另一半，保护可能失效，带内容的孤儿 tool 仍会进上下文。

## 2. 目标

1. 数据层能**识别轮次边界**：同一轮的所有消息可归组
2. **压缩不拆轮**：压缩操作只切在轮与轮之间，或整体保留未完成轮
3. 数据层可**判断轮次完成**：新增显式 `is_final` 完成标记，不再依赖运行时推断
4. 修复孤儿 tool 的边角风险：配对检查从「相邻 id」升级为「轮次组内配对」

## 3. 方案设计

核心：给 `chat_messages` 表新增一个 **`run_id`** 列（Guid），**一次 `RunChatAsync` 产生的所有消息共享同一个值**。整轮 = 同一 `run_id` 的一组行。

### 3.1 数据模型变更

| 变更 | 说明 |
|---|---|
| `ChatMessageRecord` 新增 `RunId: Guid?` | 轮次标识，可空，兼容存量数据 |
| 列名 | `run_id`（TEXT，SQLite 存 Guid 字符串） |
| `ChatMessageRecord` 新增 `IsFinal: bool` | 轮次完成标记，默认 false |
| 列名 | `is_final`（INTEGER 0/1） |
| 索引 | 建议新增组合索引 `(session_id, is_compacted, run_id, id)`，或复用现有 `idx_cm_session_active` 扩展 run_id 维度 |

`id` 仍作为组内消息顺序锚点，`run_id` 只负责**分组识别轮次**，`is_final` 只负责**标记轮次终点**，均不承担排序。

两个字段正交互补：
- `run_id`：回答「这条消息属于哪一轮」
- `is_final`：回答「这一轮是否正常收尾」——该轮存在一行 `is_final=true` 即视为完成

### 3.2 写入时机：RunChatAsync 生成，StateBag 传递

关键约束：一次 Agent Run 会触发 **N 次 Store**（N = FICC 迭代次数），N 次 Store 必须写到**同一个 run_id**。由于 `Session.StateBag` 已在存 `SessionId`，照此办理：

```csharp
// ShoppingAssistantAgent.RunChatAsync —— 每轮开始生成一次
var runId = Guid.NewGuid();
session.StateBag.SetValue("RunId", runId.ToString());

// SqliteChatHistoryProvider.StoreChatHistoryAsync —— 所有迭代读到同一个值
var runId = context.Session.StateBag.TryGetValue<string>("RunId", out var rid, null)
    ? Guid.Parse(rid)
    : Guid.NewGuid();   // 兜底：无 RunId 时退化为当前行为

db.ChatMessageRecords.Add(new ChatMessageRecord { ..., RunId = runId });
```

- 为什么放 StateBag 而不是 DB 计数器：Store 每次新开 DbContext（`IDbContextFactory`），会话级计数器要读上一轮再自增，多轮迭代下易错；StateBag 由框架在同一 Session 生命周期内可靠共享
- 兜底分支保证：即使某条路径未写 StateBag，也不会崩溃，只是该行 run_id 独立（视为单行轮次）

### 3.3 压缩算法：按轮整切

**现状**（`StoreChatHistoryAsync` 步骤 2）：

```
未压缩消息按 id 降序 → 跳过最新 50 条 → 剩余标记 is_compacted=true
```

**改为**（决策 ④：按「最近 K 个完整轮次 + 条数硬上限」切）：

```
1. 按 run_id 分组未压缩消息（run_id 为 NULL 的历史行，每行自成一组，走旧行为）
2. 从最旧的完整轮开始，整轮整轮标记 is_compacted=true
3. 直到剩余完整轮数 ≤ K 停止（K=12：纯文本轮仅 24 条、含工具轮约 48 条；见下方「K 值推导」）
4. 未完成轮（组内**无 `is_final=true` 行**）整组保留，即使它很旧
5. 安全阀：单轮极大或未完成轮堆积时，用「条数硬上限」（如 4K 条）与「未完成轮上限」（5 个）兜底，超限强制压最旧并记 Warning（决策 ②）
```

- **配对保护天然成立**：整轮一起压，FCC↔tool 永不分离，删除 `compressIds` 的 ±1 相邻推断逻辑
- **K 与硬上限的分工**：K 决定业务保留多少轮；硬上限是存储安全阀，防单轮巨大或异常堆积导致膨胀
- **K 值推导（评审 B1 修正）**：目标容量约等于现状的 50 条——纯文本轮 2 行/轮 → K=25；含一次工具 4 行/轮 → K≈12。取 K=12 偏保守（存储更省），若要贴近 50 条可改 K=16
- **未完成轮计数口径（评审 Y2）**：只统计 `run_id` 非空且 ≥2 行的组；`run_id=NULL` 的历史行不计入未完成轮（否则历史库瞬间超上限触发误压缩），它们走旧行为

### 3.4 轮次完成判断：显式 `is_final` 字段

不再靠推断，用显式字段。`is_final` 标记该行的「轮次终点」身份，查询一轮是否完成：

```csharp
var done = await db.ChatMessageRecords
    .AnyAsync(m => m.RunId == runId && m.IsFinal);
// true  → 该轮正常完成（存在明确的轮次终点行——通常为最终回复，仅调工具场景可为 tool/FCC 行）
// false → 未完成轮（FICC 迭代耗尽 / 异常中断 / 仅调工具未输出文本）
```

**写入时机**（决策 ⑥：Store 判定为主 + Run 后兜底补标）：
1. **Store 内判定**：本轮增量消息的最后一条若是「纯文本 assistant」（无 tool_calls），即 FICC 迭代结束信号，将其 `IsFinal=true` 落库（一次写，覆盖绝大多数场景）
2. **Run 后兜底补标**：`RunChatAsync` 正常返回后，若本轮仍无 `is_final=true` 行（如 Qwen「只调工具未输出文本」），把本轮最后一条消息补标 `is_final=true`。**补标可能落在 `tool`/`assistant(FCC)` 行上**——此时该行是「轮次终点」但不是「最终回复」，语义以「终点标记」为准（评审 Y1）
3. `MaximumIterationsPerRequest` 耗尽或异常中断时该轮无 `is_final=true` 行，天然表示未完成

> 职责边界（评审 Y3）：补标操作收敛在 `SqliteChatHistoryProvider`，由它暴露 `MarkRoundFinalAsync(runId)` 方法供 `ShoppingAssistantAgent` 调用——避免 Agent 直接操作 DbContext 给 `chat_messages` 引入第三个写入口（现状写入口：Provider.Store / 旧仓储 SaveChangesAsync）

相比「最后一条消息推断」的优势：
- 不依赖「最后一条恰好是纯文本 assistant」的假设——即使响应含多条文本或模型只调工具未输出，显式标记语义依然明确
- 压缩、日志、前端可直接查 `is_final`，无需扫描整组判断

### 3.5 加载（Provide）适配

- **加载顺序不变**：仍按 `(session_id, is_compacted=0)` 按 `id` 升序全量读取，保证行序 = 时序，配对重放不受影响
- **孤儿 tool 检查升级**：不再是「前一条相邻 id 是否为 assistant-FCC」，而是「**同 run_id 组内**是否存在对应的 assistant-FCC」；不存在则过滤该 tool 行。比相邻 id 推断可靠，能吸收历史碎片。**`run_id=NULL` 的历史 tool 行无组可查，退化为旧的相邻 id 检查**（评审 G1，兼容）

### 3.6 存量数据兼容

历史行 `run_id` 为 NULL（决策 ③：代码兼容 + 部署清库）：

- **代码层面按兼容实现（防御）**：NULL 行每行自成一组，压缩按旧行为处理，Provide 按 id 排序不受影响
- **部署时清库重建**：`docs/issues-multi-model-switch.md` P6 本就建议上线清库，且历史含旧 bug 碎片数据；全新库 run_id 全量有效，逻辑最干净

**schema 迁移（关键前提，评审 B2）**：项目无 migrations，`EnsureCreatedAsync` 只建**不存在的表**、不给已有表加列；`Program.cs` 兜底 DDL 的 `CREATE TABLE IF NOT EXISTS` 对已存在的 `chat_messages` 同样不补列。因此**旧 schema 库带新代码启动会因缺 `run_id`/`is_final` 列而 `SqliteException`**——「代码兼容」仅指 **NULL 行数据兼容，不含 schema 兼容**。进实现前二选一：

| 方案 | 做法 | 适用 |
|---|---|---|
| A. 幂等补列（推荐） | 启动时 `PRAGMA table_info(chat_messages)` 检查列存在，缺失则 `ALTER TABLE chat_messages ADD COLUMN run_id TEXT` / `ADD COLUMN is_final INTEGER NOT NULL DEFAULT 0`（SQLite 无 `ADD COLUMN IF NOT EXISTS`，须先查 PRAGMA） | 想保留旧库继续用 |
| B. 强制清库 | 明确「旧 schema 库必须先清库或手工迁移，否则启动即崩」 | 接受 P6 清库建议，正式环境干净 |

选 A 与「代码兼容」承诺一致（任何人带旧库也能跑），实现成本低（一段幂等 DDL）。

## 4. 边界情况

| 场景 | 处理 |
|---|---|
| `MaximumIterationsPerRequest=3` 耗尽 | 该轮无纯文本终点 → 识别为未完成轮，压缩时整组保留；受「未完成轮上限（5 个）」约束，超限强制压最旧并记 Warning |
| 跨模型切换继续对话 | 同一 sessionId 追加新 run_id 轮，无冲突 |
| FICC 中间态多次 Store | 多次 Store 共享同一 run_id（StateBag），组内行仍按 id 有序 |
| run_id 兜底生成 | 未写入 StateBag 时每行独立 run_id，行为退化到现状，不崩溃 |
| 并发 | 当前同一 session 串行对话（RunChatAsync 一次性完成），run_id 由每次 Run 独立生成，无冲突；若未来并发需显式加锁 |

## 5. 影响面

| 层 | 文件 | 变更 |
|---|---|---|
| Core | `src/AIShop.Core/Entities/ChatEntities.cs` | **不变**（`ChatMessage` 的 `ContentsJson`/`SourceType` 遗留字段保持现状，本次不触碰） |
| Infrastructure | `src/AIShop.Infrastructure/Entities/ChatMessageRecord.cs` | 新增 `RunId` + `IsFinal` 属性 |
| Infrastructure | `src/AIShop.Infrastructure/Data/AppDbContext.cs` | 列映射 + 索引（顺带清理 §5 注的重叠配置块） |
| Api | `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` | `RunChatAsync` 生成 RunId 写 StateBag；返回后兜底补标 is_final |
| Api | `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs` | Store 打标（run_id + is_final）；压缩按轮整切（K=12 + 硬上限 + 未完成轮上限）；Provide 孤儿检查升级 |
| Tests | `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs` 等 | 压缩断言从条数改轮次；新增完成判断、不拆轮、未完成轮上限测试 |
| 数据 | SQLite DB | 新增 `run_id` + `is_final` 列（全新库 EnsureCreated 建；旧库走 §3.6 方案 A 幂等补列或方案 B 清库） |

> 注：`AppDbContext.cs` 中 `ChatMessageRecord` 配置出现两段（33-49 行与 68-88 行）几乎重复，本次顺带清理第二段，属于同一文件内关联修改。

## 6. 测试要点

1. **压缩不拆轮**：构造多轮对话（含工具调用），触发压缩，断言保留区与压缩区均以轮为边界，无 FCC↔tool 分离
2. **未完成轮保留**：`MaximumIterationsPerRequest` 耗尽场景，断言未完成轮整组不被压缩
3. **完成判断（is_final）**：正常轮存在 `is_final=true` 行；迭代耗尽 / 异常中断的轮无该行；`is_final` 落在该轮终点行上——常规为最后一条纯文本 assistant，仅调工具场景可为 tool/FCC 行（语义以「终点标记」为准）
4. **存量兼容**：含 `run_id=NULL` / `is_final=0` 历史行的 session，压缩与加载行为正确
5. **孤儿 tool 过滤**：同组无 assistant-FCC 的 tool 行被过滤

## 7. 决策记录（2026-08-18 全部确认）

| # | 决策项 | 结论 |
|---|---|---|
| 1 | `run_id` 类型 | **Guid**（只负责分组，不承担排序） |
| 2 | 未完成轮兜底 | 设**独立上限 5 个**，超限强制压最旧并记 Warning |
| 3 | 存量数据 | **代码兼容 + 部署清库**（防御实现 + 干净数据） |
| 4 | 压缩阈值 | **最近 K 个完整轮次（K=12）+ 条数硬上限兜底**（4K 条，随 K 缩放） |
| 5 | 落地流程 | 设计定稿后**走 OpenSpec 变更**（`chat-round-boundary` 目录已建） |
| 6 | `is_final` 写入 | **Store 判定为主 + Run 后兜底补标** |
