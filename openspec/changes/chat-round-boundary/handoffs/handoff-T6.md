# handoff-T6 — 压缩按 run_id 整轮整切至 K=12；未完成轮整组保留；删 ±1 相邻推断

## 工单

- 工单 ID：T6
- 状态：已完成
- 所属变更：chat-round-boundary（spec「压缩按 run_id 整轮整切不拆轮」「未完成轮整组保留」）
- blockedBy：T5（StoreChatHistoryAsync 批末条纯文本 assistant → IsFinal=true 落标，见 handoff-T5）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（StoreChatHistoryAsync 步骤 2 压缩逻辑重构：按 run_id 整轮整切 + 删除 ±1 相邻推断；`MaxStoredMessages=50` 常量替换为 `MaxCompletedRounds=12`）
- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（既有压缩测试断言从「条数 50」改为「轮次口径」+ 新增 `SeedRounds` 按轮 seed 辅助方法）

## 关键设计决策

- **压缩算法**：未压缩消息先按 run_id 分组 → 从最旧**完整轮**（组内最大 id 升序）开始整轮整轮标记 `is_compacted=true`，直到剩余完整轮数 ≤ K=12 停止；未完成轮（组内无 `is_final=true` 行）整组保留、不参与压缩（T7 再补「未完成轮上限 5」兜底）。
- **NULL 行分组实现（关键坑）**：直接 `GroupBy(r => r.RunId)` 会把所有 `run_id=NULL` 的历史行合并到同一个 null 键组，无法实现「每行自成一组」。实际用复合键 `new { RunId = r.RunId, NullRowKey = r.RunId is null ? r.Id : (long?)null }`——NULL 行用其唯一 Id 作键每行自成一组，非 NULL 行共享 run_id 值同轮归组。
- **完整轮判定**：`IsComplete = RunId is null || 组内存在 IsFinal`。NULL 历史行每行自成一组、走旧行为，视为可压缩（否则存量 NULL 行永远不被裁剪）。
- **配对保护天然成立**：整轮一起压，FCC↔tool 结构性不分离，因此**删除原 `compressIds` 的 ±1 相邻推断逻辑**（原 firstCompressId 前后查询、compressIds.Remove/Add 调整全部移除）。
- **K 值**：`MaxCompletedRounds = 12`（design.md 评审 B1：纯文本轮 2 行/轮、含工具轮约 4 行/轮，容量约等于旧 50 条硬切）。
- **查询投影**：步骤 2 仅投影 `(Id, RunId, IsFinal)` 到内存分组，避免整实体加载；压缩执行仍用 `ExecuteUpdateAsync` 非物理删除。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T6 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T6 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1/T1T/T2/T2T/T4/T4T/T5/T5T 相同机制。
- **与 T6T3 范围重叠**：T6 语义变更使 4 个既有压缩测试断言失效（Store_AppendsThenTrimsToStoredLimit / Store_TrimsWhenExceedsLimit / Store_TrimsToExactly50 / Store_MultiFrcToolPair_CompressedTogetherWithAssistantFcc）。因 `check_commitgate.py` 强制提交前 dotnet test 全绿，T6 一并把这 4 个断言从「条数 50」改为「轮次口径」（`Store_TrimsToExactly50` 更名 `Store_TrimsToAtMostKCompleteRounds`），并将 `Store_TrimsToAtMostKCompleteRounds` 改用 `SeedRounds` 真轮次 seed。**这是 T6T3 的职责范围，T6 为保持门禁绿色而提前消化**；T6T3 执行时可在现有轮次口径断言基础上补充「存量 NULL 行压缩加载兼容、不丢消息不抛异常」专项用例（`Store_AppendsThenTrimsToStoredLimit` / `Store_MultiFrcToolPair` 已间接覆盖 NULL 行压缩路径）。
- **T6T1 / T6T2 未实现**：本工单仅实现 + 适配既有断言，未写 T6T1（不拆轮边界测试）/ T6T2（未完成轮保留测试）专项新用例——属后续独立测试工单，blockedBy T6。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build --warnaserror`：0 错误 0 警告。
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 243 通过、AIShop.McpServer.Tests 11 通过，共 254，0 失败。
- 压缩相关既有测试已按轮次口径适配：`Store_AppendsThenTrimsToStoredLimit`（15 NULL 行 + 1 轮 → 压缩 4 行，未压缩 13）、`Store_TrimsWhenExceedsLimit`（55 NULL 行 + 1 轮 → 压缩 44 行，未压缩 13）、`Store_TrimsToAtMostKCompleteRounds`（31 完整轮 → 压缩最旧 19 轮，剩余 12 轮 24 行，并断言 `GroupBy(run_id) == 12` 轮边界）、`Store_MultiFrcToolPair_CompressedTogetherWithAssistantFcc`（id1 FCC + id2 多 FRC tool 同为最旧组一并压缩、FCC↔tool 配对不分离，未压缩 13）。
