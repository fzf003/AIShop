# handoff-T1 — ChatMessageRecord 新增 RunId + IsFinal

## 工单

- 工单 ID：T1
- 状态：已完成
- 所属变更：chat-round-boundary（spec「chat-round-boundary 数据模型」前置基础）

## 改动文件清单

- `src/AIShop.Infrastructure/Entities/ChatMessageRecord.cs`（新增两个属性）

## 关键设计决策

- 新增 `RunId: Guid?` —— 轮次分组标识，可空兼容存量数据，属性紧跟 `SessionId` 放置（同为分组标识）
- 新增 `IsFinal: bool` —— 轮次终点标记，默认 false，属性紧跟 `IsCompacted` 放置（同为状态标记）
- 不触碰 Core 层 `src/AIShop.Core/Entities/ChatEntities.cs`（设计决策 G2：轮次字段只落在 Infrastructure 持久化实体，不污染领域模型）
- 注释使用中文，遵循项目 `.claude/rules/dotnet.md`

## 遗留问题

- 无。T1 仅实体加属性，无 schema/运行时副作用；`run_id`/`is_final` 列映射、索引、Store/压缩/读取逻辑由后续工单（T2/T4-T9）处理

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 234 通过、AIShop.McpServer.Tests 11 通过，共 245，0 失败
- T1 自身无独立测试用例（实体默认值断言属 T1T 工单，不在本工单范围）
