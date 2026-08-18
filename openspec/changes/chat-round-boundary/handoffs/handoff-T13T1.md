# handoff-T13T1 — A 测试：重试成功 / 双失败路径（方案 A 测试）

## 工单

- 工单 ID：T13T1（测试）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 A（应用层重试），设计来源 `docs/design/agent-failure-handling-design.md` §3.1
- blockedBy：T13（/api/chat catch 重构应用重试，已提交 6c83dce）；本工单为 T13 的两个测试工单之一（另一为 T13T2）

## 改动文件清单

1. `tests/AIShop.Api.Tests/ChatEndpointsTests.cs`（追加，T13T1）
   - 新增 `ShouldReturnRetriedReply_WhenFirstRunChatCallThrowsHttpRequestException`：mock `IShoppingAssistantAgent.RunChatAsync` 首次抛 `HttpRequestException`、第二次返回正常回复（`AgentChatResult("重试成功回复", [], null)`）→ `/api/chat` 响应 `Response` 含「重试成功回复」、`RunChatAsync` 恰被调用 2 次（重试成功路径，对应「重试成功则返回正常回复」）
   - 新增 `ShouldReturnFallback_WhenRetryAlsoThrowsHttpRequestException`：mock 两次都抛 `HttpRequestException` → `/api/chat` 响应 `Response` 含「抱歉，暂时无法处理您的请求」、`RunChatAsync` 恰被调用 2 次（对应「重试也失败才兜底」）
   - 沿用 `Chat_WhenAgentThrows_SetsActivityErrorAndReturnsFallback` 的 `WebApplicationFactory` + mock router 模式：`ReplaceWithIsolatedDb` 隔离库 + `RemoveAll<ModelRouter>` + mock `IShoppingAssistantAgent`；`mockRouter.ActiveModel.Returns("qwen")` + `GetAgent(Arg.Any<string>()).Returns(agent)`（重试走 `router.ActiveModel` 拿同一 mock agent）
2. `openspec/changes/chat-round-boundary/handoffs/handoff-T13T1.md`（本文件，新建）
3. `openspec/changes/chat-round-boundary/tasks.md`：T13T1 行 `[ ]` → `[x]`（**被 `.claude/hooks/check_gateway.py` 规则 4 拦截**——tasks.md 仅限 @task-breaker 编辑，检测到实际调用方为 workflow-subagent 即 BLOCK；与 T12T 先例一致，勾选交由编排方/@task-breaker 完成）

## 关键设计决策

1. **NSubstitute 回调队列模拟"首次失败→重试成功"**：`agent.RunChatAsync(...).Returns<Task<(AgentChatResult, AgentSession)>>( _ => throw new HttpRequestException("网络抖动"), _ => Task.FromResult((new AgentChatResult("重试成功回复", [], null), Substitute.For<AgentSession>())))` —— 第 1 次抛异常、第 2 次返回正常结果的先后序列由 `Returns` 多回调参数天然表达，无需自建计数器。
2. **`AgentSession` 为抽象类型不可直接 new**（CS0144），用 `Substitute.For<Microsoft.Agents.AI.AgentSession>()` 构造元组第二元素（端点已丢弃该会话对象，mock 即可）。
3. **`mockAgent` 提升到工厂 lambda 外声明**：`ConfigureServices` 闭包内声明的变量在断言处不可见（CS0103），改为外层 `IShoppingAssistantAgent? mockAgent = null;` 在闭包内赋值、闭包外 `mockAgent!.Received(2)` 断言。
4. **断言口径**：`mockAgent!.Received(2).RunChatAsync(Arg.Any<Guid>(), ...)` 恰好 2 次——首次失败 1 次 + 换默认模型重试 1 次；重试成功后 endpoint 直接复用 `result`（`Response` 含「重试成功回复」），重试仍失败才走 R11 OTel Error + 兜底文案。
5. **只落测试、不改实现**：`ChatEndpoints.cs` 的 catch 重试逻辑（T13）未触碰；本工单仅验证 T13 行为不回归。

## 遗留问题

- **tasks.md 勾选待编排方处理**：T13T1 行 `[ ]` → `[x]` 被 `.claude/hooks/check_gateway.py` 规则 4 拦截（tasks.md 仅限 @task-breaker 编辑，检测到实际调用方为 workflow-subagent 即 BLOCK）。与 T12T/T13 先例一致（当前 tasks.md 中 T11-T13T2 均仍为 `[ ]`，由编排方统一补勾），本 commit 不含 tasks.md 改动。
- **无阻塞遗留**：T13T2（非重试异常不重试 / KeyNotFoundException 不重试）为独立测试工单，另行实施。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 所属测试类全量：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ChatEndpointsTests" --nologo` —— **30/30 通过**（28 个既有用例含 T12T 分类器、R11 `Chat_WhenAgentThrows_*` 无回归 + 新增 2 个 T13T1 用例），17s。
- 测试资源清理：无 `test_*.db` 残留于源码目录（bin/Debug 下的 `*.db` 属 build 产物且已被 `.gitignore` 的 `*.db` 规则忽略）；无 testhost/AIShop Api 进程残留。
