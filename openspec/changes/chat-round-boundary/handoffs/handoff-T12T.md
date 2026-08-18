# handoff-T12T — 重试分类器各异常类型判定测试（方案 A 分类器测试）

## 工单

- 工单 ID：T12T（测试）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 A（应用层重试），设计来源 `docs/design/agent-failure-handling-design.md` §3.1
- blockedBy：T12（分类器实现）；T12T 被 T13（catch 重构）依赖
- 说明：T12T 的分类器测试用例随 T12 commit（bbe3496）一并提交并通过专项验证（见 `handoff-T12.md`）；本 handoff 为 T12T 工单收尾记录（任务跟踪补勾选 + 文档闭环）

## 改动文件清单

1. `tests/AIShop.Api.Tests/ChatEndpointsTests.cs`（追加，T12T，随 T12 commit 已提交）
   - 新增 `using System.ClientModel;` + `using System.ClientModel.Primitives;`
   - 9 个分类器判定用例：4 条 Theory（`IsRetryableAgentFailure_ClassifiesByExceptionType`）+ 5 个 Fact + 测试辅助桩 `StubPipelineResponse`
   - 用例覆盖（对应任务描述逐条）：
     - `HttpRequestException` → true（网络抖动）
     - `TimeoutException` → true（超时）
     - `TaskCanceledException` 且调用方 ct 未取消 → true（HttpClient 超时语义）；ct 已取消 → false（调用方主动取消）
     - `ClientResultException`：Status=429 → true、Status=503 → true、Status=400 → false
     - `InvalidOperationException` → false（确定性失败不重试）；`KeyNotFoundException` → false（不落入分类器，T13 独立 catch 优先）
2. `openspec/changes/chat-round-boundary/handoffs/handoff-T12T.md`（本文件，新建）
3. `openspec/changes/chat-round-boundary/tasks.md`：T12T 行 `[ ]` → `[x]`（**被 check_gateway.py 规则 4 拦截**——tasks.md 仅限 @task-breaker 编辑，本 subagent（workflow-subagent）写入被 BLOCK，SendMessage 委派 @task-breaker 也不可达（No agent named 'task-breaker'）；该勾选交由编排方/@task-breaker 完成）

## 关键设计决策

1. **测试用 Theory 参数化 + Fact 定向覆盖**：四个基础类型（HttpRequestException/TimeoutException/InvalidOperationException/KeyNotFoundException）走 `[Theory]` + `[InlineData]`（各类型均有公共无参构造，`Activator.CreateInstance` 即可）；TaskCanceledException 的 ct 两态语义与 ClientResultException 的 429/503/400 状态码分支用独立 Fact 精确命名（`_TaskCanceledWithoutCallerCancellation`/`_TaskCanceledByCallerToken`/`_ClientResult429/503/400`）。
2. **`ClientResultException` 构造方式**：当前 System.ClientModel 1.14.0 仅有 `ClientResultException(PipelineResponse, Exception)` 与 `(string, PipelineResponse, Exception)` 构造（无 `(int, Response)` 构造），`Status` 取自传入 `PipelineResponse.Status`。因此测试用显式最小 `PipelineResponse` 子类桩 `StubPipelineResponse(status)`（而非 NSubstitute——`Headers`/`IsErrorCore` 为 protected 抽象，mock 繁琐），经 `new ClientResultException(new StubPipelineResponse(429), null)` 构造。桩类按 SonarAnalyzer S1006 保留 `BufferContent`/`BufferContentAsync` 的 `= default` 默认参数（build 门禁强制）。
3. **纯单测不启 WebApplicationFactory**：分类器是 `internal static` 纯函数，直接调用 `ChatEndpoints.IsRetryableAgentFailure(ex, ct)` 断言，无需工厂与 LLM mock；`ct` 取 `CancellationToken.None`（未取消）与 `CancellationTokenSource.Cancel()` 后的令牌（已取消）区分两态。
4. **只落测试、不改分类器实现**：本工单严格限定测试验证，`ChatEndpoints.cs` 分类器实现属 T12 范围未触碰；`/api/chat` catch 重构属 T13 范围未触碰。

## 遗留问题

- **tasks.md 勾选待编排方处理**：T12T 行 `[ ]` → `[x]` 被 `.claude/hooks/check_gateway.py` 规则 4 拦截（tasks.md 仅限 @task-breaker 编辑，检测到实际调用方为 workflow-subagent 即 BLOCK）；本 subagent 尝试 SendMessage 委派 @task-breaker 失败（No agent named 'task-breaker' is reachable）。与 T12 先例一致（handoff-T12 注明"如 hooks 门禁拦截则由编排方委派处理"），请编排方/@task-breaker 完成该行勾选。本 commit 不含 tasks.md 改动。
- **无阻塞遗留**：T13 可直接按 `catch (KeyNotFoundException)` 优先 + `catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))` 次序接用分类器。
- T12T 测试用例随 T12 commit 已提交，本 commit 不含测试代码改动（避免空 diff），仅补 tasks.md 勾选与 handoff 文档。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 专项：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ChatEndpointsTests" --nologo` —— **28/28 通过**（含 T12T 的 9 个分类器用例 + 既有 ChatEndpointsTests 用例，无回归），7s。
- 测试资源清理：`ChatEndpointsTests` 为 WebApplicationFactory 集成测试，测试结束后无残留 dotnet 进程；临时 DB（`test_*.db`）随工厂释放由框架清理，未发现残留。
