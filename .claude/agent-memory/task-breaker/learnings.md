# task-breaker 经验记忆

> 每次完成任务拆解后自动追加。启动时读取，提高估算准确度。

## 历史估算记录

<!-- 记录实际耗时 vs 预估耗时的偏差，用于校准 -->

| 日期 | Change ID | 任务 | 预估 | 实际 | 偏差 | 原因 |
|------|-----------|------|------|------|------|------|
| 2026-07-10 | workflow-approval-short-circuit | Phase8 T24-T29 估算 | 60min | - | - | 参考了 Phase0 T0 的 15min 经验，但任务已在前面描述清楚，此次仅做追加无需重新发现；T25 合并 4 个节点为 15min 而非 4x8=32min 是因为模式统一可批量编写 |
| 2026-07-12 | D--Hermes-Projects-AIShop-docs-design-四合一架构重构 | 任务拆解 | 设计.md 预估 17min（仅实现） | tasks.md 拆分出 33min（实现+测试+验证） | +16min | 设计.md 仅估算了纯实现工时（7min A+B + 10min C），未包含配套测试（15min）和最终验证（5min），tasks.md 按每项实现任务配至少一个测试任务的规则补全了测试覆盖面 |
| 2026-07-12 | 产品浏览增强与Agent优化 | 全量任务拆解 | 76min（汇总所有子任务预估） | - | - | 方向 A（前端 index.html）含 8 项实现 + 10 项手动验证测试（浏览器操作），方向 B（Program.cs 改 1 行）含 1 项实现 + 3 项验证。前端变更无法用自动化测试覆盖（纯 HTML/CSS/JS 页面的 UI 行为），故以手动测试任务为主。注意：此类前端变更场景下，dotnet test 仅验证后端测试不受影响，前端功能需人工验收。 |
| 2026-08-11 | product-catalog-persistence | 设计按 review 修正 + 阶段重排 | - | - | - | 按 review 意见同步修正设计（幂等建表兜底、双轨合一、worker 侧累加、Product 迁独立文件）；阶段重排：T11（ProductRepository 缓存）前置到 T8（ProductCatalog 改造）之前（因 ProductCatalog 改为依赖 IProductRepository）；blocking edges 连锁调整（新增 T1→T11、T6→T11、T11→T8） |
| 2026-08-18 | chat-round-boundary | 拆解 24 工单（9 实现 + 15 测试，方案 B 调整后） | 实现 72min / 测试 117min（合计约 189min） | -（待实施回填） | - | 初版拆 26 工单（10 实现 + 16 测试，含 Program.cs 幂等补列 T3/T3T）；开发拍板 schema 改「方案 B 强制清库」（手动删旧库、不做补列）后移除 T3/T3T，估减 20min。估算依据：改同一大文件 SqliteChatHistoryProvider.cs 按 Store 打标→Store is_final→压缩核心→压缩安全阀→Provide 孤儿→新方法 自然边界串行拆；每实现配测试；Agent 层沿用 RunChatAsyncPreferenceBackfillTests 的 mock IChatClient 模式，故测试估算按既有模式复用成本取值 |
| 2026-08-18 | chat-round-boundary（改） | 方案 A→B 后同步 tasks.md | - | - | - | 移除 T3（Program 幂等补列）与 T3T（补列 e2e 测试）；同步更新 spec 映射表第 15 行（注明方案 B + 部署动作「删旧库文件」）、主干链（去 T3）、mermaid DAG（删 T3/T3T 节点、T2 不再指向 T3）、文件归属（Program.cs 与 ProgramSeedingTests.cs 移出）；其余工单与 blockedBy 不变 |
| 2026-08-18 | chat-round-boundary（勾选回灌） | Step 4 完成 24 工单 + Step 5 清理工单 | - | - | - | 24 工单已实施（git log 24 commit + handoffs 24 文件），但 implementer 写 tasks.md 被 check_openspec_gate.py 拦截（tasks.md 仅 task-breaker 可写）→ 由 task-breaker 回灌勾选 [x]；Step 5 评审发现 ShoppingAssistantAgentRunTests.cs 第 6 行 unused using（CS8019）→ 新增 T-FIX-1（blockedBy: 无，独立最后跑）并同步 DAG |
| 2026-08-18 | chat-round-boundary（追加 Phase 6） | 方案 A+C 追加拆解（7 工单：实现 3 + 测试 4） | 实现 26min（T11 10 + T12 6 + T13 10）/ 测试 38min（T11T 10 + T12T 8 + T13T1 10 + T13T2 10）合计约 64min | -（已实施，未逐项计时回填） | - | 基于 docs/design/agent-failure-handling-design.md §3 已定稿的 A/C 方案直接拆解，无需重新发现；A 按「分类器 → catch 重构」自然边界拆两个实现工单（分类判定与控制流可独立验收），C 保持单工单（设计文档标注"一行接线"）；T11 与 T12 不同文件（ModelRouter.cs vs ChatEndpoints.cs）无 blocking edge 可并行；方案 B 明确存档不实施，不占工单 |
| 2026-08-18 | chat-round-boundary（追加 Phase 6 勾选回灌） | 7 工单已实施 + 回灌勾选 | -（沿用上文 64min 拆解预估） | -（未逐项计时） | - | 依据：git log 7 commit（T11/T11T/T12/T12T/T13/T13T1/T13T2）+ handoffs/ 7 handoff 文件齐全；implementer 勾选 tasks.md 被 check_gateway.py 规则 4 拦截（tasks.md 仅限 @task-breaker 编辑）→ 由 task-breaker 回灌 [x] 并更新实施状态行为「已全部实施完成（7 commit + 7 handoff 齐全）」；各 handoff 均报告 build 0 错 0 警 + 对应测试类全量通过（ChatEndpointsTests 32/32、ModelRouterResilienceTests 新增用例等），无阻塞遗留 |

