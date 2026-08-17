# handoff-T4 — StoreChatHistoryAsync 从 StateBag 读 RunId 打标；无则 Guid.NewGuid() 兜底

## 工单

- 工单 ID：T4
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Store 读取 StateBag 为所有 FICC 迭代打同一 run_id」「无 RunId 时兜底生成独立 run_id」）
- blockedBy：T2（AppDbContext 已含 run_id/is_final 列映射 + idx_cm_session_active 扩 run_id 维度）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（StoreChatHistoryAsync：读 StateBag 的 RunId 打标 + 兜底生成）

## 关键设计决策

- **读取位置**：在 `StoreChatHistoryAsync` 打开 DbContext 之后、foreach 追加消息之前读取 `session.StateBag["RunId"]`，一次读取、批内全部 `ChatMessageRecord` 复用同一 runId（spec：「所有迭代写入的消息行共享同一 RunId，组内行仍按 id 升序保持时序」）。
- **打标方式**：每个新增 `ChatMessageRecord` 设 `RunId = runId`，紧邻 `SessionId` 赋值，不改动既有列逻辑。
- **兜底语义**：StateBag 无 "RunId" 或值非法时 `Guid.NewGuid()` 兜底，该批自成独立轮次，不抛异常、退化现状（spec：「写入正常完成不抛异常，行为退化到现状」）。用 `Guid.TryParse` 而非 design §4 示例的 `Guid.Parse`：值非法路径也走兜底不抛异常，与「不抛异常」意图一致，不新增复杂度。
- **不动压缩逻辑**：步骤 2 的裁剪（含 ±1 相邻推断）属 T6 范围，本工单不改动，保持既有行为。

## 遗留问题

- T4 自身无遗留。run_id 打标/兜底的断言属 T4T 工单（blockedBy T4，tasks.md 已挂载），不在本工单范围。
- **tasks.md 勾选待办**：`tasks.md` 中 T4 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent 即拦截），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T4 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T2/T2T 相同机制。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test --no-build`：全部通过无回归 —— AIShop.Api.Tests 238 通过、AIShop.McpServer.Tests 11 通过，共 249，0 失败。现有 Store/Provide/压缩测试未设置 StateBag RunId，走兜底路径；既有断言均不检查 RunId，无回归。
- T4 自身无独立测试用例（打标/兜底断言属 T4T 工单，不在本工单范围）
