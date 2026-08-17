# handoff-T2 — AppDbContext 合并重复配置 + run_id/is_final 列映射 + 索引扩 run_id 维度

## 工单

- 工单 ID：T2
- 状态：已完成
- 所属变更：chat-round-boundary（spec「schema 幂等补列或清库」方案 B 全新库 EnsureCreated 路径）
- blockedBy：T1（ChatMessageRecord 已新增 RunId/IsFinal 属性）

## 改动文件清单

- `src/AIShop.Infrastructure/Data/AppDbContext.cs`（合并两段重复配置 + 新增两列映射 + 扩展索引）

## 关键设计决策

- **合并两段重复的 `ChatMessageRecord` 配置块**：原 33-49 行（第一段，含 TEXT 类型声明与 `datetime('now')`/false 默认值）与 68-88 行（第二段）合并为一段，保留第一段更完整的映射（`Role`/`ToolCallId`/`ToolName` 的 `HasColumnType("TEXT")`、`CreatedAt` 默认 `datetime('now')`、`IsCompacted` 默认 false），删除第二段重复块。合并后映射语义与合并前保持一致（第二段的 `HasColumnName("id")` 为 SQLite 列名大小写不敏感下的等价写法，未显式保留）。
- **新增 `run_id` 列映射**：`RunId: Guid?` → `HasColumnName("run_id").HasColumnType("TEXT")`，可空兼容存量，SQLite 存 Guid 字符串（design §3 数据模型变更）。
- **新增 `is_final` 列映射**：`IsFinal: bool` → `HasColumnName("is_final").HasDefaultValue(false)`，EF 生成 `INTEGER NOT NULL DEFAULT 0`（design §3）。
- **`idx_cm_session_active` 扩展 run_id 维度**：`(SessionId, IsCompacted, Id)` → `(SessionId, IsCompacted, RunId, Id)`，支撑后续按 run_id 分组压缩与 Provide 加载；`idx_cm_session_id` 保持不变。
- 采用 tasks.md 的**方案 B 强制清库**：不做 Program.cs 幂等补列（原 T3 已移除），全新库由 `EnsureCreated` 直接建列；`Program.cs` / `ProgramSeedingTests.cs` 不在本工单范围。

## 遗留问题

- T2 自身无遗留。`run_id`/`is_final` 列的存在性、`is_final` 默认 0、索引含 run_id 维度的断言属 T2T 工单（blockedBy T2），不在本工单范围；旧 schema 库需按方案 B 清库后由 EnsureCreated 建列（部署动作：删除旧库文件）。
- **tasks.md 勾选待办**：`tasks.md` 中 T2 行标 `[x]` 被 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）拦截——该文件强制由 @task-breaker 编辑（检测到实际调用方是 workflow-subagent）。本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T2 行 `[ ]` → `[x]`（仅该行，不动其他工单）。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test --no-build`：全部通过无回归 —— AIShop.Api.Tests 236 通过、AIShop.McpServer.Tests 11 通过，共 247，0 失败（含 `ChatMessageRecordConfigurationTests` 既有表结构/索引断言）
- T2 自身无独立测试用例（列/索引断言属 T2T 工单，不在本工单范围）
