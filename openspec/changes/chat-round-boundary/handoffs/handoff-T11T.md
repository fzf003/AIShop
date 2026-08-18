# handoff-T11T — 本地 stub 429→200 重试成功 + ResilienceHandler→DebugHandler 链结构断言（测试）

## 工单

- 工单 ID：T11T
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 C（HTTP 层弹性策略），设计来源 `docs/design/agent-failure-handling-design.md` §3.2
- blockedBy：T11（`ModelRouter.BuildChatHttpPipeline` 测试缝 + `CreateChatClient` 两路径接入标准弹性，见 handoff-T11）

## 改动文件清单

- `tests/AIShop.Api.Tests/ModelRouterResilienceTests.cs`（新建，T11T 测试类）——2 个 Fact + 本地 `SequenceStubServer`（复用 `ServiceDefaultsDebugTests.HttpStubServer` 的 TcpListener 模式、支持按序返回状态码）。
  - 该文件由 T11 提交（8cb2861，handoff-T11 注明「T11（实现）+ T11T（测试，本 commit 一并完成）」）一并落地；本工单对既有代码**无新增改动**，完成验证 + 任务跟踪（handoff + tasks.md 勾选）。
  - 用例 1 `ShouldAutoRetry429AndSucceed_WhenStubReturns429Then200`（行为断言）：
    本地 stub 首次返回 429（带 `Retry-After: 0`）、第二次返回 200 → 经 `BuildChatHttpPipeline` 缝构建的 HttpClient（DebugHandler 关闭、`Timeout=120s` 保留）请求成功、stub 恰好收到 2 次请求，响应 body 为 stub 的 200 内容——429 被标准弹性自动重试后成功（对应方案 C「mock 429 → 重试后成功」）。
  - 用例 2 `ShouldReturnResilienceHandlerWithDebugHandlerInner_WhenBuildingPipeline`（结构断言）：
    `BuildChatHttpPipeline` 返回的 handler 外层为 `ResilienceHandler`（承载标准弹性策略）、其 `InnerHandler` 为 `DebugHandler`——DebugHandler 包在弹性层内层、每次重试尝试可见，既有包装链不被破坏（对应方案 C「不能破坏 DebugHandler 链」）。

## 关键设计决策

- **行为断言用真实 TCP stub 而非 mock handler**：经测试缝构建与生产 `CreateChatClient` 完全相同的管线（外层 `ResilienceHandler{ InnerHandler = DebugHandler }`），打真实 loopback 端口验证标准弹性确实在 429 上自动重试——比 mock DelegatingHandler 更贴近生产路径（retry 会重开 TCP 连接）。
- **`Retry-After: 0` 保证立即重试**：429 响应带 `Retry-After: 0`，标准弹性策略尊重该头、不引入休眠等待，测试快速确定；stub 按序返回 `(429, body)` → `(200, body)`，`Interlocked.Increment` 计数请求序号，`RequestCount == 2` 精确证明「恰好一次重试」，排除首轮直接成功或重试多次。
- **结构断言直接断言类型链**：`Assert.IsType<ResilienceHandler>` + `Assert.IsType<DebugHandler>(resilienceHandler.InnerHandler)`，无需反射，若 T11 将来改用 `IHttpClientFactory` 重构管线，该断言会显式失败而非静默通过。
- **stub 复用既有 TcpListener 模式**：`SequenceStubServer` 对齐 `ServiceDefaultsDebugTests.HttpStubServer`（fire-and-forget 每连接独立处理 + Content-Length body 读取），仅增加「按序返回状态码序列」能力，避免重复造轮子。

## 遗留问题

- **tasks.md 勾选待办**：T11T 行 `[ ]` → `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁拦截——tasks.md 强制由 @task-breaker 编辑。本子代理直接编辑若被拦截，需由编排方委派 @task-breaker 完成该行勾选（与 T1-T12 相同机制，仅动 T11T 行）。
- **测试代码随 T11 提交落地**：`ModelRouterResilienceTests.cs` 在 T11 提交（8cb2861）中已一并提交，本工单不再产生新的 .cs 改动；若编排方要求独立 T11T commit，其中仅含 handoff + tasks.md 等文档/跟踪文件。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 专项：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ModelRouterResilienceTests"` —— **2/2 通过**（`ShouldAutoRetry429AndSucceed_WhenStubReturns429Then200`：stub 首 429(Retry-After:0) 次 200 → 请求成功且 stub 恰收 2 次；`ShouldReturnResilienceHandlerWithDebugHandlerInner_WhenBuildingPipeline`：外层 ResilienceHandler、内层 DebugHandler），209ms。
- 测试资源清理：`SequenceStubServer` 用 TcpListener loopback:0 动态端口 + `await using` 释放；无临时 DB 文件；未启动 Api 进程。
