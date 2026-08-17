# handoff-T5T — 批末条纯文本 assistant → is_final=true；末条 FCC/tool → 无 is_final 行

## 工单

- 工单 ID：T5T
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Store 内判定写 is_final（末条纯文本 assistant）」）
- blockedBy：T5（StoreChatHistoryAsync 已实现批末条纯文本 assistant → IsFinal=true；非纯文本不落，见 handoff-T5）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（新增三个测试用例）

## 关键设计决策

- **测试 1「末条纯文本 assistant 落标」**：`responseMessages` 为纯文本 assistant（无 FunctionCallContent）→ 断言 user 行 `IsFinal==false`、末条 assistant 行 `IsFinal==true`。对应 spec「末条纯文本 assistant 是 FICC 迭代结束信号 → 该行落 is_final=true 作为本轮轮次终点标记」。
- **测试 2「末条 assistant(FCC) 不落标」**：末条为 assistant 带 FunctionCallContent（FICC 中间态，非纯文本）→ 断言两行 `IsFinal` 全为 false、末条 `ToolCalls` 非空。该批不落 is_final，该轮视为未完成（归 T9 `MarkRoundFinalAsync` 兜底补标）。
- **测试 3「末条 tool 不落标」**：`responseMessages` 仅一条 tool 结果 → 断言该行 `IsFinal==false`。末条为 tool 非纯文本 assistant，本批不落 is_final。
- **覆盖 spec 需求**：三用例对应 spec 需求 4（Store 内判定写 is_final）的正例 + 两个反例（assistant(FCC)/tool），断言点与 T5 实现中 `isFinalLastMessage = Role == Assistant && !Contents.OfType<FunctionCallContent>().Any()` 的判定一致。
- **测试风格**：沿用本文件既有 `Store_…` 命名、in-memory SQLite + `_dbFactory` 替身 + `InvokeStoreAsync` 辅助方法模式，不新增测试基础设施；末条 tool 用例传 `requestMessages: []` 使 `allMessages` 仅含 tool 单条（与 T4T 第 2 次 Store 用法一致）。

## 遗留问题

- 无实现遗留。
- **tasks.md 勾选待办**：`tasks.md` 中 T5T 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent 即拦截），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T5T 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1/T1T/T2/T2T/T4/T4T/T5 相同机制。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test --no-build`：全部通过无回归 —— AIShop.Api.Tests 243 通过、AIShop.McpServer.Tests 11 通过，共 254，0 失败（较 T5 时 251 增加 3，即本工单新增三用例）
- 新增 3 个用例（`--filter FullyQualifiedName~Store_LastMessage` 单独确认 3 个均通过）：
  - `Store_LastMessagePlainTextAssistant_MarksIsFinalTrue`
  - `Store_LastMessageAssistantFcc_NoRowIsFinal`
  - `Store_LastMessageTool_NoRowIsFinal`
