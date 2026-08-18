# handoff-T12 — ChatEndpoints 新增 IsRetryableAgentFailure 重试分类器（方案 A 分类器）

## 工单

- 工单 ID：T12（实现）+ T12T（测试，本 commit 一并完成）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 A（应用层重试），设计来源 `docs/design/agent-failure-handling-design.md` §3.1
- blockedBy：无（与 T11 并行，不同文件无冲突）；T13（catch 重构）blockedBy 本工单

## 改动文件清单

1. `src/AIShop.Api/Features/Chat/ChatEndpoints.cs`
   - 新增 `internal static bool IsRetryableAgentFailure(Exception ex, CancellationToken ct)` 分类器
   - 新增 `using System.ClientModel;`（`ClientResultException` 所在命名空间）
2. `tests/AIShop.Api.Tests/ChatEndpointsTests.cs`（追加，T12T）
   - 新增 `using System.ClientModel;` + `using System.ClientModel.Primitives;`
   - 9 个分类器判定用例（4 条 Theory + 5 个 Fact）+ 测试辅助桩 `StubPipelineResponse`

## 关键设计决策

1. **分类判定表**（严格对应任务描述）：
   - `HttpRequestException` → true（网络抖动）
   - `TimeoutException` → true（超时）
   - `TaskCanceledException` 且调用方 `ct.IsCancellationRequested == false` → true（HttpClient 内部超时）；ct 已取消 → false（调用方主动取消，非可重试失败）
   - `ClientResultException` 且 `Status is 429 or >= 500` → true（OpenAI/Qwen 路径 429/5xx）；其余状态码 → false
   - 其余（含 `InvalidOperationException`、协议/解析类错误、`KeyNotFoundException`）→ false
   - `KeyNotFoundException` 不落入本分类器：本分类器返回 false，T13 中由更前的独立 `catch (KeyNotFoundException)` 优先处理（400「不支持的模型」不重试）
2. **switch 表达式实现**：四个异常类型互无继承关系，类型模式顺序不影响正确性；`TaskCanceledException when !ct.IsCancellationRequested` 用 when 卫语句区分「HttpClient 超时」与「调用方取消」两种语义。
3. **【关键验证】`ClientResultException` 构造方式与任务描述有偏差**：任务描述提示可用 `new ClientResultException(status, Substitute.For<Response>())`，但当前解析版本 **System.ClientModel 1.14.0**（经 `project.assets.json` 确认）只有两个构造：`ClientResultException(PipelineResponse, Exception)` 与 `ClientResultException(string, PipelineResponse, Exception)`——**无 (int, Response) 构造**（程序集反射 + XML 文档双重验证）。`Status` 是 `get/set` 均可的 `Int32` 属性，值取自传入的 `PipelineResponse.Status`（探针程序实测：构造 `PipelineResponse(503)` → `ex.Status == 503`）。因此测试用显式最小 `PipelineResponse` 子类桩 `StubPipelineResponse`（而非 NSubstitute——`Headers`/`IsErrorCore` 为 protected 抽象成员，mock 设置繁琐），经 `new ClientResultException(new StubPipelineResponse(429), null)` 构造。
4. **`StubPipelineResponse` 需补基类默认参数**：`PipelineResponse.BufferContent`/`BufferContentAsync` 带 `= default` 默认参数值，重写方法须保留（SonarAnalyzer S1006 强制，否则 build 门禁拦截）。
5. **只落分类器、不改 catch**：本工单严格限定「新增分类器 + 测试」，`/api/chat` catch 重构属 T13 范围，未触碰。

## 遗留问题

- **无阻塞遗留**：分类器判定表与任务描述逐条对齐，T13 可直接按 `catch (KeyNotFoundException)` 优先 + `catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))` 次序接用。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。
- `tasks.md` T12/T12T 行 `[ ]` → `[x]` 勾选：本 commit 不含 tasks.md 改动（如 hooks 门禁拦截则由编排方委派处理）。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。构建前清理了会话前残留的 Aspire AppHost 进程（`aspire stop`，其锁定了 AppHost 输出文件导致 MSB3026）。
- 专项：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~IsRetryableAgentFailure"` —— **9/9 通过**（Theory 4 例：HttpRequestException→true / TimeoutException→true / InvalidOperationException→false / KeyNotFoundException→false；Fact 5 例：TaskCanceled 未取消→true、TaskCanceled 调用方取消→false、ClientResult 429→true、503→true、400→false），32ms。
- 所属测试类全量：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ChatEndpointsTests"` —— **28/28 通过**（含既有 ChatEndpointsTests 用例，无回归），7s。
- 测试资源清理：无临时 DB 文件残留（`test_*.db` 空）；未启动 Api/Aspire 进程。
