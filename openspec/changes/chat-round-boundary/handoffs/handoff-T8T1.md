# handoff-T8T1 — 孤儿 tool 配对过滤：同 run_id 组内配对保留/过滤 + NULL 退化路径（测试）

## 工单

- 工单 ID：T8T1
- 状态：已完成
- 所属变更：chat-round-boundary（spec「孤儿 tool 按同 run_id 组内配对过滤」#13 的测试侧）
- blockedBy：T8（Provide 孤儿 tool 检查升级实现，见 handoff-T8）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`
  - 新增 `Provide_ToolWithRunId_HasAssistantFccInGroup_Kept`（同 run_id 组内有 assistant-FCC → tool 保留）
  - 新增 `Provide_ToolWithRunId_HasNonAdjacentFccInGroup_Kept`（FCC 与 tool 同 run_id 但不相邻 → 组内配对保留，证明配对已从相邻 id 推断升级为组内判定）
  - 新增 `Provide_ToolWithRunId_NoAssistantFccInGroup_Filtered`（同 run_id 组内无 assistant-FCC → tool 被过滤，且会话中其他轮次不同 run_id 的 FCC 不救回它，证明配对是「同 run_id 组内」限定）
  - 新增 `Provide_NullRunIdTool_AdjacentAssistantFcc_Kept`（NULL tool 行退化相邻 id 检查：id-1 为 assistant-FCC → 保留）
  - 新增 `Provide_NullRunIdTool_NoAdjacentAssistantFcc_Filtered`（NULL tool 行退化相邻 id 检查：id-1 非 assistant-FCC → 过滤）

## 关键设计决策

- **三个核心断言场景（对应 T8T1 描述与 spec #13）**：
  1. **组内保留**：tool 行带 run_id，其同 run_id 组内存在 assistant-FCC 行（`assistantFccRunIds.Contains(runId)`）→ tool 正常重建 FRC 进上下文。
  2. **组内过滤**：tool 行带 run_id，同 run_id 组内无 assistant-FCC → tool 被过滤。特意在会话中再 seed 一个含 assistant-FCC 的**不同 run_id** 轮次，断言不同组的 FCC 不救回孤儿 tool，精确锁定「同 run_id 组内」语义（而非"会话内存在任意 FCC"）。
  3. **NULL 退化**：`run_id=NULL` 的 tool 行走旧相邻 id 检查（`assistantFccRowIds.Contains(row.Id - 1)`），id-1 为 assistant-FCC 保留、非 FCC 过滤，兼容存量数据。
- **非相邻组内配对用例**：`HasNonAdjacentFccInGroup_Kept` seed FCC(id2) 与 tool(id4) 同 run_id、中间夹普通 assistant 文本行——旧相邻 id 推断会误过滤，组内配对则保留。该用例直接证明 T8 升级的设计意图（"比相邻 id 推断可靠，能吸收历史碎片"），是本工单中区分新旧行为最强的回归锚点。
- **与既有测试关系**：T8 commit（8b15152）已按 T8T2 方式为两个孤行 tool 用例补前置相邻 assistant-FCC 行（NULL run_id 退化路径）。本工单新增的 `Provide_NullRunIdTool_AdjacentAssistantFcc_Kept` 与其语义重叠但为**显式专项用例**（直接映射 T8T1「NULL tool 行相邻 id 退化路径」），不重复修改既有用例。
- **对象比较规避 CS0252**：`FunctionResultContent.Result` 为 `object?`，`f.Result == "孤儿结果"` 触发引用比较警告（warnaserror 会阻塞），改用 `f.Result?.ToString() == "孤儿结果"` 值比较。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T8T1 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T8T1 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T8 相同机制。
- **后续工单未实现**：T9（MarkRoundFinalAsync）及 T9T1/T9T2、T10（Agent 接线）及 T10T1/T10T2 属后续工单，blockedBy 各自前置，不在本工单范围。
- **T8T2 范围提示**：T8T1 仅新增专项测试；「适配既有配对测试」部分已被 T8 commit 覆盖（handoff-T8 已说明），T8T2 剩余可评估是否需补充同 run_id 组内配对的正向/反向第二视角断言。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --filter FullyQualifiedName~SqliteChatHistoryProviderTests` —— 36/36 通过（含新增 5 个 T8T1 用例），3s。
- 全量回归：`dotnet test` —— AIShop.Api.Tests 254 通过、AIShop.McpServer.Tests 11 通过，共 265，0 失败（新增 5 个用例，无回归）。
- 受影响用例验证（T8 新规则下既有 Provide 配对用例均保持通过）：
  - `Provide_RebuildsToolMessageAsFunctionResultContent` / `Provide_RebuildsMultiFrcToolMessageFromToolCallsJson` / `Provide_RebuildsLegacyToolMessageWithToolCallIdAndContent`（NULL run_id 相邻 assistant-FCC 退化路径）→ 保留 tool，通过。
  - `StoreAndProvide_MultiFrcRoundTrip_PreservesAllToolResults`（Store 落同一 run_id 的 assistant(FCC)+tool）→ 同 run_id 组内配对，通过。
  - `Provide_ToolCallsJsonDeserializationFailure_LogsWarningAndSkipsOrphanTool`（contents 空，走原空内容过滤）→ 通过。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
