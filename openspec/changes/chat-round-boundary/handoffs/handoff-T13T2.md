# handoff-T13T2 — A 测试：非重试异常不重试 / KeyNotFoundException 不重试（方案 A 测试）

## 工单

- 工单 ID：T13T2（测试）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 A（应用层重试），设计来源 `docs/design/agent-failure-handling-design.md` §3.1
- blockedBy：T13（/api/chat catch 重构应用重试，已提交 6c83dce）；本工单为 T13 的两个测试工单之一（另一为 T13T1）

## 改动文件清单

1. `tests/AIShop.Api.Tests/ChatEndpointsTests.cs`（追加，T13T2）
   - 新增 `ShouldReturnFallback_WhenRunChatThrowsInvalidOperationException`：mock `IShoppingAssistantAgent.RunChatAsync` 抛 `InvalidOperationException`（非重试类型，`IsRetryableAgentFailure`→false）→ `/api/chat` 响应 `Response` 含「抱歉，暂时无法处理您的请求」、`RunChatAsync` 恰被调用 1 次（确定性失败不重试，保持既有 R11 行为，不进入重试分支）
   - 新增 `ShouldReturnBadRequest_WhenRunChatThrowsKeyNotFoundException`：mock `RunChatAsync` 抛 `KeyNotFoundException` → `/api/chat` 返回 400 且 `detail == "不支持的模型"`、`RunChatAsync` 恰被调用 1 次（T13 独立 catch 优先，不被重试逻辑覆盖，对应方案 A「KeyNotFoundException 不重试」）
   - 沿用 T13T1 的 `WebApplicationFactory` + mock router 模式：`ReplaceWithIsolatedDb` 隔离库 + `RemoveAll<ModelRouter>` + mock `IShoppingAssistantAgent`；`mockRouter.ActiveModel.Returns("qwen")` + `GetAgent(Arg.Any<string>()).Returns(agent)`（KeyNotFoundException 用例 endpoint 独立 catch 内 `logger.Error` 读取 `router.ActiveModel` 需 mock）
2. `openspec/changes/chat-round-boundary/handoffs/handoff-T13T2.md`（本文件，新建）
3. `openspec/changes/chat-round-boundary/tasks.md`：T13T2 行 `[ ]` → `[x]`（**被 `.claude/hooks/check_gateway.py` 规则 4 拦截**——tasks.md 仅限 @task-breaker 编辑，检测到实际调用方为 workflow-subagent 即 BLOCK；与 T12T/T13/T13T1 先例一致，勾选交由编排方/@task-breaker 完成）

## 关键设计决策

1. **mock 抛异常用单回调**：`agent.RunChatAsync(...).Returns<Task<(AgentChatResult, AgentSession)>>(_ => throw new InvalidOperationException("agent boom"))`——抛异常在 NSubstitute 回调内直接表达，无需返回桩。
2. **InvalidOperationException 落入非重试兜底路径**：endpoint catch 链 `KeyNotFoundException` → `catch (Exception) when (IsRetryableAgentFailure)` → 通用 `catch (Exception)`；`InvalidOperationException` 分类器返回 false，落入通用 catch → R11 OTel Error + 兜底「抱歉…」，`RunChatAsync` 仅 1 次（不重试）。
3. **KeyNotFoundException 验证独立 catch 优先**：该异常既被最前独立 `catch (KeyNotFoundException)` 命中（400），分类器本身对 KeyNotFoundException 也返回 false——双保险断言「不被重试逻辑覆盖」；400 断言 `detail == "不支持的模型"`（与既有 `Chat_WithNonExistentModel_ReturnsBadRequest` 同口径）。
4. **断言口径**：`mockAgent!.Received(1).RunChatAsync(...)` 恰好 1 次——非重试异常/独立 catch 场景均不触发重试；与 T13T1 的 Received(2) 形成对照（重试 1 次 vs 不重试）。
5. **只落测试、不改实现**：`ChatEndpoints.cs`（T13）与 `IsRetryableAgentFailure` 分类器（T12）均未触碰；本工单仅验证 T13 行为不回归。

## 遗留问题

- **tasks.md 勾选待编排方处理**：T13T2 行 `[ ]` → `[x]` 被 `.claude/hooks/check_gateway.py` 规则 4 拦截（tasks.md 仅限 @task-breaker 编辑，检测到实际调用方为 workflow-subagent 即 BLOCK）。与 T12T/T13/T13T1 先例一致（当前 tasks.md 中 T11-T13T2 均仍为 `[ ]`，由编排方统一补勾），本 commit 不含 tasks.md 改动。
- **无阻塞遗留**：Phase 6 方案 A/C 全部工单（T11/T11T/T12/T12T/T13/T13T1/T13T2）已实施完成。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 所属测试类全量：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ChatEndpointsTests" --nologo` —— **32/32 通过**（30 个既有用例含 T12T 分类器、T13T1、R11 `Chat_WhenAgentThrows_*` 无回归 + 新增 2 个 T13T2 用例），9s。
- 单点确认：`dotnet test ... --filter "FullyQualifiedName~ShouldReturnFallback_WhenRunChatThrowsInvalidOperationException|FullyQualifiedName~ShouldReturnBadRequest_WhenRunChatThrowsKeyNotFoundException"` —— **2/2 通过**。
- 测试资源清理：无 `test_*.db` 残留于源码目录（bin/Debug 下的 `*.db` 属 build 产物且已被 `.gitignore` 的 `*.db` 规则忽略）；无 testhost/AIShop Api 进程残留。
