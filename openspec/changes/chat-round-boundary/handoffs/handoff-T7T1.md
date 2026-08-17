# handoff-T7T1 — 超 4K 条 → 强制压最旧整轮达硬上限（测试）

## 工单

- 工单 ID：T7T1
- 状态：已完成
- 所属变更：chat-round-boundary（spec「条数硬上限 4K 兜底」）
- blockedBy：T7（压缩安全阀兜底实现，见 handoff-T7）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`
  - 新增测试 `Store_ExceedsHardLimit4K_CompressesOldestWholeRound`
  - 新增 seed 辅助方法 `SeedHugeCompleteRound(int rowCount)`（单个极大完整轮）

## 关键设计决策

- **用例构造（隔离阶段 3 硬上限）**：seed 单个极大完整轮（4100 行同一 run_id，末条 `IsFinal=true`）+ Store 落 1 个新完整轮（2 行）= 未压缩 4102 行 > 4096。此时完整轮仅 2 个 ≤ K=12、无未完成轮，阶段 1/2（按轮数裁剪 / 未完成轮上限）均不会触发，压缩完全由阶段 3 硬上限驱动 —— 干净隔离出「4K 硬上限」这一安全阀，证明其独立生效。
- **断言维度**：
  - 压缩只标 `is_compacted=true` 不物理删除 → 总行数 4102 不变。
  - 硬上限达成 → 未压缩条数压到 ≤ 4096（实际仅剩新轮 2 行）。
  - 整轮整切不拆轮 → 同一 run_id 组内所有行压缩状态一致（组内无半轮被拆散）。
  - 压的是最旧整轮 → 压缩行恰为 id 最小的 4100 行（极大轮），保留区仅剩新轮（user + assistant），run_id 与极大轮不同。
- **seed 性能**：4100 行走 EF `AddRange` + 单次 `SaveChanges`，in-memory SQLite 下测试总时长约 1s，无需原生 SQL 批量插（工单备注的「可走原生 SQL」为可选优化，非必需）。未采用原生 SQL 以避免 Guid 在 TEXT 列上的存储格式与 EF 读写不一致的风险。
- **Warning 日志未断言**：阶段 3 的 Warning 日志（Serilog）未挂 sink 断言，沿用既有测试约定（仅代码走查确认调用正确），与 handoff-T7 遗留说明一致。
- **xUnit 分析器合规**：`Assert.Single(uncompacted.Select(...).Distinct())` 替代 `Assert.Equal(1, ...Count())`，过 xUnit2013 门禁。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T7T1 行标 `[x]` 受 `.claude/hooks/check_gateway.py` 门禁（该文件强制由 @task-breaker 编辑，检测到实际调用方非 task-breaker 即拦截）。若本次编辑被拦截，需由编排方委派 @task-breaker 完成该行 `[ ]` → `[x]`（仅该行，不动其他工单），与 T1-T7 相同机制。
- **T7T2 未实现**：未完成轮上限 5 / NULL 计数口径的专项测试属下一测试工单（T7T2），blockedBy T7，独立 commit。

## 测试情况

- `dotnet build`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --filter FullyQualifiedName~Store_ExceedsHardLimit4K_CompressesOldestWholeRound` 通过（1s）。
- 全量回归：`dotnet test` —— AIShop.Api.Tests 247 通过、AIShop.McpServer.Tests 11 通过，共 258，0 失败。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留（仅 MSBuild 持久节点正常驻留）。
