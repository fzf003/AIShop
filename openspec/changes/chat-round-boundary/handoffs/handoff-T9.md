# handoff-T9 — 新增 MarkRoundFinalAsync(runId)：无 is_final 则末条补标；已 true 不动；无行静默返回

## 工单

- 工单 ID：T9
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Run 后兜底补标 is_final」#5 +「补标操作收敛于 Provider 单一写入口」#6 +「轮次完成判断基于 is_final 查询」#7）
- blockedBy：T8（Provide 孤儿 tool 检查升级，见 handoff-T8）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（新增 `public Task MarkRoundFinalAsync(Guid runId, CancellationToken cancellationToken = default)`，置于 StoreChatHistoryAsync 之后、GetSessionId 之前）

## 关键设计决策

- **补标逻辑（spec #5 / #7）**：先 `AnyAsync(m => m.RunId == runId && m.IsFinal)` 判断本轮是否已有终点行——已 true 则直接返回不重复补标；false 时按 `OrderByDescending(Id)` 取该轮最后一条（max id）补标 `IsFinal=true`。
- **补标落点语义（评审 Y1）**：补标可能落在 tool/FCC 行上，此时该行是「轮次终点」但不是「最终回复」，语义以「终点标记」为准。覆盖 Store 内判定（T5）漏掉的「仅调工具未输出文本」等场景。
- **静默返回**：runId 无对应行时 `FirstOrDefaultAsync` 返回 null → 直接 return，不抛异常、不记错日志（记一条 Information 仅发生在真正补标时）。
- **职责边界（评审 Y3）**：补标写入口收敛在本方法，不新增其他写入口；Agent（ShoppingAssistantAgent，T10）只调用本方法，不直接操作 DbContext。查询与写在同一 DbContext 实例内完成，保证读-改-写一致性。
- **签名带 CancellationToken（可选默认值）**：对齐项目异步规范（I/O 操作支持取消），`= default` 不破坏 design.md §9.3 的 `MarkRoundFinalAsync(Guid runId)` 调用语义，T10 接线时可将 RunChatAsync 的 token 透传。

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T9 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理（implementer）上下文无 task-breaker 可达，未强行绕过流程门禁。需由编排方委派 @task-breaker 完成 `tasks.md` T9 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T8 相同机制。
- **T9T1 / T9T2 未实现**：专项测试（T9T1：末条为 tool/FCC 行补标 is_final=true；T9T2：已 true 不重复补标 + runId 无行静默返回）属后续独立测试工单，blockedBy T9。本 commit 只做 T9 实现，测试工单由编排方另行调度。
- 代码库中 `appsettings.json`（用户本地模型切换）、`.claude/agent-memory/*`、`.vs/*`、`docs/design-chat-round-boundary.md` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build --warnaserror`：0 错误 0 警告。
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 254 通过、AIShop.McpServer.Tests 11 通过，共 265，0 失败。
- 本工单无新增测试（T9T1/T9T2 属独立测试工单）；现有 Store/Provide 相关用例（T4/T5/T6/T7/T8 及其测试）全部保持通过，确认未引入回归。
