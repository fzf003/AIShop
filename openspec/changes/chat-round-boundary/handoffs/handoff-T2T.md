# handoff-T2T — chat_messages 新列存在 / is_final 默认 0 / 索引含 run_id 断言

## 工单

- 工单 ID：T2T
- 状态：已完成
- 所属变更：chat-round-boundary（spec「schema 幂等补列或清库」方案 B 全新库 EnsureCreated 路径）
- blockedBy：T2（AppDbContext 已合并配置 + 新增 run_id/is_final 列映射 + 索引扩 run_id 维度）

## 改动文件清单

- `tests/AIShop.Api.Tests/ChatMessageRecordConfigurationTests.cs`（更新列存在断言 + 新增 2 个用例）

## 关键设计决策

- **列存在断言并入既有 `ChatMessagesTable_ShouldHaveExpectedColumns`**：在该用例的列清单中断言新增 `run_id`/`is_final`，保持"期望列集合"用例完整；不另起重复的列存在用例。
- **`is_final` 默认 0 用原生 SQL 验证 DB 层默认值**：`INSERT INTO chat_messages (session_id, role, content) VALUES ({0}, 'user', 'test')` 不提供 `is_final` 列（`created_at` 也一并省略，顺带验证 `datetime('now')` 默认），读回后断言 `IsFinal == false`。相比既有 `IsCompactedDefault_ShouldBeFalse`（走 EF CLR 默认路径），原生 SQL 直接落在方案 B「全新库 EnsureCreated 建列」的 DB 默认语义上，不依赖 EF 对默认值的省略行为。
- **索引 run_id 维度用 `pragma_index_info('idx_cm_session_active')` 断言**：返回该索引各列列名，断言集合含 `run_id`/`session_id`/`is_compacted`/`id` 四维，对应 T2 中 `(SessionId, IsCompacted, RunId, Id)` 的组合索引。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T2T 行标 `[x]` 被 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）拦截——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent）。本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T2T 行 `[ ]` → `[x]`（仅该行，不动其他工单）。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test --no-build`：全部通过无回归 —— AIShop.Api.Tests 238 通过（较 T2 后 236 增 2 个 T2T 用例）、AIShop.McpServer.Tests 11 通过，共 249，0 失败
- T2T 新增用例：
  - `ChatMessagesTable_IsFinalDefault_ShouldBeZero`（原生 SQL 插入不提供 is_final → 读回 `IsFinal == false`）
  - `ChatMessagesTable_CompositeIndex_ShouldContainRunId`（`pragma_index_info` 断言索引含 run_id 维度）
- 既有 `ChatMessagesTable_ShouldHaveExpectedColumns` 扩展断言 `run_id`/`is_final` 存在
