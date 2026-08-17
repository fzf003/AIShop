# handoff-T9T2 — 已有 is_final → 不重复补标；runId 无行 → 静默返回（测试）

## 工单

- 工单 ID：T9T2
- 状态：已完成
- 所属变更：chat-round-boundary（spec「轮次完成判断基于 is_final 查询」#7 的幂等与静默语义测试侧）
- blockedBy：T9（`MarkRoundFinalAsync(runId)` 实现，见 handoff-T9）

## 改动文件清单

- `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs`（新增 2 个专项用例，置于 T9T1 的两个 `MarkRoundFinal_*` 用例之后）
  - `MarkRoundFinal_RoundAlreadyHasFinalRow_DoesNotReMark`：轮内已有 `is_final=true` 终点行 → 调用 `MarkRoundFinalAsync(runId)` 后不重复补标
  - `MarkRoundFinal_UnknownRunId_ReturnsSilently`：runId 无对应行 → 静默返回不抛异常，DB 无新增无变更

## 关键设计决策

- **AnyAsync 短路守卫的判别式设计**：`MarkRoundFinal_RoundAlreadyHasFinalRow_DoesNotReMark` 故意把既有终点行放在中间（`user → assistant(is_final=true) → tool`），末条残留一行 tool——若实现退化为「不查 `AnyAsync`、直接给 max id 补标」，会在末条行上产生第二个 `is_final=true`。断言「该轮 `IsFinal=true` 行恰好 1 条」可精确捕获该回归，且证明 `AnyAsync(m => m.RunId == runId && m.IsFinal)` 短路生效。
- **未知 runId 静默路径**：`MarkRoundFinal_UnknownRunId_ReturnsSilently` 预置另一轮次（不同 run_id）数据，调用一个不存在的 runId 后断言：不抛异常、该会话行数不变、无行被误补标、既有轮 run_id 未被触碰——对应 T9 实现中 `FirstOrDefaultAsync` 返回 null 时直接 `return` 的分支。
- **自包含 seed、不依赖共享 helper**：与 T9T1 一致，直接以已知 runId seed 行，未复用内部生成 runId 不外露的共享 helper；命名沿用本文件既有 `{Action}_{Context}_{Expected}` 风格。
- **不改动实现**：本工单为纯测试新增，不触碰 `SqliteChatHistoryProvider.cs`（T9 实现已提交）。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T9T2 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）规则 4 门禁——tasks.md 强制由 @task-breaker 编辑（`check_agent_identity` 校验 agent_type 非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T9T2 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T9T1 相同机制。
- **T10/T10T1/T10T2 未实现**：`ShoppingAssistantAgent` 接线（run_id 生成写 StateBag + Run 后调用 `MarkRoundFinalAsync` 兜底补标）属独立实现/测试工单（blockedBy T9），由编排方另行调度。本 commit 只做 T9T2 的幂等/静默语义验证。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build src/AIShop.Api/AIShop.Api.csproj -warnaserror`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test tests/AIShop.Api.Tests/AIShop.Api.Tests.csproj --filter FullyQualifiedName~MarkRoundFinal` —— 4/4 通过（T9T1 两个 + T9T2 两个），1s。
- 全量回归：`dotnet test tests/AIShop.Api.Tests/AIShop.Api.Tests.csproj -warnaserror` —— 258 通过，0 失败（原 256 + 新增 2），无回归。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
