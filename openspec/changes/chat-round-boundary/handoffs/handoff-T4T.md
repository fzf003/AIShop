# handoff-T4T — 同 RunId 两次 Store 共享 run_id 且组内按 id 升序；无 RunId 每批独立不抛异常

## 工单

- 工单 ID：T4T
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Store 读取 StateBag 为所有 FICC 迭代打同一 run_id」「无 RunId 时兜底生成独立 run_id」）
- blockedBy：T4（StoreChatHistoryAsync 已实现 StateBag RunId 打标 + Guid.NewGuid() 兜底，见 handoff-T4）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（新增两个测试用例）

## 关键设计决策

- **测试 1「同 StateBag RunId 两次 Store 共享 run_id」**：模拟 FICC 两次迭代——第 1 次 Store 写 `user` + `assistant(FCC)`，第 2 次 Store 写 `tool` 结果（StateBag 写入同一 RunId）。断言：两次 Store 落库的所有行 `RunId == StateBag 中设置的 runId`；按 `id` 升序读取的时序为 `user → assistant(FCC) → tool`，验证「组内行仍按 id 升序保持时序」。
- **测试 2「无 RunId 每批独立」**：StateBag 不写 RunId，两次 Store（各 2 行）正常落库不抛异常。断言：共 4 行、两批 run_id 互不相同（共 2 个不同 run_id）、同一批内 2 行共享同一 run_id——验证兜底 `Guid.NewGuid()` 每批自成独立轮次、行为退化现状。
- **覆盖 spec 需求**：两用例分别对应 spec 需求 2（Store 读取 StateBag 为所有 FICC 迭代打同一 run_id）与需求 3（无 RunId 时兜底生成独立 run_id）。
- **测试风格**：沿用本文件既有 `Store_…` 命名、in-memory SQLite + `_dbFactory` 替身 + `InvokeStoreAsync` 辅助方法模式，不新增测试基础设施；用 `Assert.DoesNotContain(runIds, r => r is null)` 断言 run_id 非空（规避 `Assert.NotNull` 对 `Nullable<Guid>` 的重载歧义）。

## 遗留问题

- 无实现遗留。
- **tasks.md 勾选待办**：`tasks.md` 中 T4T 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent 即 BLOCKED），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T4T 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1/T1T/T2/T2T/T4 相同机制。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test -warnaserror`：AIShop.Api.Tests 240 通过、AIShop.McpServer.Tests 11 通过，共 251，0 失败，无回归（既有 Store/Provide/压缩测试未设置 StateBag RunId 走兜底路径，既有断言不检查 RunId，不受影响）
- 新增 2 个用例（`--filter` 单独确认均通过）：
  - `Store_SameStateBagRunId_TwoBatchesShareRunIdAndKeepIdOrder`
  - `Store_NoStateBagRunId_EachBatchGetsIndependentRunId`
