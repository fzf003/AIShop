# handoff-T8T2 — 更新既有 Provide 配对测试适配新配对规则（测试）

## 工单

- 工单 ID：T8T2
- 状态：已完成
- 所属变更：chat-round-boundary（spec「存量 NULL 行压缩与加载兼容」#14 的测试侧 + T8 新配对规则的既有测试适配）
- blockedBy：T8（Provide 孤儿 tool 检查升级实现，见 handoff-T8）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`
  - 更新 `Provide_RebuildsToolMessageAsFunctionResultContent`：显式标注其依赖的 T8 配对规则路径——前置相邻 assistant-FCC 行（同 session、无 run_id，走 NULL 相邻 id 退化路径），保持既有配对语义（单 FRC tool 行重建为 FunctionResultContent）不变被验证

## 关键设计决策

- **T8T2 适配现状核对**：本工单目标是「既有 Provide 工具配对测试适配新配对规则」。经逐项核对，T8 commit（8b15152）已为两个**孤行 tool** 既有用例补齐相邻 assistant-FCC 行（NULL run_id 退化路径）：
  - `Provide_RebuildsMultiFrcToolMessageFromToolCallsJson`（多 FRC tool 行，T8 前无 FCC 前置行 → T8 补齐）
  - `Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent`（旧格式单 FRC tool 行，T8 前无 FCC 前置行 → T8 补齐）
  - 两者均满足 T8 配对过滤（`assistantFccRowIds.Contains(row.Id - 1)`），对应 spec「存量 NULL 行加载兼容」。
- **本工单补齐最后一块**：`Provide_RebuildsToolMessageAsFunctionResultContent` 是剩余唯一既有 Provide 配对测试——其 seed 结构（id N assistant-FCC + id N+1 tool，均 run_id=NULL）在 T8 前已满足 NULL 相邻退化路径、无需补行，但注释未显式标注配对规则依赖。本工单为其补充中文注释（含 id N / id N+1 行内注释），与 T8 已适配的两个测试保持一致的显式标注风格，使「既有配对语义在新规则下仍被验证」对读者可见。
- **不重复改 T8T1 新增的专项测试**：T8T1 已新增 5 个显式配对专项用例（组内保留/组内过滤/非相邻组内配对/NULL 相邻保留/NULL 非相邻过滤），负向过滤场景已充分覆盖；本工单只做「既有测试适配」，不再新增用例。
- **不改动实现**：本工单为纯测试标注改动，不触碰 `SqliteChatHistoryProvider.cs`（T8 实现已提交）。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T8T2 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——tasks.md 强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T8T2 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T8T1 相同机制。
- **后续工单未实现**：T9（MarkRoundFinalAsync）及 T9T1/T9T2、T10（Agent 接线）及 T10T1/T10T2 属后续工单，blockedBy 各自前置，不在本工单范围。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --filter FullyQualifiedName~SqliteChatHistoryProviderTests` —— 36/36 通过（含 T8/T8T1/T8T2 全部配对用例），2s。
- 全量回归：`dotnet test` —— AIShop.Api.Tests 254 通过、AIShop.McpServer.Tests 11 通过，共 265，0 失败，无回归。
- 受影响用例验证（T8 新规则下既有 Provide 配对测试均保持通过，本工单目标达成）：
  - `Provide_RebuildsToolMessageAsFunctionResultContent` / `Provide_RebuildsMultiFrcToolMessageFromToolCallsJson` / `Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent`（NULL run_id 相邻 assistant-FCC 退化路径）→ 保留 tool，通过。
  - `StoreAndProvide_MultiFrcRoundTrip_PreservesAllToolResults`（Store 落同一 run_id 的 assistant(FCC)+tool）→ 同 run_id 组内配对，通过。
  - `Provide_ToolCallsJsonDeserializationFailure_LogsWarningAndSkipsOrphanTool`（contents 空，走原空内容过滤）→ 通过。
- commit 门禁：check_commit_gate.py 在 commit 时自动重跑 `dotnet build` + `dotnet test`，通过后提交成功（commit 21efbc4）。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
