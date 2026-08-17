# handoff-T10T1 — 两次 RunChatAsync(同 sessionId) → 两轮 run_id 不同、均非空（测试）

## 工单

- 工单 ID：T10T1
- 状态：已完成
- 所属变更：chat-round-boundary（spec「RunChatAsync 每轮开始生成 run_id 写 StateBag」#1 +「Store 读取 StateBag…」#2 +「跨模型切换追加新轮无冲突」#16）
- blockedBy：T10（`ShoppingAssistantAgent.RunChatAsync` 生成 run_id 写 StateBag + Run 后兜底补标，见 handoff-T10）

## 改动文件清单

- `tests/AIShop.Api.Tests/ShoppingAssistantAgentRunTests.cs`（唯一代码改动文件，新建）
  - 新增测试类 `ShoppingAssistantAgentRunTests`（`RunChatAsyncPreferenceBackfillTests` 同构：内存 SQLite + mock IChatClient + NSubstitute dbFactory）
  - 新增 1 个用例 `RunChatAsync_TwiceSameSession_PersistsTwoRoundsWithDistinctNonEmptyRunIds`：
    同一 sessionId 连续跑两次 `RunChatAsync`（mock 返回纯文本 assistant 回复，无 tool_calls）→
    断言 ①落库消息非空（Store 打标链路接通）②所有行 `RunId` 非空 ③按 `RunId` 分组恰为两轮
    ④两轮 `RunId` 互不相同 ⑤组内按 `Id` 升序保持时序

## 关键设计决策

- **复用 RunChatAsyncPreferenceBackfillTests 的 mock 模式**（tasks.md 指定）：不新建测试项目，直接以 mock `IChatClient`（`GetResponseAsync` 返回固定 JSON 回复、`GetStreamingResponseAsync` 返回空）驱动 `HarnessAgent` 全流程，不调真实 LLM。这是首个验证「mock 驱动完整 RunChatAsync 后 Store 真实落库」的用例——实证确认 `ChatHistoryProvider` 的 Store 钩子在该路径下被 MAF 正常触发。
- **纯文本 assistant 回复（无 tool_calls）**：mock 回复仅含 TextContent，一轮即完成（`MaximumIterationsPerRequest=3` 不触发 FICC 多迭代），Store 每轮写批内消息（user + assistant）并共享同一 run_id；同时走通 T5「批末纯文本 assistant → is_final=true」，`MarkRoundFinalAsync` 找到终点行不重复补标——聚焦验证本工单的 run_id 维度，不被工具轮干扰。
- **断言口径对齐 spec**：`Assert.All(rows, r => Assert.NotNull(r.RunId))` 对应 spec #2（所有 FICC 迭代写入行共享非空 run_id）；`GroupBy(RunId).Count()==2` + `Distinct().Count()==2` 对应 spec #1（每轮独立生成一次）与 #16（跨模型切换追加新轮无冲突、不覆盖）；组内按 id 升序断言对应 spec「组内行仍按 id 升序保持时序」。
- **与 T10T2 分工**：T10T1 只验证 run_id 两轮独立（本轮任务范围），is_final 终点行链路由 T10T2 另行覆盖，不在本工单重复断言。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T10T1 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁拦截——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理直接编辑被 BLOCKED。需由编排方委派 @task-breaker 完成该行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T10 相同机制。
- **T10T2 未实现**：`RunChatAsync` 正常返回后该轮存在 `is_final=true` 终点行（Agent→Provider 补标链路），属 T10 的另一个独立测试工单（blockedBy T10），由编排方另行调度。本 commit 只做 T10T1 的 run_id 两轮独立验证。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --no-build --filter FullyQualifiedName~ShoppingAssistantAgentRunTests` —— 1/1 通过（T10T1 新用例）。
- 全量回归：`dotnet test --no-build` —— AIShop.Api.Tests 259 通过（原 258 + 新增 1）、AIShop.McpServer.Tests 11 通过，共 270，0 失败，无回归。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
