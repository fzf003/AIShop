# handoff-T10T2 — RunChatAsync 正常返回后该轮存在 is_final=true 终点行（测试）

## 工单

- 工单 ID：T10T2
- 状态：已完成
- 所属变更：chat-round-boundary（spec「Run 后兜底补标 is_final」#5 +「轮次完成判断基于 is_final 查询」#7）
- blockedBy：T10（`ShoppingAssistantAgent.RunChatAsync` 生成 run_id 写 StateBag + Run 正常返回后调 `_provider.MarkRoundFinalAsync(runId)` 兜底补标，见 handoff-T10）

## 改动文件清单

- `tests/AIShop.Api.Tests/ShoppingAssistantAgentRunTests.cs`（唯一代码改动文件，T10T1 新建测试类中追加 2 个用例，无实现代码改动）
  - 用例 1 `RunChatAsync_NormalReturn_RoundHasIsFinalTerminalRow`（主判定链路）：
    mock 返回纯文本 assistant 回复（无 tool_calls），跑一次 `RunChatAsync` →
    断言 ①一轮消息落库且共享同一 `run_id` ②该轮存在**且仅有唯一**的 `is_final=true` 终点行
    ③终点行即最后一条（max id）、角色为 assistant、Content 为最终回复（经 `StripAgentReplyJson` 提取的 "模拟回复"）
    ④首条 user 行非终点
  - 用例 2 `RunChatAsync_ToolOnlyNoTextEnding_RunTimeBackfillMarksLastRowFinal`（Run 后兜底补标链路）：
    mock 每轮 FICC 迭代都只返回 `assistant(FunctionCallContent)`（工具 `add_to_cart(productId=999, quantity=0)`
    数量非法短路返回，不触碰真实 DI/DB），循环至 `MaximumIterationsPerRequest=3` 耗尽后 `RunChatAsync` 正常返回 →
    断言 ①一轮消息落库且共享同一 `run_id` ②该轮存在**且仅有唯一**的 `is_final=true` 终点行
    ③终点行即最后一条（max id）④终点行**不是纯文本 assistant**（role==assistant 且 ToolCalls 为空为假）——
    本轮末条恒为 tool/FCC 行、Store 内判定（T5）只对末条纯文本 assistant 落标，故该 `is_final=true` 只能来自
    `RunChatAsync` 返回后 `_provider.MarkRoundFinalAsync(runId)` 的兜底补标，即 Agent→Provider 补标链路真实接通

## 关键设计决策

- **两个用例覆盖 spec #5 的双写入路径**（design.md §5「Store 判定为主 + Run 后兜底补标」）：
  - 主路径（用例 1）：正常纯文本回复 → Store T5 落 `is_final=true`，验证「RunChatAsync 正常返回后该轮存在 is_final=true 终点行」的常规形态（终点行 = 末条纯文本 assistant 最终回复）
  - 补标路径（用例 2）：仅调工具未输出文本 → Store T5 不落标，`RunChatAsync` 返回后由 `MarkRoundFinalAsync` 兜底补标到 tool/FCC 行，验证「Run 后兜底补标链路接通」——这是唯一能证明 `RunChatAsync` 确实调用了 `MarkRoundFinalAsync`（而非仅靠 Store 主判定）的用例
- **补标语义的 tool/FCC 落点已由 T9T1 覆盖**（tasks.md 明确分工）：本工单不再重复构造/断言 Provider 层补标细节，只做 Agent→Provider 链路端到端验证；用例 2 中终点行为 tool/FCC 行的断言作为链路接通的逻辑证据（Store T5 不会给 tool/FCC 行落标）
- **tool 短路设计**：`add_to_cart(999, 0)` 因 `quantity <= 0` 在进入 DI/DB 前返回，测试无需注册商品/用户/购物车仓储；FCC 每迭代用自增 `call_{n}` 唯一 CallId，避免 MAF 工具调用 ID 冲突
- **与 T10T1 分工**：T10T1 验证 run_id 两轮独立（本轮范围外），T10T2 只验证 is_final 终点行链路（本工单范围），互不重复

## 遗留问题

- **tasks.md 勾选待办**：`tasks.md` 中 T10T2 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁拦截——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即 BLOCKED），本子代理直接编辑被拦截。需由编排方委派 @task-breaker 完成该行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T10 相同机制。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 等与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build`（TreatWarningsAsErrors 已在 Directory.Build.props 启用）：0 错误 0 警告。
- 专项测试：`dotnet test --no-build --filter FullyQualifiedName~ShoppingAssistantAgentRunTests` —— 3/3 通过（T10T1 1 个 + T10T2 新增 2 个），复跑一次确认确定性。
- 全量回归：`dotnet test --no-build` —— AIShop.Api.Tests 261 通过（原 259 + 新增 2）、AIShop.McpServer.Tests 11 通过，共 272，0 失败，无回归。
- 测试资源清理：用例使用 in-memory SQLite（`:memory:`），无临时 DB 文件；测试后无 testhost 残留。
