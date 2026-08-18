# handoff-T11 — ModelRouter.CreateChatClient 接入标准弹性策略（方案 C HTTP 层）

## 工单

- 工单 ID：T11（实现）+ T11T（测试，本 commit 一并完成）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 C（HTTP 层弹性策略），设计来源 `docs/design/agent-failure-handling-design.md` §3.2
- blockedBy：无（与 T12/T13 并行，不同文件无冲突）

## 改动文件清单

1. `src/AIShop.Api/Agents/ModelRouter.cs`
   - 新增 `internal static HttpMessageHandler BuildChatHttpPipeline(HttpMessageHandler inner, bool enableDebugHandler)` 测试缝
   - `CreateChatClient` 的 DeepSeek 与 OpenAI/Qwen 两条路径由 `new HttpClient(httpHandler)` 换为 `new HttpClient(BuildChatHttpPipeline(handler, _enableDebugHandler))`；删除原 `httpHandler` 局部变量；`HttpClient.Timeout=120s` 保留
   - 新增 `using Microsoft.Extensions.Http.Resilience;` + `using Polly;`
2. `src/AIShop.Api/AIShop.Api.csproj` — InternalsVisibleTo 由 property 形式改为 item 形式（支撑 internal 测试缝，见关键设计决策 5）
3. `tests/AIShop.Api.Tests/ModelRouterResilienceTests.cs`（新建，T11T）— 2 个 Fact + 本地 `SequenceStubServer`（复用 `ServiceDefaultsDebugTests.HttpStubServer` 的 TcpListener 模式、支持按序返回状态码）

## 关键设计决策

1. **【重要偏差】`AddStandardResilienceHandler(ResiliencePipelineBuilder)` 在锁定版本不存在**：任务描述的 `new ResiliencePipelineBuilder().AddStandardResilienceHandler(o => o.TotalRequestTimeout = TimeSpan.FromSeconds(110))` 在当前锁定包 `Microsoft.Extensions.Http.Resilience` 10.7.0（ServiceDefaults.csproj 锁定）中**无此重载**——经编译错误 + 程序集完整公开方法清单双重验证，`AddStandardResilienceHandler` 只有 `IHttpClientBuilder` 版本。改用公开策略积木等效复现标准弹性（语义一致、无需升级依赖）：
   - `.AddTimeout(TimeSpan.FromSeconds(110))`（最外层）——对应 TotalRequestTimeout=110s 整体预算，弹性先到点
   - `.AddRetry(new HttpRetryStrategyOptions())`——标准 HTTP 重试（默认 `ShouldRetry=IsTransient`，覆盖 429/5xx/网络抖动，≤3 次，尊重 Retry-After）
   - `.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions())`——标准熔断
   - 管线类型 `ResiliencePipelineBuilder<HttpResponseMessage>`（`ResilienceHandler` 需要 `ResiliencePipeline<HttpResponseMessage>`）
2. **DebugHandler 包装链保留**：`new ResilienceHandler(pipeline) { InnerHandler = new DebugHandler(inner, enableDebugHandler) }`——DebugHandler 包在弹性层内层，每次重试尝试都可见，不破坏既有包装链（T11T 结构断言验证）。
3. **不共享同一 DelegatingHandler 实例**：两路径各自 `new HttpClient(BuildChatHttpPipeline(handler, _enableDebugHandler))`，每次调用新建完整链。
4. **双层超时无冲突**：`HttpClient.Timeout=120s` 外层硬天花板 > 弹性总超时 110s——弹性先到点、外层兜底，无双重超时冲突。
5. **InternalsVisibleTo 修复（支撑测试缝）**：原 `<InternalsVisibleTo>AIShop.Api.Tests</InternalsVisibleTo>` 写在 PropertyGroup 内是 **property 形式，此 SDK（10.0.100）不生成 friend 特性**（经验证生成的 `obj/.../AIShop.Api.AssemblyInfo.cs` 无该特性，与历史 handoff-T3 learnings「InternalsVisibleTo 在完整解决方案构建中未完全生效」一致）。改为 `<ItemGroup><InternalsVisibleTo Include="AIShop.Api.Tests"/></ItemGroup>` item 形式后特性正确生成（scratch 项目 + 本仓库构建双重验证）。

## 遗留问题

- **`AddStandardResilienceHandler(ResiliencePipelineBuilder)` 重载缺失**：若后续把 ServiceDefaults 的 `Microsoft.Extensions.Http.Resilience` 升级到含该重载的版本，可将 `BuildChatHttpPipeline` 简化为一行配置；当前等效实现（Retry + CircuitBreaker + TotalTimeout=110s）已覆盖方案 C 全部意图，不阻塞后续工单。
- **DeepSeek 路径 429 吞兜底行为未改动**：符合 Phase 6 边界（`docs/design/agent-failure-handling-design.md` 明确不要求改 `DeepSeekChatClient` 抛异常行为），弹性重试耗尽后仍走既有兜底。
- 工作树中 `appsettings.json`、`tests/.../ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动，与本工单无关，未纳入 commit。
- `tasks.md` T11/T11T 行 `[ ]` → `[x]` 勾选：本 commit 不含 tasks.md 改动（如 hooks 门禁拦截则由编排方委派处理）。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 专项：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ModelRouterResilienceTests"` —— **2/2 通过**（`ShouldAutoRetry429AndSucceed_WhenStubReturns429Then200`：stub 首 429(Retry-After:0) 次 200 → 请求成功且 stub 恰收 2 次；`ShouldReturnResilienceHandlerWithDebugHandlerInner_WhenBuildingPipeline`：外层 ResilienceHandler、内层 DebugHandler），343ms。
- 合并过滤防回归：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ModelRouterResilienceTests|FullyQualifiedName~ChatEndpointsTests"` —— **21/21 通过**（含 ChatEndpointsTests 既有用例，T11 未触碰 ChatEndpoints.cs），7s。
- 测试资源清理：`SequenceStubServer` 用 TcpListener loopback:0 动态端口 + `await using` 释放；无临时 DB 文件；未启动 Api 进程（构建前清理了会话前残留的 AIShop.Api.exe 锁定进程）。
