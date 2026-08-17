# handoff-T9T1 — 无 is_final 轮（末条 tool/FCC）→ 调用后末条补标 is_final=true（测试）

## 工单

- 工单 ID：T9T1
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Run 后兜底补标 is_final」#5 的 tool/FCC 终点语义测试侧 +「补标操作收敛于 Provider 单一写入口」#6 设计约束）
- blockedBy：T9（`MarkRoundFinalAsync(runId)` 实现，见 handoff-T9）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（新增 2 个专项用例，置于 `Store_LastMessageTool_NoRowIsFinal` 之后）
  - `MarkRoundFinal_NoFinalToolEndingRound_MarksLastRowFinal`：无 is_final 的轮（末条为 tool 行，仅调工具未输出最终文本场景）→ 调用 `MarkRoundFinalAsync(runId)` 后末条 tool 行补标 `is_final=true`
  - `MarkRoundFinal_NoFinalAssistantFccEndingRound_MarksLastRowFinal`：无 is_final 的轮（末条为 assistant(FCC) 行，FICC 迭代耗尽 / MaximumIterationsPerRequest 场景）→ 调用后末条 FCC 行补标 `is_final=true`

## 关键设计决策

- **tool/FCC 终点语义（评审 Y1）**：T9T1 目标是验证「补标可能落在 tool/FCC 行上」——该行是轮次终点但不必是最终回复，语义以「轮次终点标记」为准。两个用例分别覆盖 tool 终点与 assistant(FCC) 终点，与 T9 实现（`OrderByDescending(Id)` 取 max id 补标）一一对应。
- **调用前前置断言**：seed 后先查询该 run_id 组内行，`Assert.DoesNotContain(rows, r => r.IsFinal)` 确认前置条件（Store 内判定未落标、本轮尚未收尾），再调用补标——使「调用导致补标」的因果链条对读者可见。
- **调用后落点断言**：按 id 升序读回该 run_id 组内全部行，断言仅 max id 行（末条）`IsFinal=true`、其余行恒为 false，且补标不改变末条行的内容（tool 行仍为 tool、FCC 行 ToolCalls 列仍非空）——证明补标是「补写终点标记」而非改行内容。
- **自包含 seed、不依赖共享 helper**：用例直接以已知 runId seed 行（user → assistant(FCC) → tool / user → assistant(FCC)），未复用 `SeedIncompleteToolRound()`（其 runId 内部生成不外露，复用需先回查 DB，自包含更直接）。命名沿用本文件既有 `{Action}_{Context}_{Expected}` 风格。
- **不改动实现**：本工单为纯测试新增，不触碰 `SqliteChatHistoryProvider.cs`（T9 实现已提交）。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T9T1 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——tasks.md 强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T9T1 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T9 相同机制。
- **T9T2 未实现**：轮内已有 `is_final=true` → 不重复补标；runId 无对应行 → 静默返回，属 T9 的另一个独立测试工单（blockedBy T9），由编排方另行调度。本 commit 只做 T9T1 的 tool/FCC 补标语义验证。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --no-build --filter FullyQualifiedName~MarkRoundFinal` —— 2/2 通过（T9T1 两个新用例），1s。
- Provider 测试：`dotnet test --no-build --filter FullyQualifiedName~SqliteChatHistoryProviderTests` —— 38/38 通过（原 36 + 新增 2），2s。
- 全量回归：`dotnet test --no-build` —— AIShop.Api.Tests 256 通过、AIShop.McpServer.Tests 11 通过，共 267，0 失败，无回归。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
