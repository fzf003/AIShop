# handoff-T8 — Provide 孤儿 tool 检查升级：同 run_id 组内配对，NULL 退化相邻 id

## 工单

- 工单 ID：T8
- 状态：已完成
- 所属变更：chat-round-boundary（spec「孤儿 tool 按同 run_id 组内配对过滤」#13 + 「存量 NULL 行压缩与加载兼容」#14 的加载侧）
- blockedBy：T7（压缩安全阀，见 handoff-T7）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（ProvideChatHistoryAsync 孤儿 tool 检查：新增 assistant-FCC 集合预计算 + tool 行配对过滤升级）
- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（适配 T8 新规则下被过滤的两个孤行 tool 用例，见「与 T8T2 范围重叠」）

## 关键设计决策

- **配对过滤语义（spec #13）**：`ProvideChatHistoryAsync` 加载到 tool 行时，有 FRC 内容的 tool 行还必须能在其轮次内找到对应的 assistant-FCC 行才进上下文：
  1. **有 run_id** → 检查**同 run_id 组内**是否存在 assistant-FCC 行（`assistantFccRunIds.Contains(runId)`），不存在则过滤该 tool 行。比旧相邻 id 推断可靠，能吸收历史碎片（FCC 行丢失/错位时不残留孤儿 tool）。
  2. **run_id=NULL** → 无组可查，**退化为旧相邻 id 检查**（`assistantFccRowIds.Contains(row.Id - 1)`，id-1 为 assistant-FCC 则保留，否则过滤，评审 G1 兼容存量）。
- **assistant-FCC 行定义**：`Role=="assistant"` 且 `ToolCalls` 非空（与 Store 侧把 FCC 序列化到 ToolCalls 列一致）。在加载 rows 后一次性预计算两个集合（含 assistant-FCC 的 run_id 集合 + assistant-FCC 行 id 集合），循环内 O(1) 查，无每行子查询。
- **保留原有空内容过滤**：原「tool 行 contents 为空则过滤」继续保留（覆盖 ToolCalls 反序列化失败等 contents 为空的退化场景），T8 新增检查只针对有 FRC 内容的 tool 行做配对过滤，两层互补。
- **NULL 相邻 id 语义**：采用「紧邻前一行（id-1）」而非「前一条出现的 assistant-FCC」，与设计「id-1 为 assistant-FCC 则保留」及旧 c3377f7 成对保留算法（tool 仅在直接跟随 assistant-FCC 时保留）一致。

## 与 T8T2 范围重叠（需编排方注意）

- T8 新规则会使**孤行 tool 用例**（有 FRC 内容但无同组/相邻 assistant-FCC）被过滤。既有两个测试因此失败：
  - `Provide_RebuildsMultiFrcToolMessageFromToolCallsJson`
  - `Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent`
- 本 commit 已按 T8T2 描述的方式（"补齐 run_id 或相邻 assistant-FCC 行"）为这两个用例补上前置相邻 assistant-FCC 行（保持 NULL run_id，走相邻 id 退化路径，语义契合"存量"定位），使 `dotnet test` 全绿——否则 `check_commit_gate.py` 会在 commit 时强制跑 test 并 BLOCKED。
- **因此 T8T2 的"适配既有配对测试"部分已被本 commit 覆盖**，T8T2 剩余可做：补充同 run_id 组内配对的正向/反向断言（对应 T8T1 之外的第二视角），或确认现有适配已满足 spec #14。请编排方评估 T8T2 是否需要调整范围。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T8 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T8 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T7 相同机制。
- **T8T1 未实现**：专项测试（同 run_id 组内存在 assistant-FCC → tool 保留；组内无 → tool 被过滤；NULL tool 行相邻 id 退化路径）属后续独立测试工单 T8T1，blockedBy T8。
- **无 `ProvideChatHistoryAsync` 的其他调用方受影响**：经检索，仅 `SqliteChatHistoryProviderTests.cs` 调用该 Provider 的 Provide；`PreferenceMemoryProviderTests` 的 `ProvideMethod` 属于另一 Provider，不受影响。
- 代码库中 `appsettings.json`（用户本地模型切换）、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build --warnaserror`：0 错误 0 警告。
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 249 通过、AIShop.McpServer.Tests 11 通过，共 260，0 失败。
- 受影响用例验证（T8 新规则下）：
  - `Provide_RebuildsToolMessageAsFunctionResultContent`（assistant-FCC 在前、tool 在后，NULL run_id）→ 相邻 id 退化路径保留 tool，通过。
  - `Provide_RebuildsMultiFrcToolMessageFromToolCallsJson` / `Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent` → 已补前置相邻 assistant-FCC 行后保留 tool，通过。
  - `StoreAndProvide_MultiFrcRoundTrip_PreservesAllToolResults`（Store 落同一 run_id 的 assistant(FCC)+tool）→ 同 run_id 组内配对保留 tool，通过。
  - `Provide_ToolCallsJsonDeserializationFailure_LogsWarningAndSkipsOrphanTool`（contents 空）→ 原空内容过滤路径，通过。
