# handoff-T10 — ShoppingAssistantAgent 存 provider 字段；RunChatAsync 生成 run_id 写 StateBag；Run 后调 MarkRoundFinalAsync

## 工单

- 工单 ID：T10
- 状态：已完成
- 所属变更：chat-round-boundary（spec「RunChatAsync 每轮开始生成 run_id 写入 StateBag」#1 +「Run 后兜底补标 is_final」#5 +「补标操作收敛于 Provider 单一写入口」#6）
- blockedBy：T9（Provider 新增 `MarkRoundFinalAsync(runId)`，见 handoff-T9）

## 改动文件清单

- `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs`（唯一代码改动文件）
  1. 新增字段 `private readonly SqliteChatHistoryProvider _provider;`
  2. 构造函数：`_provider = new SqliteChatHistoryProvider(dbFactory);` 存为字段，`HarnessAgentOptions.ChatHistoryProvider = _provider` 引用该字段（不再内联 new）
  3. `RunChatAsync` 创建 session 后：`var runId = Guid.NewGuid(); session.StateBag.SetValue("RunId", runId.ToString());`
  4. `RunChatAsync` 末尾（Run 正常返回后）：`await _provider.MarkRoundFinalAsync(runId, ct);` 兜底补标

## 关键设计决策

- **Provider 存字段（非内联）**：Agent 需在 Run 返回后调用 Provider 的补标方法，故将 `new SqliteChatHistoryProvider(dbFactory)` 提为字段 `_provider`，`ChatHistoryProvider` 选项引用同一实例——Provider 只创建一次，Agent 持有同一实例，不重复构造。
- **run_id 每轮只生成一次**（spec #1）：在 `CreateSessionAsync` 后、`RunAsync` 前生成并写入 StateBag，Store（每次 FICC 迭代）从 StateBag 读到同一值，为一次 Run 的所有迭代打同一轮次标记。写在 `SessionId` 之后、`preferences` 回填之前，一次 Run 恰一次。
- **Run 后兜底补标**（spec #5 / 决策 ⑥ 双路径的兜底路径）：Store 内判定（T5）若未落 `is_final=true` 行（如仅调工具未输出文本、FICC 迭代结束信号漏判），`MarkRoundFinalAsync` 将本轮末条补标为轮次终点。补标收敛在 Provider 单一写入口（评审 Y3），Agent 不直接操作 DbContext。
- **异常中断不补标**：调用位于 OpenAI / 非 OpenAI 两个分支之后、`return` 之前，仅 Run 正常返回后执行；`RunAsync` 抛异常中断时不会执行到该行，该轮天然视为未完成（spec「MaximumIterationsPerRequest 耗尽或异常中断时该轮无 is_final 行」）。
- **CancellationToken 透传**：将 `RunChatAsync` 的 `ct` 传给 `MarkRoundFinalAsync(runId, ct)`，对齐项目异步规范（I/O 支持取消）与 T9 预留的签名。
- **补标落点语义**：补标可能落在 tool/FCC 行上，此时该行是「轮次终点」但不是「最终回复」，语义以「终点标记」为准（评审 Y1），由 T9 的 `MarkRoundFinalAsync` 实现保证。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T10 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁拦截——已实测本子代理直接编辑被 BLOCKED，该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截）。需由编排方委派 @task-breaker 完成该行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T9 相同机制。
- **T10T1 / T10T2 未实现**：专项测试（T10T1：mock IChatClient 跑两次 RunChatAsync → 两轮 run_id 不同、落库均带非空 run_id；T10T2：Run 后该轮存在 is_final=true 终点行）属后续独立测试工单，blockedBy T10。本 commit 只做 T10 实现，测试工单由编排方另行调度。
- 代码库中 `appsettings.json`（用户本地模型切换）、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build --warnaserror`：0 错误 0 警告。
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 258 通过、AIShop.McpServer.Tests 11 通过，共 269，0 失败。
- 本工单无新增测试（T10T1/T10T2 属独立测试工单）；现有 Agent 相关用例（RunChatAsyncPreferenceBackfillTests 等）与 T1-T9 及其测试全部保持通过，确认未引入回归。
