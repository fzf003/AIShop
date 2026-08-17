# handoff-T5 — StoreChatHistoryAsync 批末条纯文本 assistant → IsFinal=true；非纯文本不落

## 工单

- 工单 ID：T5
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Store 内判定写 is_final（末条纯文本 assistant）」）
- blockedBy：T4（StoreChatHistoryAsync 已从 StateBag 读 RunId 打标 + Guid.NewGuid() 兜底，见 handoff-T4）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（StoreChatHistoryAsync：批末条纯文本 assistant 判定 + IsFinal=true 落库）

## 关键设计决策

- **判定位置**：在 `StoreChatHistoryAsync` 读取 run_id 之后、foreach 追加消息之前，一次性判定 `allMessages`（RequestMessages + ResponseMessages 拼接后）最后一条；`allMessages[^1]` 前置有 `Count == 0` 早退保证非空，无越界风险。
- **判定标准**：`Role == ChatRole.Assistant && !Contents.OfType<FunctionCallContent>().Any()`——即「纯文本 assistant / 无 tool_calls」，对应 spec 定义「纯文本 assistant（无 tool_calls，即 FICC 迭代结束信号）」。未额外要求必须有 TextContent：仅调工具场景末条为 assistant(FCC)/tool 时自然不满足，归 T9 兜底补标；纯 reasoning 无 tool_calls 的 assistant 落 final 语义仍成立（该行确为轮次终点）。
- **落标方式**：循环从 `foreach` 改为 `for`（带索引 `i`），在 `ChatMessageRecord` 构造时以 `IsFinal = isFinalLastMessage && i == allMessages.Count - 1` 仅对批内最后一条置 true，其余行恒为 false（`IsFinal` 默认 false，显式赋值保持可读）。
- **不落语义**：末条非纯文本（assistant(FCC) 或 tool 行）时 `isFinalLastMessage == false`，本批所有行 `IsFinal=false`——该轮视为未完成，由 T9 `MarkRoundFinalAsync(runId)` 兜底补标，符合 design §5「Store 判定为主 + Run 后兜底补标」双路径。
- **不动压缩逻辑**：步骤 2 的裁剪（含 ±1 相邻推断）属 T6 范围，本工单不改动；IsFinal 列只做标记，不影响 Provide 按 `(session_id, is_compacted=0)` 按 id 升序加载。

## 遗留问题

- T5 自身无实现遗留。is_final 落标行为断言属 T5T 工单（blockedBy T5，tasks.md 已挂载），不在本工单范围。
- **tasks.md 勾选待办**：`tasks.md` 中 T5 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent 即拦截），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T5 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1/T1T/T2/T2T/T4/T4T 相同机制。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test --no-build`：全部通过无回归 —— AIShop.Api.Tests 240 通过、AIShop.McpServer.Tests 11 通过，共 251，0 失败。现有 Store/Provide/压缩测试响应多为纯文本 assistant（现会被标 IsFinal=true）或 assistant(FCC)/tool（不满足判定不落标），但既有断言均不检查 IsFinal 列，无回归；IsFinal 不参与 Provide 加载过滤与压缩计数，行为不受影响。
- T5 自身无独立测试用例（落标断言属 T5T 工单，不在本工单范围）。