## 任务粒度经验

<!-- 哪些类型的任务容易拆得太大或太小 -->

- 同文件多个功能点（如 Store 的 run_id 打标 / is_final 判定 / 压缩重构）即使都在一个大方法里，也按「写侧→读侧→新方法」的自然边界拆成独立工单，每工单 ≤10min 且可独立验证，避免把一个大方法一次改到位（难以独立验收、diff 过大）。
- 压缩算法这类「核心逻辑 + 若干兜底安全阀」的结构，拆成「核心整轮整切（K=12）」与「安全阀（硬上限/未完成轮上限/计数口径）」两个工单：核心先落地并更新既有断言，安全阀作为增量层再加测试。
- 追加实施项（如 Phase 6 A/C）若方案已在设计文档定稿，直接按「分类器/控制流」「实现/测试」自然边界拆，不必重新发现；两个实现工单若在不同文件（ModelRouter.cs vs ChatEndpoints.cs）可并行，不加 blocking edge。

## 测试任务模式

<!-- 项目中常用的测试方式/框架 -->

- 单测/集成测试：xUnit v3 + NSubstitute + in-memory SQLite（`SqliteConnection("DataSource=:memory:")` + `IDbContextFactory` substitute），已有 `tests/AIShop.Api.Tests`，无需新建测试项目。
- Program 启动引导（播种/建表/补列 DDL）的 e2e 测试：`ProgramSeedingTests` 模式——临时文件库 + `WebApplicationFactory<Program>.WithWebHostBuilder` 替换 DB 注册（`ReplaceDbWith`），可测「旧 schema 库带新代码启动」路径；测试后 `SqliteConnection.ClearAllPools()` + 删临时 db 文件（置于 `DisableParallelization` 串行集合避免并行宿主竞争）。
- Agent 层（RunChatAsync）测试：`RunChatAsyncPreferenceBackfillTests` / `ShoppingAssistantAgentRunTests` 模式——NSubstitute mock `IChatClient.GetResponseAsync` 返回固定 JSON 回复 + in-memory SQLite 构造 `ShoppingAssistantAgent`，可端到端验证 StateBag 写入、落库字段、DB 副作用。
- 端点重试/异常兜底测试：`ChatEndpointsTests.Chat_WhenAgentThrows_SetsActivityErrorAndReturnsFallback` 模式——`WebApplicationFactory` + `RemoveAll<ModelRouter>` + mock `IShoppingAssistantAgent.RunChatAsync`（`Returns` 按调用次数编排「首抛/次成」），可验证重试调用次数与最终响应；分类器单测用 `internal static` 方法 + `InternalsVisibleTo`（Api csproj 已配）直接调用。
- HTTP 层弹性策略测试：`ServiceDefaultsDebugTests.HttpStubServer` 模式——`TcpListener` 本地 stub 返回「首次 429(Retry-After:0) → 二次 200」计数请求，配合 `ModelRouter` 内新增 `internal` 测试缝（`BuildChatHttpPipeline`）验证自建 HttpClient 带上了标准弹性重试。

## 新增经验

- 设计文档的工时估算通常只覆盖实现，不会包含配套测试。tasks.md 需额外预算 1.5x-2x 的测试与验证时间。
- DI 相关改动（构造函数签名变更 + 注册行新增）虽然实现仅 8min，但涉及容器解析正确性和运行时行为两项独立验证，至少需要 2 个测试任务。
- 纯前端 UI 变更（HTML/CSS/JS）无法用 dotnet 单元测试覆盖，应拆分为手动浏览器验证任务，每项验证对应 spec.md 中一条具体行为。
- 改动现有行为（如压缩阈值从条数 50 改为轮次 K=12）时，既有断言测试必然被破坏——须在对应实现工单的测试配套里显式列出「更新既有断言」任务，否则实现工单无法以绿 build 收口。
- 测试工单里若包含「既有测试用例要补数据形态适配」（如 Provide 孤行 tool 用例在新配对规则下需补 run_id 或相邻 assistant-FCC），要在任务描述中点名具体用例，避免实施时漏改。
- 拆解后若开发决策拍板变更（如 schema 方案 A 幂等补列 → B 强制清库），tasks.md 须同步三处才一致：spec 映射表对应行（注明新方案 + 把「部署动作」如手动清库标为部署事项而非代码工单）、主干执行顺序/DAG（删除受影响节点、剪掉指向边）、文件归属（受影响文件移出清单）。其余工单 ID 与 blockedBy 保持稳定，避免连锁重编号。
- tasks.md 有写权限门禁（仅 task-breaker 可写，implementer 被 check_openspec_gate.py 拦截）→ Step 4 完成后由 task-breaker 回灌勾选 [x]；勾选依据 = handoffs/ 下对应 handoff 文件存在 + git log 有对应 commit。Step 5 评审发现的清理项（如 unused using CS8019）追加为独立 T-FIX-N 工单（blockedBy: 无、DAG 加独立节点、单独 commit），保持与已实施工单解耦。
- 追加实施项若超出 spec.md 范围（如 Phase 6 A/C），tasks.md 需显式标注「不在 spec 16 条内」并在 spec 映射表外维护 traceability（引用设计文档 §3），避免 implementer 误以为漏了 spec 映射行；方案 B 存档不实施要写进边界说明，防止被误拆。
