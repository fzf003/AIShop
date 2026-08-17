# handoff-T1T — ChatMessageRecord 实体默认值测试

## 工单

- 工单 ID：T1T
- 状态：已完成
- 所属变更：chat-round-boundary（spec「chat-round-boundary 数据模型」的实体默认值契约）
- blockedBy：T1（已在前置工单完成：`ChatMessageRecord` 已含 `RunId`/`IsFinal` 属性）

## 改动文件清单

- `tests/AIShop.Api.Tests/ChatMessageRecordEntityTests.cs`（新增，纯单元测试）

## 关键设计决策

- 断言新建 `ChatMessageRecord` 实例的两个轮次字段默认值：`IsFinal == false`、`RunId == null`
- 采用**新建独立测试文件** `ChatMessageRecordEntityTests.cs` 而非塞入 `ChatMessageRecordConfigurationTests.cs`：默认值断言是纯实体单元测试，无需 DbContext/schema 装配，避免为无状态断言构造内存 SQLite；命名与先例 `UserPreferencesEntityTests.cs`（纯实体契约测试）一致
- 测试命名 `NewChatMessageRecord_RunId_DefaultsToNull` / `NewChatMessageRecord_IsFinal_DefaultsToFalse`，贴合既有实体测试风格
- 注释使用中文，遵循项目 `.claude/rules/dotnet.md`

## 遗留问题

- 无。列级默认值（`is_final` 默认 0）、列存在性、索引维度断言属 T2T 工单范围，不在本工单覆盖
- **tasks.md 勾选 T1T 被流程门禁拦截**：`.claude/hooks/check_gateway.py` 规则 4 规定 `tasks.md` 必须由 `@task-breaker` 写入，检测到本次调用方为 `workflow-subagent` 而 BLOCKED。需编排方将该单行勾选操作委派给 `@task-breaker`（将 T1T 行 `- [ ]` 改为 `- [x]`），本工单代码与 handoff 已就绪，不阻塞后续 T2/T2T 工单

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 236 通过（含本工单新增 2 用例）、AIShop.McpServer.Tests 11 通过，共 247，0 失败
- `dotnet test --filter ChatMessageRecordEntityTests`：2 通过 0 失败
