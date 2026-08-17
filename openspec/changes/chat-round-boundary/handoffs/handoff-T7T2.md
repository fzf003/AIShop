# handoff-T7T2 — >5 未完成轮 → 最旧强制压；run_id=NULL 单行组不计入（测试）

## 工单

- 工单 ID：T7T2
- 状态：已完成
- 所属变更：chat-round-boundary（spec「未完成轮上限 5 个兜底」#11 + 「未完成轮计数口径只统计有效轮组」#12）
- blockedBy：T7（压缩安全阀兜底实现，见 handoff-T7）+ T7T1（4K 硬上限测试，独立 commit）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`
  - 新增测试 `Store_MoreThan5IncompleteRounds_ForceCompressesOldestIncompleteRound`
  - 新增测试 `Store_IncompleteRoundsWithNullRows_NullSingleRowGroupsNotCounted`
  - 新增 seed 辅助方法 `SeedIncompleteRounds(int roundCount)`（每轮 2 行、共享 run_id、无 is_final 的有效未完成轮）

## 关键设计决策

- **用例 1（未完成轮上限 5 兜底，spec #11）**：seed 6 个有效未完成轮（run_id 非空且组内 ≥2 行、无 is_final）+ Store 落 1 个新完整轮。此时完整轮仅 1 个 ≤ K=12，阶段 1（按完整轮数裁剪）不触发；未完成轮 6 > 上限 5，阶段 2 强制压最旧 1 个未完成轮。干净隔离出「未完成轮上限」这一安全阀。断言：总行数 14 不变（只标 is_compacted）、被压的恰为 id 1-2（最旧未完成轮，整轮整切）、其余 5 个未完成轮 + 新完整轮保留、保留的未完成轮组内仍无 is_final（保持未完成语义）、同一 run_id 组内压缩状态一致（无半轮拆散）。
- **用例 2（NULL 单行组不计入计数，spec #12 / 评审 Y2）**：6 个有效未完成轮 + 10 条 `run_id=NULL` 单行组 + 新完整轮。若 NULL 行误计入未完成轮计数（16 > 5）会误压 11 个最旧组波及 NULL 行；正确口径下 NULL 行被 `RunId is not null && Ids.Count >= 2` 过滤，仍只压最旧 1 个未完成轮。断言：10 条 NULL 行（ids 13-22）原样保留（run_id 为空、未被压缩）、被压的仍仅为 id 1-2、未压缩 22 行 = 5 未完成轮（10 行）+ 10 NULL + 新完整轮（2 行）。本用例 10 条 NULL + 1 新轮 = 11 完整轮 ≤ K=12，阶段 1 不触发，从而把「NULL 不计入未完成轮」与「NULL 走阶段 1 完整轮裁剪」两条路径隔离清楚。
- **seed 形态**：未完成轮用 2 行（user + assistant、无 is_final）而非 3 行工具轮 —— 计数口径只关心「run_id 非空且 ≥2 行、组内无 is_final」，不关心 FCC↔tool 配对（那属 T6T1/T8 职责），保持用例精简。
- **Warning 日志未断言**：阶段 2 的 Warning 日志（Serilog）未挂 sink 断言，沿用既有测试约定（仅代码走查确认调用正确），与 handoff-T7/T7T1 遗留说明一致。
- **xUnit 分析器合规**：避免 `Assert.Equal(1, ...Count())` 触发 xUnit2013；`Assert.DoesNotContain(compacted, r => r.IsFinal)` 与 `Assert.All` 表达均过门禁。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T7T2 行标 `[x]` 受 `.claude/hooks/check_gateway.py` 门禁（该文件强制由 @task-breaker 编辑，检测到实际调用方非 task-breaker 即拦截）。若本次编辑被拦截，需由编排方委派 @task-breaker 完成该行 `[ ]` → `[x]`（仅该行，不动其他工单），与 T1-T7 相同机制。
- **后续工单未实现**：Provide 孤儿 tool 同 run_id 组内配对过滤（T8）及补标方法（T9）、Agent 接线（T10）均属后续工单，blockedBy 各自前置。

## 测试情况

- `dotnet build -warnaserror`：0 错误 0 警告。
- 专项测试：`dotnet test --filter FullyQualifiedName~Store_MoreThan5IncompleteRounds|FullyQualifiedName~Store_IncompleteRoundsWithNullRows` 通过（2/2，1s）。
- 全量回归：`dotnet test` —— AIShop.Api.Tests 249 通过、AIShop.McpServer.Tests 11 通过，共 260，0 失败。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
