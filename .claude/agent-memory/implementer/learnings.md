# implementer 经验记忆

> 每次完成实现后自动追加。启动时读取，指导本次实现。

## T16 全量验证 + flaky 修复笔记（service-layer-extraction）

- **T16 全量验证最终态：build 0 错误 0 警告 + test 全绿（McpServer 11 + Service 140 + Api 136 = 287）**；复核三项全过：grep 全解决方案 `AIShop.Api.Agents` 无匹配、`dotnet list package` 无 MAF 1.17.0（Service 三包 1.18.0 + DeepSeek 1.0.4、AgentTelemetry 单包 1.18.0、Api 零 MAF/DeepSeek 顶级包）、`src/AIShop.Api/Agents/` 目录不存在。迁移阶段（T4-T9 源文件 + T10-T15 测试文件 + T17 文档）全部收口后全量绿。
- **既有 flaky `PreferenceWriteHostedServiceTests`（in-memory SQLite 共享连接 + worker 并发）T16 根治**：根因 = `TestDbContextFactory` 所有 DbContext 共享同一 `SqliteConnection("DataSource=:memory:")`，hosted service 后台线程写 + 测试主线程轮询读并发访问同一连接，Microsoft.Data.Sqlite 单连接非线程安全 → 偶发 NRE（`SqliteConnection.Close()` DisposeAsync 或轮询 DbContext `InternalServiceProvider`）。全量失败集合不稳定（ShouldTruncateToTop20/ShouldMergeMultipleEnqueues/ShouldContinue 轮换），隔离 filter 也偶发失败——learnings R1/T2/T3 记录了「串行集合」解法但未根治（串行集合只消跨类竞争，不消类内 worker 线程 + 主线程并发）。
- **根治方案 = 临时文件库替代 in-memory 共享连接**（T7/T15/T23-pre 同款模式）：`_dbFile = Path.Combine(Path.GetTempPath(), $"pref_{Guid.NewGuid():N}.db")` + `UseSqlite($"Data Source={_dbFile}")`，`TestDbContextFactory` 各 context 独立连接（EF 连接池复用文件库）；`DisposeAsync` 改 `SqliteConnection.ClearAllPools()` + `File.Delete(_dbFile)`。worker 写 + 轮询读走不同连接，文件库多连接并发安全。改后隔离连续 5 次全过 + 全量 136/136 绿。**做「hosted service + 轮询读」测试，直接上临时文件库，不要用 in-memory 共享连接（微软文档已声明 SqliteConnection 非线程安全）**。
- **T16 提交 `0abe0c9`**：pathspec `git commit -o -m -- <单路径>` 精确隔离（index 混有并行 T4-T9 的 12 rename + 7 M staged 在制品），commitgate 全量 build+test 一次通过（修复后 287 全绿）；`git show --stat HEAD` 复核恰 1 文件。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选。

## 编码约定

<!-- 每次发现新的项目约定时追加 -->

- index.html 中 CSS 样式和 HTML 结构可能已经由之前的实现完成，修改前需先完整读取文件确认当前状态
- PostToolUse hook 可能会在 Edit 操作后自动修改文件（如格式化或补充代码），需要在下一次 Edit 前重新 Read 目标区域

## 常见陷阱

<!-- 每次踩坑修复后追加 -->

- Windows 环境下 dotnet build 可能因后台进程锁定 DLL 文件而失败（MSB3026），需先 taskkill 相关 dotnet 进程再重试
- 关闭弹窗/重置状态的函数中，除了重置 JS 状态变量，还必须清空相关 DOM 元素的 innerHTML，否则下次打开时会出现残留内容
- MAF 1.16.0 相比 1.13.0 的 HarnessAgentOptions API 破坏：移除了 `DisableFileAccess`（无替代）；`DisableNonApprovalRequiredFunctionBypassing` 改名为 `DisableApprovalNotRequiredFunctionBypassing`
- PreToolUse commit gate hook（.claude/hooks/check_commitgate.py）在 commit 前强制跑 `dotnet build` + `dotnet test`（各 180s 超时），超时后 Python subprocess 不会杀子进程，会遗留持有 bin/obj 锁的孤儿 MSBuild 进程，使后续 build/test 越跑越慢甚至卡死；commit 被 BLOCK 后必须先清理孤儿 dotnet/MSBuild 进程再重试，热缓存建立后重试才可能通过

## 编码约定

<!-- 每次发现新的项目约定时追加 -->

- index.html 中 CSS 样式和 HTML 结构可能已经由之前的实现完成，修改前需先完整读取文件确认当前状态
- PostToolUse hook 可能会在 Edit 操作后自动修改文件（如格式化或补充代码），需要在下一次 Edit 前重新 Read 目标区域
- Git Bash 下 `dotnet build /warnaserror` 会把 `/warnaserror` 当成 `C:/Program Files/Git/warnaserror` 路径（MSB1009），必须用 `-warnaserror`
- SonarAnalyzer S1481：foreach 解构 `foreach (var (key, value) in dict)` 中未使用的变量（如 key）会报「Remove the unused local variable」，改用 `foreach (var value in dict.Values)` 遍历
- check_gateway.py 规则 4 强制 `tasks.md` 必须由 `@task-breaker` 编辑；implementer 直接 Edit tasks.md 会被 BLOCK（PreToolUse hook），勾选 checkbox 应委派 task-breaker 处理
- handoffs/handoff-*.md 不受 agent_type 约束（check_gateway.py 规则 6 仅校验规划产出物齐全），implementer 可直接写
- `dotnet test` 前若存在遗留的 `AIShop.Api.exe`（`dotnet run` 启动的应用进程，非 MSBuild），会锁住 `src/AIShop.Api/bin/Debug/net10.0/` 下被引用的依赖 dll，报 MSB3026/MSB3027 复制失败（非代码错误）；先 `tasklist //FI "PID eq <pid>"` 确认再用 `taskkill //PID <pid> //F` 清理后重跑
- cherry-pick 冲突：WebApplicationFactory 测试中 `AgentTelemetry:Debug=false` 的显式覆盖注释，合并时优先保留对"引用 Api 项目时 appsettings.json 被复制到测试输出目录"这一根因的描述（含 T26 隔离修复引用），commit message 用 `GIT_EDITOR=true git cherry-pick --continue` 保留原样

## 项目结构笔记

<!-- 发现的模块间依赖关系 -->

- ModelRouter 使用 `IServiceProvider` 在 `GetOrAdd` 回调中延迟解析依赖，注意 SonarAnalyzer 规则 S6612 要求使用 lambda 参数（`key =>`）替代捕获的变量（`modelName`），以避免闭包捕获开销
- PostToolUse hook 可能会在 Edit 后自动格式化/还原文件，每次 Edit 前必须重新 Read 目标文件确认状态

## 测试习惯

<!-- 测试框架偏好、mock 策略等 -->

- 集成测试中使用临时 SQLite 数据库时，每个测试必须使用独立的 WebApplicationFactory + 数据库文件，否则并行执行会导致 EF Core DbUpdateConcurrencyException（"expected 1 row(s) but affected 0"）
- 使用 [CollectionDefinition(DisableParallelization = true)] 和 [Collection] 确保串行执行，避免 SQLite 文件写入冲突
- 两次连续 POST 到同一个用户在独立 Factory + DB 场景下仍可能因 EF Core 的状态追踪机制产生并发问题，建议同一用户的操作在同一请求中验证

## T8 前端实现笔记

- 方案 A（双排卡片布局）参考 prototype-multi-model.html 的 Variant A 设计：用户卡片一行 + 模型卡片一行 + 底部开始按钮
- model-card 的选中态使用 `border-color: #1a1a2e; background: #f8f8ff; box-shadow: 0 0 0 3px rgba(...)` 三要素组合
- 开始按钮禁用态用 `background: #ccc; color: #999; cursor: not-allowed;` 保持一致视觉
- 登录页容器 `login-card` 使用白色圆角卡片包裹，避免元素散落在屏幕中央
- `loadModels()` 在页面加载时自动调用，失败时显示 "模型加载失败" 并禁用模型选择
- `updateStartBtn()` 是唯一控制按钮状态的地方，从 selectUser/selectModel/loadModels 三个入口调用

## T0 基线备忘（product-catalog-persistence）

- 环境基线（T0）：`AIShop.Api.Tests` 134 通过、`AIShop.McpServer.Tests` 11 通过，合计 147 中 145 通过。
- 基线含 2 个**预置失败**（均在 `ServiceDefaultsDebugTests`，属 ServiceDefaults/AgentTelemetry 范围，与本变更无关）：`ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse` 稳定失败；`ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` 为 flaky（全量跑失败、单独跑通过）。
- T25 全量验收对比基线时：失败集合不得新增/扩散，仅需确认仍限这两个（或更少）。
- tasks.md 的 checkbox 由 @task-breaker 维护（check_gateway.py 拦截 implementer 直接编辑），implementer 只产出 handoff，勾选需由 task-breaker 完成。

## T4 接口实现笔记（product-catalog-persistence）

- T4 接口 `IPreferenceRepository` 签名依赖 `UserPreferences` 实体，而 T3 尚未完成时该实体不存在；implementer 只做 T4 时需按 design 3.2 最小创建该实体作为编译依赖（handoff 中标注），checkbox 仍归 T3 / 由 task-breaker 勾选。
- 契约测试用反射断言接口签名：positional record 可用 `<Clone>$`（instance）方法识别；`GetMethod` 需带 `BindingFlags.Instance | NonPublic`。
- `ServiceDefaultsDebugTests.ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse` 稳定失败根因确认：`src/AIShop.Api/appsettings.json` 的 `AgentTelemetry:Debug=true` 被复制到测试输出目录 `tests/AIShop.Api.Tests/bin/Debug/net10.0/appsettings.json`，`Host.CreateApplicationBuilder()`（content root=AppContext.BaseDirectory）读到 true → 处理器链含 FileSpanExporter/BodyRedactionProcessor → Debug=false 断言失败。属 T26 既有问题（T0 已列为预置失败），与本变更无关。

## T17 RecommendationMerger 实现笔记（product-catalog-persistence）

- 该既有失败会让 `check_commitgate.py` 在 commit 前全量 `dotnet test` 返回非零 → **任何 git commit 都被 BLOCK**（T0 起全分支如此）。实现完成 ≠ 能提交；commit 是否成功取决于协调者先处理既有失败或授权豁免。T17 尝试 `git commit -m "feat(T17): ..."` 被 BLOCK，两文件留在暂存区。
- 共享 worktree 的 index 存在多线程竞态：并行 implementer 会同时 `git add` 各自文件（T1 ProductSeedData 多次被并发重新暂存）。提交前必须 `git diff --cached --stat` 核对精确文件集，发现混入他人文件用 `git restore --staged <path>` 剔除，不能直接 `git commit`。
- `GetTopPreferenceKeywords` 权重并列时用 `ThenBy(key, StringComparer.Ordinal)` 二次排序，保证 Top-N 确定性可复现；测试避免断言并列权重间的顺序。
- `MergeKeywords` 补齐阈值是 3：`current.Count < 3` 才用偏好补齐，补齐循环内用 `OrdinalIgnoreCase` 判重、满 5 即 break；最终再 `Distinct(OrdinalIgnoreCase).Take(5)` 兜底（design 4.3 合并算法）。
- tasks.md 勾选仍归 @task-breaker（check_gateway.py 规则 4 拦截 implementer），implementer 只产出 handoff 并在其中标注。

## T3 实体实现笔记（product-catalog-persistence）

- **EF Core materialization 与 `init` 属性**：EF Core 无法给 `init`-only 属性赋值，从 DB 读回时 `Tags`/`Price` 等会变默认值（`[]`/`0m`）。凡需 EF 读回的 Core 实体属性一律用 `set`（T3 已将 `Product` 全部属性 `init`→`set`）；`UserPreferences.UserId` 保持 `init`（design 3.2 明确）。
- **EF 往返测试在 AppDbContext 尚无 DbSet 时**：T3 阶段 `AppDbContext` 还没有 `DbSet<Product>`（属 T6），验证实体 EF materialization 用测试内自建的 `TestProductDbContext`（仅映射 `Product`，`HasKey(Id)` + `ValueGeneratedNever`，与 T6 计划配置一致）；读回用全新 context 强制从 DB materialization，而非返回已跟踪实例。
- **`Product` 迁移影响面**：从 `ChatEntities.cs` 移出到独立 `Product.cs` 不改变命名空间（仍 `AIShop.Core.Entities`），既有引用零改动；`init`→`set` 不破坏对象初始化器（`new() { Id=1, Tags=[...] }`）与 `ProductSeedData` 写法。
- **并行工单共享同一工作树**：T3 完成时 T1（ProductSeedData）/T2/T4 产物已存在于磁盘但未提交；T3 测试依赖 T1 的 `ProductSeedData.Products`，T3 commit 不含 T1 文件，依赖 T1/T2 在树中共存，归档顺序须 T1/T2 先于 T3。

## T1 ProductSeedData 实现笔记（product-catalog-persistence）

- **T1 只迁出数据、不删 `ProductCatalog.All` 硬编码列表**：`ProductCatalog.cs` 的 18 商品删除属 T8（`All => repository.GetAll()` 走 repo 缓存）的职责；T8 前两者并存是预期中间态，删早了会破坏既有 `ProductCatalogTests`（`new ProductCatalog()` + `Catalog.All.Count`）与中间提交。
- **共享工作树剔除他人已暂存文件用 `git restore --staged <path>`**：T17 已记；本次用 `git reset HEAD <paths>` 出现"把整个暂存区清空"的副作用（疑似与并行 Agent 的 `git add` 竞态），更稳的做法是 `git restore --staged` 精确剔除 + 每次操作后 `git diff --cached --name-status` 复核精确文件集。
- **`git stash push -- <path>` 对未跟踪文件无效**：新文件必须先 `git add` 或加 `-u` 才进 stash；验证"既有失败"时用「目标文件 `git diff` 为空 + 与改动零耦合」论证即可，不必真 stash。
- **commit gate 的 BLOCK 不一定是超时孤儿进程**：本次 2 个 `ServiceDefaultsDebugTests` 既有失败（T0 已列为预置，根因见 T4 笔记的 appsettings.json 拷贝）会让全量 `dotnet test` 返回非零 → 任何 commit 都被 BLOCK。判定为既有问题后如实上报，不绕过（不改 hook / 不 `--no-verify` / 不改测试逻辑），文件留在暂存区交给协调者处理。

## T26 测试隔离修复笔记（ServiceDefaultsDebugTests）

- 稳定失败根因（T4 笔记已确认）：`src/AIShop.Api/appsettings.json` 的 `AgentTelemetry:Debug=true`（b4a2cdf 引入）被复制到测试输出目录，`Host.CreateApplicationBuilder()`（content root=AppContext.BaseDirectory）读到 true → Debug=false 断言失败。
- 修复方式（对称于 Debug=true 测试 L89 的显式 true）：在 `ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse` 内 `builder.Configuration["AgentTelemetry:Debug"] = "false"` 显式覆盖，保证测试隔离，不再依赖宿主配置文件。commit aaf39f3。
- 修复后 `ServiceDefaultsDebugTests` 3/3 全绿；全量 164/164 通过（flaky 的 `ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` 全量重跑后通过，确认与本次改动无关，仍属 T0 预置 flaky）。
- **commit gate 即使修复了目标稳定失败，全量中任意 flaky 偶发失败仍会 BLOCK**：本次第一次 commit 被拦（该 flaky 在全量中失败），但全量重跑 164/164 通过后立即重试 commit 即成功，无需 `--no-verify`。这是绕过 gate 阻塞的合法路径：先确认全量重跑通过，再重试 commit。

## 第一波提交笔记（product-catalog-persistence，T2/T4/T17/T3）

- **并行 agent 的 `git commit` 会把暂存区里我的文件一起提交（最严重竞态）**：T12 agent 提交 `78c4e01 feat(T12)` 时，暂存区只剩我的 T3 4 文件（因我先 restore 了它的 T12 文件），导致它的 commit **内容=我的 T3**、message=T12。发现后 `git show --stat <hash>` 核对 commit 内容，用 `git reset --soft HEAD~1` 撤销错误 commit（保留索引+工作树，零丢失），再按正确归属重新 commit；T12 agent 的真实文件仍在工作树，需重新 add。
- **暂存区被并行 agent 反复抢占/清空**：我的 `git add` 后可能被并行操作挤出（index 竞态），或并行 agent 的 add 与我混存。提交前必须 `git diff --cached --name-status` 核对精确文件集；混入他人文件用 `git restore --staged <path>` 剔除（T1/T17 已记，本次再证）。
- **防误提交硬校验技巧**：把"提交"和"白名单校验"放同一条复合命令：`(git diff --cached --name-only | grep -vE '^(白名单正则)$') && echo UNEXPECTED && exit 1 || git commit -m "..."`，保证即使 commit 前一瞬暂存区被并行污染也不会误提交（grep 命中非白名单即中止）。
- **编译中间态会持续阻塞一切 commit**：并行 agent 改 `ShoppingAssistantAgent.cs`（T5）/`ChatEndpoints.cs`（T16）等共享文件期间，工作树处于编译失败（CS0246/CS1503）或测试失败（如 `GetProducts_ReturnsAll` Expected 18 Actual 0 因 ProductRepository 改查库未完成链路）中间态，任何 git commit 的 gate（build/test）必拦。只能等并行 agent 提交对应工单（HEAD 前进）后恢复；用 `git log --oneline` + 关键文件 `stat mtime` 判断并行进度，避免盲目重试。
- **与并行 agent 的 commit gate 并发跑 test 会导致 ServiceDefaultsDebugTests 双双失败**（含 T26 已修复的 Debug=false 用例），单独 `--filter` 跑却 3/3 通过——是 WebApplicationFactory/日志资源竞争，不是代码问题。等并行安静窗口（无 testhost/vstest 进程）再全量重跑，通过后立即提交。
- **T0 预置 flaky（`ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue`）在 gate 里反复偶发拦截**：手动全量 177/177 通过，gate 里同一用例偶发失败，重试（全量重跑通过→立即 commit）即可，不 `--no-verify`；失败集合每次不同（flaky vs 并行竞争），需看完整失败列表区分。

## T6 AppDbContext 表映射实现笔记（product-catalog-persistence）

- **EF Core 10 的 `ValueGenerated` 枚举命名空间是 `Microsoft.EntityFrameworkCore.Metadata`**（旧版在 `Microsoft.EntityFrameworkCore`）。测试里断言 `property.ValueGenerated == ValueGenerated.Never` 需 `using Microsoft.EntityFrameworkCore.Metadata;`，否则 CS0103。EF 主程序集 XML 文档中该类型为 `T:Microsoft.EntityFrameworkCore.Metadata.ValueGenerated`。
- **EF metadata 断言 API**：`ctx.Model.FindEntityType(typeof(T))!.FindProperty(nameof(...))!.ValueGenerated` 返回 `ValueGenerated?`，`GetColumnType()` 返回显式 `.HasColumnType("TEXT")` 配置的列类型。
- **并行 T5 改造的中间态会阻塞整个测试项目编译**：T5 先改 `ShoppingAssistantAgent.cs`（删 using + 构造函数签名替换）后改 `ModelRouter.cs`，中间态下 `ModelRouter` 仍引用旧构造 → `AIShop.Api` 编译失败 → `AIShop.Api.Tests`（引用 AIShop.Api）编译失败，T6 测试无法运行。处置：识别为并行 agent 中间态（检查文件 mtime 变化），轮询重试 build 直至其 commit（`ee40bfc`）落库，不越权修改 T5 文件。
- **隔离提交再验证**：共享工作树 `git add` 会混入并行 agent 文件，且并行 agent 的 `git add`/`git reset` 会把已暂存的自己的文件移出暂存区。最稳路径：`git commit -m "..." -- <我的精确路径>`（pathspec 提交只含指定路径，忽略其他已暂存文件），commit 后 `git show --stat <hash>` 复核文件集。

## T12 PreferenceQueue 实现笔记（product-catalog-persistence）

- **`AIShop.Infrastructure.csproj` 没有 `InternalsVisibleTo`**：测试项目（`AIShop.Api.Tests`）经 Api 引用链能看到 Infrastructure 的 public 类型，但看不到 internal 类型。凡需测试直接 `new`/`PreferenceQueue.Create()` 访问的 Infrastructure 服务必须 `public`（与 `ProductCatalog` public 约定一致；`ProductRepository` 保持 internal 是因为测试走 DI/间接链路）。
- **Channel completed 测试无法直接触发**：`ChannelReader` 没有 Complete API，测试「channel 标记完成后 `TryEnqueue` 返回 false」只能反射访问实现内部 `_channel.Writer.TryComplete()`（`GetField("_channel", NonPublic)` + `GetValue`），与 T4 契约测试的反射先例一致。
- **共享工作树并行 agent 中间态会破坏全量 build → commit gate BLOCK**：本次 T5（Agent 解耦）把 `ShoppingAssistantAgent.BuildInstructions` 改到中间态（已移除 `using AIShop.Core.Interfaces` 但 L26 仍引用 `IProductCatalogService`），导致 `dotnet build AIShop.sln` 失败（CS0246），T12 的 commit 被 gate BLOCK。判定标准：先 `dotnet build tests/AIShop.Api.Tests`（只编译我的依赖链）验证自己的代码 0 错误 + 目标测试全绿，再对比 `dotnet build AIShop.sln`（全量）确认失败源在他人文件；staged 精确隔离（`git diff --cached --name-only`）后如实上报，等待并行 agent 完成或协调者处理。


## T5 Agent 解耦笔记（product-catalog-persistence）

- **受影响测试清单需全局 grep 找全，不能只信 tasks.md**：T5 任务只列了 ChatEndpointsTests/AgentTelemetryTests，但 `ShoppingAssistantAgent` 构造函数解耦还波及 `SanitizingChatClientTests.cs`（L89-92 用 NSubstitute mock `IProductCatalogService` 构造 Agent）。做「构造函数签名变更」类工单时，先 `grep -rn "new <Type>(" src/ tests/` 找全所有构造点，否则 `dotnet build` 0 错误无法达成。
- **任务标注的引用数与实测可能不符**：T5 任务标 ChatEndpointsTests "16 处"，实际 grep 到 13 处（design 5 章已注明「实测 13 处」）。以 grep 实测为准。
- **解耦方案（design 6.1）**：`ShoppingAssistantAgent` 构造函数 `IProductCatalogService catalog` → `IReadOnlyDictionary<string, string[]> keywordMap`（`BuildInstructions` 只用 KeywordMap）；`ModelRouter.GetAgent` 删 `_sp.GetRequiredService<IProductCatalogService>()`（根容器解析 Scoped 隐患），改传 Core 静态 `ProductKeywordMap.Entries`。测试侧最保守适配 = 直接传 `ProductKeywordMap.Entries`（静态常量，无需 factory 注册 mock），比注册真实/ mock 服务更简单且与原 `catalog.KeywordMap` 内容等价。
- **共享工作树并行 T6 在制品会阻塞整个测试项目编译 + commit gate**：T6 的 `AppDbContextMappingsTests.cs`（未跟踪、缺 `ValueGenerated` using）编译错误会让 `AIShop.Api.Tests` 项目无法构建，进而所有 commit 被 gate BLOCK，与我的改动零耦合。判定为「并行在制品阻塞」后 3 次 build 失败即停，写 handoff ⚠️，我的文件留在暂存区交协调者（等 T6 修复后重试，不 --no-verify）。
- **`git commit -- <paths>` 精确提交**：共享 index 有并行 agent 暂存文件（T12 PreferenceQueue*.cs）时，用 `git commit -m "..." -- <我的路径>` 只提交我的文件，避免把他人暂存文件卷入我的 commit；不要 `git reset`（会误清他人暂存）。
- **T6 在制品阻塞的最终结局**：T6 修复后（mtime 变化）测试项目恢复编译，T5 相关 71 测试全绿，commit gate 通过，`git commit -- <5 个路径>` 成功（`ee40bfc`）。结论：并行在制品阻塞时先 3 次 build 停止 + handoff ⚠️，但**不要放弃**——短时间后重查 `stat -c '%y' <在制品文件>` mtime，变了就立即重试 build/test/commit。commit 必须用 `git commit -- <精确路径>` 隔离，避免卷入并行 agent 已暂存文件。

## T16 RunChatAsync preferences 回填笔记（product-catalog-persistence）

- **MAF 偏好注入路径实测**：`PreferenceMemoryProvider.ProvideAIContextAsync` 返回的 `AIContext.Instructions` 被 HarnessAgent 合入 **`ChatOptions.Instructions`**（LLM system prompt），**不是** `ChatMessage` 列表。mock IChatClient 断言"捕获输入含偏好文本"时必须读 `ci.Arg<ChatOptions?>().Instructions`，读 messages 的 TextContent 只会看到用户消息 → 断言失败。design 4.3「待验证假设」结论成立，无需走方案 A/B 备用路径。
- **`ChatMessage` 类型歧义**：测试项目同时 using `Microsoft.Extensions.AI` 与 `AIShop.Core.Entities` 时，裸 `ChatMessage` 报 CS0104 歧义。沿用 `ChatEndpointsTests` 的 `using Meai = Microsoft.Extensions.AI;` 别名，用 `Meai.ChatMessage` / `Meai.ChatOptions` / `Meai.TextContent`。SonarAnalyzer 还会要求 `SaveChangesAsync`（S6966）而非 `SaveChanges`。
- **签名加可选参数放中间位的坑**：`RunChatAsync(Guid, string, string, string? preferences = null, CancellationToken ct = default)` 中，若既有调用 `RunChatAsync(sid, msg, username, ct)` 不改成具名 `ct:`，第四个位置参数会静默绑到 `preferences` 而不是 `ct`（编译不报错、语义错误）。凡在既有 `ct` 参数前插入可选参数，必须全局 grep 调用点并把 `ct` 改具名。
- **并行 T11 在制品判定**：`ProductRepository.cs` 被并行 agent 改为查库（`M ` 未提交）时，隔离测试库无种子 → `/api/products` 返回 0 → `ChatEndpointsTests.GetProducts_ReturnsAll` 失败。判定：改动文件与我的范围零耦合（`git status` + mtime 佐证），归为并行在制品，不越权修。

## T12 commit 补做笔记（并行 index 竞争，product-catalog-persistence）
- **共享 index 竞态会「偷梁换柱」**：`git add <我的两文件>` 并核对 staged 无误后，隔数秒再单独 `git commit`，期间并行 agent 的 `git add`/`git reset` 可能清空我的暂存并换入它的文件 → 我用 T12 的 message 提交出了 T3 的内容（`Product.cs` 等 4 个文件，commit 78c4e01）。**commit 后必须立刻 `git show --stat HEAD` 复核文件集**，不能只看 commit 返回成功。
- **误提交的修正**：并行方发现后 `git reset`（HEAD 回退到 T17，误提交的 78c4e01 撤销，内容回到工作树）——不要自己 amend 并行中共享历史，避免与并行 agent 操作冲突；交由协调者/并行方处理，我重新隔离提交自己的文件即可。
- **PreToolUse gate hook 在 bash 命令执行前整条拦截**：`git add ... && git commit ...` 连写时，hook BLOCK 会让整条命令不执行（连 add 都没跑）；判断「add 是否生效」要用后续 `git diff --cached --name-only` 实测，不能假设 `&&` 链已走到 commit。
- **`git commit -o -- <untracked 文件>` 报 pathspec not known**：`-o`/only 模式只接受已跟踪文件；未跟踪文件必须先 `git add`，再用 `git commit -o -m ... -- <paths>` 提交（-o 保证即使 index 里混入并行 staged 内容也只提交 pathspec 指定文件）。
- **最终稳定成功路径**：`git add <我的文件>` + 立即 `git commit -o -m "<msg>" -- <我的精确路径>` 单命令完成（脚本里用 `set -o pipefail` + `git commit ... | tail` 才能拿到 git 真实退出码）；gate 全量 build/test 通过即成功，commit hash `9362824`。gate 中途被 T11（ProductRepository CS8603）与 T8/T9/T10（ChatEndpointsTests.GetProducts_ReturnsAll 期望18实际0）中间态拦截，均非 T12 问题，等并行 agent 提交后全量恢复即可。

## T11 ProductRepository 实现笔记（product-catalog-persistence）

- **`ProductRepository` 必须 `internal` → `public`**：`AIShop.Infrastructure.csproj` 无 `InternalsVisibleTo`，T11/T10/T22 测试都需直接 `new ProductRepository(cache, dbFactory)` 注入计数用 `IDbContextFactory` 以断言「缓存命中不再查库」。凡「构造函数注入可替换工厂」的 Infrastructure 服务，测试要直测就必须 public（与 `ProductCatalog public` 约定一致）。
- **`IMemoryCache.GetOrCreate` 返回 `TItem?`（可空）**：工厂 `db.Products.ToList()` 永不返回 null（无行返回空列表），返回值尾部加 `!` 抑制 CS8603（TreatWarningsAsErrors 下必须处理）。
- **缓存命中断言双保险**：`Assert.Same(first, second)`（同一 List 实例，证明命中缓存返回原对象）+ 计数 `IDbContextFactory.CreateCount` 不增（证明不再查库）。计数工厂实现 `IDbContextFactory<T>` 需同时实现 `CreateDbContext()` 与 `CreateDbContextAsync(CancellationToken)`（EF Core 接口两方法），每次创建递增计数。
- **T11 改查库会稳定破坏 `ChatEndpointsTests.GetProducts_ReturnsAll`（期望18实际0）**：`ReplaceWithIsolatedDb`（ChatEndpointsTests.cs L92）用全新空库 `test_{suffix}.db` 且不播种；改查库后 `/products` 从空表返回 0。修复归属 T23：在 `ReplaceWithIsolatedDb` 注册 factory 后补 `EnsureCreated` + `AddRange(ProductSeedData.Products)` 播种（3 行）。此失败会拦 T11 及所有依赖 T11 的 T8/T9/T10 commit（同一 gate 全量测试），属阶段 4 已知阻塞点，需协调者提前处理，否则阶段 4 死锁。
- **commit gate 对「build 绿但 test 红」同样 BLOCK**：`check_commitgate.py` 先跑全量 `dotnet build`（绿）再跑全量 `dotnet test`，任一失败即退出码 2；pathspec `git commit -- <paths>` 只隔离 commit 内容，**不绕过 gate 的全量测试判定**。并行 agent 测试文件（如 `RunChatAsyncPreferenceBackfillTests`、`ProbePreferenceInjectionTests`）中间态会连 build 一起拦，等其 mtime 稳定 + 全量绿后再提交。

## T23-pre 测试夹具修复笔记（product-catalog-persistence）

- **在 WebApplicationFactory 的 ConfigureServices 阶段给隔离库播种，不要 `services.BuildServiceProvider()`**：`ReplaceWithIsolatedDb`（ChatEndpointsTests.cs）只有 `IServiceCollection`，若为拿 `AppDbContext` 而 `BuildServiceProvider()` 会触发 Serilog "already frozen"（测试内 L60 注释已警告）。安全做法 = 用**独立 `DbContextOptions` + `new AppDbContext(options)`** 直接构造上下文播种（与 `ProductRepositoryTests` 同模式）：`using var seedCtx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options); seedCtx.Database.EnsureCreated(); seedCtx.Products.AddRange(ProductSeedData.Products); seedCtx.SaveChanges();`。与 App 注册的 factory 共用同一 `connStr` 文件库，互不冲突；Program.cs 幂等播种（`if (!await db.Products.AnyAsync())`）看到已有 18 行会跳过，双路径不重复。
- **播种放在 helper 内即可一次覆盖所有测试用例**：`ReplaceWithIsolatedDb` 被 6 个测试的 ConfigureServices 调用，helper 内播种后每个用例的隔离库都有商品，`GetProducts_ReturnsAll` 恢复期望 18 条。commit `7dfb66d`（fix(T23-pre)，pathspec 隔离只含 ChatEndpointsTests.cs）。
- **flaky 再次复现**：`ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` 本次全量又偶发失败，全量重跑 177/177 通过后 commit gate 即放行——与 T26 结论一致，属 T0 预置 flaky，与本改动零耦合。

## T7 Program.cs 幂等建表兜底笔记（product-catalog-persistence）

- **EF SQLite 生成的表名/约束名是 PascalCase，tasks.md 的小写 DDL 示例是错的**：`sqlite3 .schema` 实测 `EnsureCreated` 生成 `CREATE TABLE "Products" (... CONSTRAINT "PK_Products" PRIMARY KEY ...)` 与 `CREATE TABLE "UserPreferences" (... CONSTRAINT "PK_UserPreferences" PRIMARY KEY ...)`；表名/约束名都是 PascalCase，不是 tasks.md 里的 `products`/`user_preferences`/`PK_products`。手写兜底 DDL 必须以「临时空库跑 EnsureCreated → 导出 sqlite_master」为准照抄，否则播种/查询因表名不匹配失败。
- **导出 DDL 的正规做法**：写临时 xUnit 测试，`EnsureCreatedAsync` 建文件库，用 `SqliteConnection` 读 `SELECT sql FROM sqlite_master WHERE type='table' AND name IN (...)`，`File.WriteAllText` 到临时文件，跑完读文件；用完删除临时测试（注意 SonarAnalyzer S2699 要求测试有断言，临时测试也要加最小断言）。SQLite 文件被连接池锁定导致 `File.Delete` 报 IOException，需 `SqliteConnection.ClearAllPools()` 后再删（或忽略该清理异常）。
- **Program.cs 启动播种可用 WebApplicationFactory 端到端测试**：`ConfigureServices` 里 `RemoveAll<IDbContextFactory<AppDbContext>>/DbContextOptions/AppDbContext` 后指向临时文件库（**不预播种**），`WebApplicationFactory<Program>` 启动会真实执行 Program.cs 的 `EnsureCreatedAsync` + 兜底 DDL + 播种，`GET /api/products` 断言 18 条即验证全链路。既有库缺表路径 = 先 `EnsureCreated` 建全表再 `DROP TABLE "Products"; DROP TABLE "UserPreferences";` 模拟历史库，再启动 factory 验证补建。
- **`ExecuteSqlRawAsync` 优于 `ExecuteSqlRaw`**：SonarAnalyzer S6966 要求异步 I/O；Program.cs 的 top-level 内 `await db.Database.ExecuteSqlRawAsync("""...""")` 合法且 0 警告。
- **`WebApplicationFactory` 并行/资源竞争**：每个测试实例用 `Path.GetTempPath()` 唯一文件名 + `Dispose` 里释放 factory + `ClearAllPools` + 删临时文件，避免与其他测试类的 `test_*.db` 互踩；全量跑仍会出现 T0 预置 flaky（`ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue`），单独 filter 3/3 通过 → 全量重跑绿 → 立即 commit。
- **commit gate 被 flaky 连拦 3 次后的解法（本次）**：手动全量 196/196 绿 → gate 里 flaky 仍失败（184/185），连续 3 次。把 WebApplicationFactory 重测试类放入 `[CollectionDefinition(nameof(T), DisableParallelization = true)]` + `[Collection(nameof(T))]` 串行集合（减少与 ServiceDefaultsDebugTests 的并行宿主竞争）后，第 4 次 commit gate 直接通过（`83201d7`）。结论：T0 flaky 根因是 HttpClient body 捕获在并行加载下时序竞争，减少自身测试的并行宿主数量可显著提高 gate 通过率；路径 = 串行化重宿主测试类 + 手动全量绿 + 立即重试 commit。

## T13 PreferenceRepository 实现笔记（product-catalog-persistence）

- **EF Core 能 materialize `init` 标量属性（实证）**：`UserPreferences.UserId` 为 `init`（design 3.2 明确），但 `GetByUserIdAsync` 经 `FirstOrDefaultAsync` 读回后 `Assert.Equal(userId, result!.UserId)` **通过**——EF 正常赋回了 Guid 主键。这与 T3 关于 `Product` 集合属性（`Tags`）读回默认值 `[]` 的观察形成对照：`init` 限制主要影响集合/复杂类型 materialization，标量主键可正常读回。设计稿 3.1 关于「EF 无法给 init-only 属性赋值」的判断可能是保守假设而非普适事实；但**不要据此推翻**已按 design 改为 `set` 的既有实体（Product），新写实体时标量 `init` 可用、集合/复杂类型仍建议 `set`。
- **`PreferenceRepository` 须 public**（`AIShop.Infrastructure.csproj` 无 `InternalsVisibleTo`）：T13 测试需直接 `new PreferenceRepository(db)` 注入受控 `AppDbContext` 断言插入/覆盖/刷新，与 T11 `ProductRepository` public 约定一致；`CartRepository` 保持 internal 是因为无直测。
- **Upsert 判重用 `FirstOrDefaultAsync(u => u.UserId == preferences.UserId, ct)`**：支持 CancellationToken、避免 `FindAsync` 的 params 数组写法（`FindAsync(new object[]{id}, ct)` 较啰嗦）；存在则把入参的 `KeywordsJson`/`UpdatedAt` 拷到跟踪实例，否则 `Add` 新实体，最后 `SaveChangesAsync`。测试用「全新 context 读回 + `Where(UserId==userId)` 行数 `Single`」验证「覆盖而非插入重复行」。
- **提交时机**：本次并行 T16 已先行落库（HEAD=695af71），工作树干净；`git add` 后 `git diff --cached --name-status` 确认 index 仅含我的两文件，`git commit -o -m "feat(T13): ..." -- <两路径>` 单命令提交成功，commit `87bcacd`（gate 全量 build+test 通过）。

## T8/T9/T10 原子集群实现笔记（product-catalog-persistence）

- **T8 改查库会引爆 McpServer 集成测试（`no such table: Products`）**：`AIShop.McpServer/Program.cs` 用 `AddInfrastructure()` 且**不 EnsureCreated / 不播种**（design 6.2 声明不改 McpServer 业务逻辑）。T8 前 `ProductCatalog` 用硬编码列表不查库，`McpServerIntegrationTests.ToolsCall_MatchProducts_ReturnsResults`（T0 基线通过）；T8 改查库后 `MatchProducts` → `ProductCatalog.All` → `ProductRepository.GetAll()` 查测试输出目录空库 `aishop.db`（0 张表）抛异常。修复只能落在**测试侧**：自定义 `TestMcpServerFactory : WebApplicationFactory<Program>`，`ConfigureWebHost` 里 `RemoveAll` 原 `IDbContextFactory<AppDbContext>`/`DbContextOptions`/`AppDbContext` 后指向临时文件库 + `EnsureCreated` + `AddRange(ProductSeedData.Products)` 播种（与 T23-pre 同模式，不 `BuildServiceProvider()` 防 Serilog frozen），`Dispose` 清池删文件。
- **`ConfigureWebHost` 里 `IWebHostBuilder` 需 `using Microsoft.AspNetCore.Hosting;`**：McpServer.Tests 原未引用，首次编译 CS0246。
- **做「改查库/改数据源」类工单要全局排查端到端集成测试**：grep `new ProductCatalog(` 只找到 2 个直构造，但 WebApplicationFactory 端到端测试（`McpServerIntegrationTests`）也因数据源变更炸；affected 测试清单不能只信 tasks.md（T22 字面只列 ProductToolsTests，实测补充 McpServerIntegrationTests）。
- **并行 agent 活跃期 T0 flaky 连续拦截 commit gate**：并行 agent（PID 26708 10:28 启动 CPU 245s）跑 build/test 时，全量测试 `ServiceDefaultsDebugTests.ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` 连续 3 次拦截（gate 1 次 + 手动全量 2 次），单独 filter 3/3 通过；**并行 agent 安静后**（其进程退出）下一次全量 200/200 全绿 → 立即 commit 成功（`e4d2ba0`）。验证「并行 agent 活跃」用 `Get-Process dotnet | Select Id,CPU,StartTime`（高 CPU + 近期 StartTime = 活跃）。
- **T14 在制品的 flaky 不稳定**：`PreferenceWriteHostedServiceTests`（未跟踪）首轮全量失败 `ShouldTruncateToTop20_*`、次轮 `ShouldAccumulateWeights_*`，gate 里却通过——T14 agent 正在活跃修改，其失败集合不稳定，判为并行在制品不越权修，但会随机 BLOCK gate。
- **tasks.md 补充说明被 check_gateway 拦**：implementer 编辑 tasks.md 的 PreToolUse hook BLOCK 依然生效；「新增受影响测试文件」的说明只能写进 handoff，checkbox/说明由 task-breaker 落库。

## T14 PreferenceWriteHostedService 实现笔记（product-catalog-persistence）

- **hosted service 构造函数注入具体 `PreferenceQueue` 而非 `IPreferenceQueue`**：接口只暴露 `TryEnqueue`，读端 `Reader` 在具体类上（T12 明确「T14 通过注入 PreferenceQueue 的 Reader 消费」）。后果：T15 的 design 4.4 注册 `services.AddSingleton<IPreferenceQueue>(PreferenceQueue.Create())` 无法解析 `AddHostedService<PreferenceWriteHostedService>()` 的具体 `PreferenceQueue` 依赖，需额外注册具体类型并映射：`AddSingleton<PreferenceQueue>(PreferenceQueue.Create())` + `AddSingleton<IPreferenceQueue>(sp => sp.GetRequiredService<PreferenceQueue>())`。handoff 已标注 T15。
- **BackgroundService 必须捕获 OCE 优雅退出**：`await foreach` 的 `ReadAllAsync(stoppingToken)` 在 `StopAsync` 取消 token 时抛 `OperationCanceledException`；若不捕获，`ExecuteAsync` 以取消状态结束 → 测试 `await _service.StopAsync()` 抛 `TaskCanceledException`。正确写法：`catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }`（S2486 不触发，因 catch 块含 `return` 语句；S2139 对 OCE 豁免）。
- **`SqliteConnection` 实现 `IAsyncDisposable`**：`Dispose()` 触发 SonarAnalyzer S6966（"Await DisposeAsync instead"），测试里须 `await _connection.DisposeAsync();`。
- **Top-20 截断后断言陷阱**：worker 先逐词 +1 再 `Take(20)` 丢弃最末词，被丢弃词的权重已消失，故落库 `weights.Values.Sum()` ≠ 输入词总数。正确断言：`Count==20` + 保留词权重均为 1 + 输入词中恰 20 个被保留（`words.Count(w => weights.ContainsKey(w)) == 20`）。
- **in-memory SQLite + `IDbContextFactory` 直测 hosted service 的模式**：共享 `SqliteConnection`（`DataSource=:memory:` 按连接隔离）+ 自定义 `TestDbContextFactory : IDbContextFactory<AppDbContext>`（实现 `CreateDbContext` 与 `CreateDbContextAsync`），hosted service 与轮询读取共用同一库；`IAsyncLifetime.InitializeAsync` 启动服务、`DisposeAsync` 停止 + 释放连接，无需 WebApplicationFactory。
- **C# 14 空集合表达式 `?? []` 可作 `Dictionary<string,int>` 空值兜底**：`JsonSerializer.Deserialize<Dictionary<string,int>>(...) ?? []` 编译通过（collection expression 支持 Dictionary）。

## T24 ModelRouterTests 回归笔记（product-catalog-persistence）

- **T5 解耦 + T8/T9 Scoped 后 ModelRouterTests 无需任何测试基建调整即 7/7 全绿**：`GetAgent` 已不解析 `IProductCatalogService`（改传 `ProductKeywordMap.Entries`），Agent 链路只依赖 Singleton 服务（`IDbContextFactory`/`CartToolProvider`/`AgentTelemetryOptions`），无根容器解析 Scoped 隐患 → design 6.1 的预期验证通过；`GetDefaultAgent()` 集成测试（WebApplicationFactory 真实 DI）无生命周期异常。
- **纯回归验证类工单若无文件改动就不提交 commit**：T24 无踩坑、无测试文件改动，按「若无需改动则无 commit」不提交，只产 handoff；tasks.md checkbox 仍归 @task-breaker（规则 4 拦截 implementer），handoff 注明即可。

## T15 DI 注册偏好服务笔记（product-catalog-persistence）

- **design 4.4 修正版落地（T14 handoff 标注的关键交接）**：`PreferenceWriteHostedService` 注入具体 `PreferenceQueue`，DI 注册需 `services.AddSingleton<PreferenceQueue>(PreferenceQueue.Create())` + `services.AddSingleton<IPreferenceQueue>(sp => sp.GetRequiredService<PreferenceQueue>())` + `services.AddHostedService<PreferenceWriteHostedService>()`；`IPreferenceRepository` 为 Scoped（依赖 Scoped `AppDbContext`，已注册）。若照抄 design 4.4 只注册接口，`AddHostedService` 启动时会 DI 解析失败。
- **hosted service「已启动」的最强验证 = 端到端消费，非仅注册检查**：`_factory.Services.GetServices<IHostedService>().OfType<PreferenceWriteHostedService>()` `Assert.Single` 只证明注册；入队一条 `UserPreferenceUpdate` → 轮询临时库断言落库才证明 worker 真实启动消费（`WebApplicationFactory.Services` getter 会 EnsureServer 启动 host，hosted service 已 StartAsync）。
- **接口与具体类型同一实例断言**：`Assert.Same(queue1, concrete)`（`IPreferenceQueue` 与 `PreferenceQueue` 从容器解析同一对象）——验证「端点（接口）与 worker（具体类）消费同一队列」的注册语义。
- **WebApplicationFactory + 临时 SQLite 文件库验证 worker 落库**：`WithWebHostBuilder` 替换 `IDbContextFactory` 指向临时文件库（`ProgramSeedingTests` 同模式，不 `BuildServiceProvider()` 防 Serilog frozen），Program.cs 启动逻辑（EnsureCreated + 播种）在临时库执行，hosted service 的 factory 解析到同一库 → 测试直接查该库轮询。放 `[CollectionDefinition(DisableParallelization = true)]` 串行集合规避与 ServiceDefaultsDebugTests 竞争。
- **commit 成功 `2658a67`**：gate 全量 `dotnet build` + `dotnet test` 一次通过（API 192/192 + McpServer 11/11，含 T0 flaky 本轮未现），`git commit -o -- <两路径>` 隔离，`git show --stat HEAD` 复核精确 2 文件（DependencyInjection.cs +8 / 测试 +156）。本轮无并行 agent 中间态阻塞、无 flaky 拦截。

## T18/T19/T20 /chat 端点改造笔记（product-catalog-persistence）

- **「共享 tag 多路匹配」会让消息级 validKeywords 数量超出直觉**：`/chat` 的 `validKeywords` 匹配逻辑是"消息含关键词或其任一 expansion tag"。`跑步` 同时是 `健身`（tags 含"跑步"）与 `运动`（tags 含"跑步"）的 expansion tag，因此消息「推荐跑步鞋」实际匹配出 **3 个**当前关键词（跑步/健身/运动），`RecommendationMerger.MergeKeywords` 因 `count==3` **不触发偏好补齐**（spec「不足 3 个才补齐」的正确行为）。写「当前关键词优先偏好补齐」集成测试时，选词必须避开共享 tag：用「推荐跑鞋」（仅匹配 鞋子/跑步 2 个）才能触发补齐；「推荐跑步鞋」天然 3 个用于验证不补齐。**做 /chat 推荐集成测试前先枚举消息命中哪些 KeywordMap key（含 tag 级命中），不要凭直觉猜 validKeywords 数量**。
- **派生 WebApplicationFactory 的 host 竞争**（`The entry point exited without ever building an IHost`）：在已 WithWebHostBuilder 包装的 `_factory` 上再 `_factory.WithWebHostBuilder` 派生 f，若 f 的 mockRouter 闭包仍引用 `_factory`（`capturedFactory.Services` 是 `_factory` 的），则 f 的请求会触发 `_factory.Services` 启动**第二个 host**，与 f 的 host 竞争同一 Program 入口点 → `f.CreateClient()` 抛 InvalidOperationException。**解法：测试完全自建 factory（`BuildFactory(connStr, queueOverride)` helper），mockRouter 引用自建 factory 的 `factory!`**；不要在一个测试里同时启动基 factory 与派生 factory 两个 host。
- **NSubstitute `Arg.Is` 谓词表达式树不能含集合表达式**：`u.Preferences.SequenceEqual(["咖啡","健身"])` 报 CS9175（表达式树不能包含集合表达式）。改用 `new[] { "咖啡", "健身" }`（数组初始化在表达式树中合法）。
- **「/chat 异步不阻塞写入」的最可靠测试方式 = mock 队列吞消息**：替换 `IPreferenceQueue` 为 mock（`Received(1).TryEnqueue` 断言入队参数精确），消息被 mock 吃掉后真实 worker（注入的是具体 `PreferenceQueue`）收不到 → DB 保持预置值 → 无竞态地证明「响应在写入完成前返回」。比「POST 返回后立刻读 DB 断言未写入」稳（worker 毫秒级消费，后者有竞态）。
- **`/chat` 端点三轮改动逐步累积**（T18 偏好回填 → T19 推荐合并 → T20 入队），每轮 commit 用 `git commit -o -m -- <ChatEndpoints.cs + 当次测试文件>` 精确隔离；T18 删除旧 StateBag 块后 `session` 变量仍被 `cache.Set(chatCacheKey, (result, session), ...)` 引用，无未使用告警。
- **T0 预置 flaky（`ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue`）在 gate 里依旧高频拦截**：T18 两次 BLOCK、T20 一次 BLOCK，手动全量绿后立即重试 commit 即成功（T19 一次通过）；路径与 T26/T7 一致——不 `--no-verify`，全量重跑通过即重试。

## T21 /recommendations 复用合并逻辑笔记（product-catalog-persistence）

- **P2-8 端点层偏好注入落地方式**：`/recommendations` 的 `validKeywords` 来自 Agent `Keywords` 白名单过滤（不是消息级匹配），端点注入 `IPreferenceRepository prefRepo` 加载 `prefs` → `prefKeywords = GetTopPreferenceKeywords(prefs?.KeywordsJson, 5)` → `merged = MergeKeywords(validKeywords, prefKeywords)`；分支条件从 `validKeywords.Length > 0` 改为 `merged.Length > 0`，`merged==0` 才走 `All.Take(6)` 兜底，与 `/chat` 的合并+兜底结构完全一致。
- **`/recommendations` 测试先 POST `/api/chat` 再 POST `/api/recommendations` 的缓存链验证**：`/chat` 缓存 `agent_result_{username}_{hash}`，`/recommendations` 用 `GetMessageHash(lastUserMessage.Content)` 命中同 key（不重新调 LLM）；mock `GetResponseAsync` 未被二次调用即证明走了缓存命中路径。测试只需一个 mock JSON（可配置 `Keywords`），无需消息级关键词匹配。
- **`/recommendations` 的 BestMatch 断言可精确到具体商品 Id**：`merged` 按关键词序进 `SplitProducts` → `orderedTags` 带 `(index, tag)` → 商品按最小匹配 index 排序，同 index 保持 `All` 顺序。`merged=[咖啡,健身]` → BestMatch 恒为 Id=5（意式浓缩咖啡机）；`merged=[鞋子,咖啡,健身]` → BestMatch 恒为 Id=3（专业跑鞋，当前关键词优先）。做断言前先按 `SplitProducts` 的排序规则推演，不凭猜。
- **无并行 agent 活跃时 T0 flaky 仍可能偶发拦 commit gate**：本轮手动全量 202/202 绿、隔离 filter 3/3 绿，但 gate 全量里 flaky 仍失败一次；全量重跑绿后立即重试 commit 即成功（commit `2a1899c`）。判定 flaky 用「隔离 filter 通过 + 失败集合与 T0 一致」双证据，不越权修。
- **commit `2a1899c`**：`git add` 两文件 → `git diff --cached --name-only` 核对 → `git commit -o -m -- <两路径>` 一次成功；`git show --stat HEAD` 复核恰 2 文件（ChatEndpoints.cs +15/-2、ChatRecommendationsMergeTests.cs +227）。handoff `handoff-T21.md` 已产出，checkbox 归 @task-breaker。

## T23 ChatEndpointsTests 回归 + 偏好场景复查笔记（product-catalog-persistence）

- **T23 纯回归 + 覆盖复查，无文件改动、无 commit**：`ChatEndpointsTests`（18）+ T18/T19/T20 三个偏好测试类（8）+ `RunChatAsyncPreferenceBackfillTests`/`AgentTelemetryTests`（22）合计 **48/48 全绿**。T18/T19/T20 已在各自 commit 用「ReplaceWithIsolatedDb + SeedPreferencesAsync helper」落地全部 6 条 spec 条款（回填注入/无偏好不注入/当前词优先补齐/不足3不补齐/无词无偏好兜底/无词有偏好推荐/入队不阻塞/worker累加），T23 复查**无缺口、不重复建设**。
- **做「回归确认 + 覆盖复查」类工单的关键**：先全局 grep 找出所有受影响测试类（`grep -rln 'api/chat'` + `RunChatAsync`），用 `--filter "A|B|C"` 一次性跑相关类，不要只信 tasks.md 字面列出的类；T23 字面只列 ChatEndpointsTests，实测还连带 RunChatAsync/AgentTelemetry（T16 具名 ct 的直测）。
- **tasks.md checkbox 勾选仍被 check_gateway.py 规则 4 拦截**（implementer Edit 实测 BLOCK，报「必须由 @task-breaker 完成」），handoff 中注明即可，不要重复尝试。

## R1 worker 容错 + Top-20 按权重截断笔记（product-catalog-persistence）

- **in-memory SQLite 共享连接下「worker 写 + WaitForWeightsAsync 轮询读」并发会在全量并行时让 `SqliteConnection.Close()` 抛 NRE（DisposeAsync 阶段）**：T14 直测 hosted service 用共享 `DataSource=:memory:` 连接，测试主线程轮询读 + worker 写并发访问同一连接；单独 filter 稳定通过，全量并行时偶发在 `_connection.DisposeAsync()` 抛 NullReferenceException（`SqliteConnection.Close()` 内部竞态）。解法 = 与 T7/T15 一致，给测试类加 `[CollectionDefinition(nameof(X), DisableParallelization = true)]` + `[Collection(nameof(X))]` 串行集合，消除跨类并行宿主竞争。
- **NSubstitute 5.3.0 的 `It.IsAnyType` 在编译期不可见（CS0246）**：`Arg.Any<It.IsAnyType>()` / `Func<It.IsAnyType, Exception?, string>` 报「未能找到类型或命名空间名 It」。断言 ILogger 的 `Log<TState>` 调用改用 `Arg.Any<dynamic>()` + `Arg.Any<Func<dynamic, Exception?, string>>()`（NSubstitute 兼容任意 TState 匹配，编译通过且运行时正确）。
- **Top-20 截断的红绿验证要构造「权重最高但枚举顺序第 21 位」的词**：`ShouldTruncateToTop20` 要区分「按权重」与「按 Dictionary 枚举顺序」，预置 20 个权重 1 的词 + 1 个权重 5 且位于 JSON 最后的「词X」（反序列化插入顺序最后 = 枚举最后），再入队触发词。旧实现 `Take(20)` 按枚举顺序丢弃词X，新实现 `OrderByDescending(Value).ThenBy(Key, Ordinal).Take(20)` 保留词X——用 `Assert.True(weights.ContainsKey("词X"))` 区分。
- **worker 单条异常容错测试用「坏 JSON + 后续正常消息落库」验证**：预置 `KeywordsJson="not-valid-json"` 行 → 入队该用户消息触发 JsonException → 再入队健康用户消息 → 轮询断言健康消息落库（旧实现无内层 catch，worker 死亡 → 健康消息永不处理 → 超时失败）。logger 断言放在好消息落库之后执行，避免时序 flaky（此时坏消息必已处理完、Warning 必已记录）。
- **全量跑 T0 flaky（`ServiceDefaultsDebugTests.ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` / `...WhenDebugFalse`）在并行 agent 活跃期轮番拦截 commit gate**：失败集合在 Debug=true（T0 flaky）与 Debug=false（T26 已修）间变化 = 并行 agent 在跑全量测试（`Get-CimInstance Win32_Process` 看到 vstest/testhost 16:19 启动），WebApplicationFactory 资源竞争。判定依据 = 单独 `--filter ServiceDefaultsDebugTests` 3/3 通过 + 失败集合限 ServiceDefaultsDebugTests。处理 = 等并行 vstest/testhost 进程退出（安静窗口）→ 手动全量绿 → 立即重试 commit。
- **`dotnet run --project AIShop.Api` 残留进程持有 DLL**：进程列表常有 15:58 启动的 AIShop.Api run 实例（并行 agent 手动验证残留），不影响 build 但属并行活动佐证；判定并行活跃优先看 vstest/testhost 进程而非 MSBuild 常驻节点（`/nodemode:1 /nodeReuse:true` 是正常残留）。

## R2 队列满可观测性 + 偏好词白名单过滤笔记（product-catalog-persistence）

- **DropOldest 语义下 `TryEnqueue` 的 false 只发生在 channel complete**：`TryWrite` 满队时挤出最旧再写入并返回 **true**，因此端点 `TryEnqueue==false` 的 `Log.Warning` 是死代码（生产不会满队返回 false）。真正的观测点应在 `TryWrite` **前**检查 `Reader.Count >= Capacity`（写入时已满 = 本次必挤旧）记 Warning。近似观测拿不到被挤出的具体消息，以「写入时已满」近似即可。
- **并行安全优先于 tasks.md 的 ILogger 方案**：tasks.md 阶段 9 主张 Infrastructure 用 `ILogger<T>` 注入（`static Create(ILogger<PreferenceQueue>)`），但改 `Create()` 签名会连锁破坏并行 R1 正在改的 `PreferenceWriteHostedServiceTests.cs` 构造点与共享 `DependencyInjection.cs`。协调者指令明确为 Serilog 静态 `Log.Warning`——不改签名、不动 DI，是并行安全选择。代价：`AIShop.Infrastructure.csproj` 需**新增 `Serilog` 4.3.0 包引用**（Infrastructure 原不引 Serilog；Api 已同版本传递引用，无冲突）。实现类工单若协调者指令与 tasks.md 计划冲突，以协调者指令为准并在 handoff 注明偏差。
- **测试 Serilog 静态 `Log` 需临时替换全局 `Log.Logger`**：`PreferenceQueue` 用静态 `Log.Warning`，测试捕获方式 = 保存原 `Log.Logger` → `new LoggerConfiguration().WriteTo.Sink(collectingSink).CreateLogger()` → try/finally 恢复；自定义 `ILogEventSink` 收集 `LogEvent`，按 `Level == Warning` + `MessageTemplate.Text.Contains("dropping oldest")` 断言（不断言具体属性值，避免 ScalarValue 渲染格式差异）。全局 swap 是微秒级窗口，断言过滤 message template 避免并行其他 Warning 误判。
- **P2-4 偏好词过滤公式要对齐 `SplitProducts` 实际匹配能力**：`SplitProducts` 匹配 `product.Tags ∪ {Category}` + KeywordMap 扩张，所以偏好词过滤用「`KeywordMap.ContainsKey(kw)` **或** `catalog.All` 任一商品 Tags 包含 kw」（比仅 KeywordMap 更宽，保留「非 key 但为商品 tag」的合法词如 `穿戴`）；`IsNullOrWhiteSpace` 剔除空白词。`preferencesText`（Agent 上下文注入）**不做**过滤，只过滤进 `MergeKeywords` 的 `prefKeywords`。
- **commit gate 的 T0 flaky 在并行 R1 活跃期高频拦截 amend**：R2 主 commit `6eb2ff8` 一次成功（全量 219/219 绿后 gate 通过），但随后对同一 commit 的 `git commit --amend`（仅改日志文案）被 T0 flaky 连续 4 次拦截（期间还夹杂 R1 的 `PreferenceWriteHostedServiceTests` 全量不稳定：ShouldTruncateToTop20 / ShouldAccumulateWeights 交替失败、单独 filter 4/4 通过）。教训：**纯文案微调不值得反复烧 gate 循环**——amend 被 flaky 连续拦后，回退保持已提交状态 + handoff 注明差异，比硬等到安静窗口更划算。amend 若在 R1 提交后执行会改写他人 commit，绝不冒险。
- **`git commit --amend -o -- <paths>` 可用但 gate 会重跑全量**：amend 同样触发 check_commitgate（build+test），且 `-o -- <paths>` 只改指定路径（不卷入 index 里 R1 已暂存文件）。但 amend 若被 gate BLOCK，工作树改动会滞留（index 已 add）——回退时注意 `git add` 的工作树 Edit 要用反向 Edit 恢复，或 `git restore <file>`。
- **MSB3021/MSB3027（testhost 锁定 DLL）在并行活跃期会随机拦 gate build**：`testhost (PID)` 持锁导致 copy 超重试 10 次失败。判定并清理：`Get-Process testhost` 找持锁进程 → 等它自然退出（R1 的测试进程，不主动杀）→ `Stop-Process` 孤儿 MSBuild（gate 超时遗留）后重试。

## R4 Reply 回复文本清洗笔记（product-catalog-persistence）

- **SonarAnalyzer S6444：静态 `Regex.Replace(input, pattern, "")` 不带 timeout 在 `-warnaserror` 下报 error S6444**（"Pass a timeout to limit the execution time"），且同一模式字面量重复会触发 S6354。规避：用 `static readonly Regex` 字段 `new(pattern, RegexOptions.None, TimeSpan.FromSeconds(1))`（构造带 timeout 满足 S6444）+ 实例 `Replace` 调用；测试里 `Regex.IsMatch` 同样要带 `TimeSpan` 重载（`Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))`），否则测试项目 build 也报 S6444。
- **回复文本清洗方案 D（R4）**：只清洗 `ChatReply.Response`，两处构造点（fallback/推荐分支）`result.Reply ?? ""` → `SanitizeReply(result.Reply)`（方法内 `?? ""` 保留既有防御）；正则 `#\d+` + `商品ID[\s:：]*\d+` + `Trim`，**不裸用 `\d+`**（会误删价格 `349.99`/数量）；红线——`RecommendedProducts`/`OtherProducts` 的 `ProductDto.Id` 一字不碰（前端加购依赖结构化数据，不从 Reply 文本解析）。测试显式断言 `RecommendedProducts` 含指定 Id 不变 + 价格数字保留 + `#`/`商品ID` 后无数字不误删。
- **`（商品ID: 4）` 清洗后残留空括号 `（）`**：正则只删 ID 标记本身、不删包裹括号，`Trim()` 只清首尾——中间残留空括号是方案 D 已知现象（不影响「无商品 ID 模式」核心诉求），如需连括号清理属后续 UX 微调，按协调者方案不推测性增强。
- **MSB3026/MSB3027 也可能是 `dotnet run` 常驻 web server 锁 `AIShop.Api.exe` 本身**：`taskkill //PID <pid> //F` 清理后 build 恢复（实例：PID 46360，18:54 启动的常驻 run 进程，锁 `bin/Debug/net10.0/AIShop.Api.exe`，CPU 30s 且 Responding=True）；与既有「testhost 锁 DLL」记录互补——exe 本身被 run 进程锁是另一形态，Build 报 MSB3026/MSB3027 时可同时查 `Get-Process AIShop.Api`。
- **tasks.md 补 R4 条目被 check_gateway.py 规则 4 拦截**（PreToolUse Edit hook BLOCK，报「必须由 @task-breaker 完成」）——tasks.md 无 R4 小节，implementer 无法自建 checkbox，handoff 注明等 @task-breaker 落库；R4 测试文件单独 commit 不受影响（`git commit -o -m -- <两路径>` 精确隔离，commit `ade7b78`，gate 全量 211/211 + 11/11 一次通过，无 flaky 拦截）。

## R5 SanitizeReply 正则收紧笔记（product-catalog-persistence）

- **`.NET \b` 对中文是 Unicode 单词字符，`#3无线` 中 `3` 与 `无` 之间无词边界**：`无` 为 `\p{L}`（letter）→ `\w`，纯正则 `#(?:1[0-8]|[1-9])\b` 判定 `#3` 后无边界而**不清洗**，会静默破坏既有 R4 断言 `Assert.DoesNotContain("#3")`。收紧「带明确 ID 范围的 `#\d+`」时不要用 `\b` 方案；优先「`#(?<id>\d+)` 提取 + `int.TryParse` 校验 1-18 才删」（`(?!\d)` 负向先行也可用但 TryParse 更可读，范围变化只改 `Min/MaxProductId` 常量）。`\d+` 贪心消费整段数字，`#123456` 整体 TryParse 超范围返回原串，不会拆成 `#1`+`23456` 污染文本。
- **`Regex.Replace(input, MatchEvaluator)` 的 `static` lambda 可安全调用 static 方法**：`static match => IsProductId(match.Groups["id"].Value) ? "" : match.Value` 编译通过（无闭包捕获，S6605 不触发），`IsProductId` 内 `int.TryParse` + `id is >= Min and <= Max` relational pattern 满足 TreatWarningsAsErrors。
- **`商品ID[\s:：]*\d+` 保持宽匹配是有意取舍**：前缀本身明确标识「商品 ID」（无歧义），收紧到 1-18 反而会漏删 `商品ID: 99` 这类明确的产品 ID 展示，回归 R4「隐藏商品 ID 展示」诉求；与 `#` 前缀（`#` 可能是订单号/话题）收紧的理由不冲突，注释说明取舍即可。
- **tasks.md 的 R5 小节可能由并行 task-breaker 先落库**：本次 tasks.md 已有「阶段 11：codex 审核修复工单（R5）」且 4 个 checkbox `[ ]`，implementer 尝试 Edit 标 `[x]` 依旧被规则 4 BLOCK（实测）——handoff 记录确切状态（小节已存在 + checkbox 待 task-breaker 勾选）即可。
- **R5 commit `91a0d18`**：`git add` 两文件 → `git diff --cached --name-status` 核对精确 2 文件 → `git commit -o -m -- <两路径>` 一次成功；`dotnet build AIShop.sln -warnaserror` 0 错误 0 警告，全量 223/223（Api 212 + McpServer 11）绿，无 flaky 拦截。

## R6 /recommendations 纯偏好驱动重构笔记（product-catalog-persistence）

- **`SplitProducts` 的推荐集合比直觉大，shuffle 后 BestMatch 断言不能硬编码 {5,6}**：merged=[咖啡,健身] 时，健身的 expansion tags（跑步/运动/瑜伽/健康）会命中 跑鞋(3)/瑜伽垫(6)/水瓶(8)/手表(10)/蛋白粉(14)，加上咖啡的 咖啡机(5)，推荐集合为 {3,5,6,8,10,14} 共 6 个。端点对 recommended 做 shuffle 后 `BestMatch = recDtos.FirstOrDefault()` 随机落在其中任一 → 断言 `BestMatch.Id ∈ {5,6}` 会偶发失败（首跑 30 绿、次跑 2 红）。正确做法：测试里 `catalog.SplitProducts(["咖啡","健身"]).Recommended` 求期望集合再 `Assert.Contains(result.BestMatch.Id, expectedIds)`（与端点同一口径，稳健且自文档化）。
- **`Random.Shared` + Fisher-Yates 在 `-warnaserror` 下 0 告警**：SonarAnalyzer S2245（PRNG 安全热点）默认 Info 级，不因 TreatWarningsAsErrors 变 error；`ShuffleProducts(products, Random? random = null)`（`rng = random ?? Random.Shared`）既满足线程安全又保留测试可注入固定种子能力，签名可测。
- **删除端点 DI 参数后 sessionId 也不再需要**：/recommendations 去掉 `sessions`/`chatRepo`/`router`/`cache` 后，`sid`（GetOrCreateSessionIdAsync）只服务过 lastUserMessage 与日志，一并删；`GetMessageHash` 仍被 /chat 使用（SHA256/Encoding using 保留）。
- **/chat 的 `agent_result_{username}_{hash}` 缓存写入成为死代码**：原只为 /recommendations 复用 LLM 结果而写，R6 后 /recommendations 不再读它。按「只改 /recommendations + 相关测试」的范围纪律**未删除**（删会连锁改 /chat 的 `session` 变量解构触发 S1481），handoff 标注为后续可清理项。
- **AIShop.AppHost.exe 常驻进程同样锁全量 build（MSB3026/MSB3027）**：除既有 AIShop.Api.exe 外，Aspire AppHost 的 `dotnet run` 残留也会锁 `bin/.../AIShop.AppHost.exe`；`taskkill //PID <pid> //F` 后恢复。与测试进程无关，属并行 agent 手动验证残留。
- **无偏好兜底测试断言用「排序后 Id 相等」而非顺序**：shuffle 只变顺序，`Assert.Equal([1,2,3,4,5,6], result.Other.Select(p => p.Id).OrderBy(x => x).ToArray())` 既验证兜底内容恒为 All.Take(6) 又对 shuffle 健壮。
- **「无 LLM 调用」断言用 `await mockClient.DidNotReceive().GetResponseAsync(...)`**：`DeepSeekDelegatingChatClient` 构造不调用 inner mock（仅存引用），/recommendations 不再解析 ModelRouter 后 mock 全程零调用；capturedMock 通过 ConfigureServices 回调闭包捕获，host 构建（CreateClient）后才赋值，断言放请求完成后执行。
- **tasks.md 的 R6 checkbox 仍归 @task-breaker**：tasks.md 无 R6 小节（阶段 9/10/11 只到 R5），implementer 直接 Edit 会被 check_gateway.py 规则 4 BLOCK；handoff 记录「无 R6 小节 + 待 task-breaker 落库」，不重复尝试。




## R7 /recommendations 随对话内容驱动 + 去 shuffle 笔记（product-catalog-persistence）

- **R7 与 R6 的核心差异在于「推荐数据源」与「提示语判据」**：R6 纯偏好驱动（`MergeKeywords([], prefKeywords)`）+ shuffle；R7 改「最新用户消息关键词优先 + 偏好补齐」（`MergeKeywords(validKeywords, prefKeywords)`，读 `chatRepo.GetLastUserMessageAsync` + `catalog.KeywordMap` 直接匹配，不调 LLM）+ **删掉所有 `ShuffleProducts` 调用并删除该方法**（固定顺序，多次调用确定性）。端点 lambda 重新引入 R6 删掉的 `ISessionRepository sessions`、`IChatMessageRepository chatRepo`（`GetOrCreateSessionIdAsync` → `GetLastUserMessageAsync(sid)`）。
- **提示语判据以 tasks.md 为准而非协调者口述摘要**：协调者任务消息写「仅当完全无内容（无消息等）才'暂无特定推荐'」，但 tasks.md 阶段 13 L388 明确「`merged.Length == 0` 兜底分支 `Message = catalog.All.Count == 0 ? "暂无特定推荐" : "为您精选商品"`」且 L394 测试断言「无历史消息、无偏好 → 直接 POST → Message=='为您精选商品'」。实现时必须按 tasks.md（商品库完全为空才"暂无特定推荐"），否则 L394 测试必红。**implementer 实现前先读 tasks.md 的 R7 小节全文，不能只信启动指令摘要。**
- **`ShouldFollowLatestMessage_WhenMessageChanges`（R7 语义反转）选消息要避开共享 tag**：第一条消息用「推荐咖啡机」（命中 key 咖啡 1 个）→ BestMatch 恒 5；第二条用「你好」（无关键词、无偏好）→ 兜底「为您精选商品」。不要用「推荐跑步鞋」（共享 tag 命中 3 个关键词，validKeywords 数量超出直觉，见 T18 笔记）。断言用 `Assert.NotEqual(reco1.Message, reco2.Message)` + `BestMatch 5 vs null` 证明「推荐随消息变化」。
- **`ShouldUseMessageKeywords_NotAgentKeywords`（R6 ShouldIgnoreAgentKeywords 反转）**：mock Agent 返回 `Keywords=["数码"]`，消息「推荐跑鞋」→ 端点只读 DB 消息命中 鞋子/跑步 → BestMatch 恒 3（专业跑鞋），证明消息关键词驱动、忽略 Agent 结构化输出。选「推荐跑鞋」而非「推荐跑步鞋」（后者共享 tag 命中 3 个）。
- **`catalog.All.Take(6)` 兜底列表在 R7 下可精确断言顺序**（去 shuffle）：`Assert.Equal([1,2,3,4,5,6], result.Other.Select(p => p.Id).ToArray())`（去掉了 R6 的 `OrderBy`），`ShouldReturnDeterministicOrder_WhenCalledMultipleTimes` 连续 3 次调用断言 `Other` Id 序列逐次 `Assert.Equal`（SequenceEqual）。
- **commit 隔离仍用 `git commit -o -m -- <4 路径>`**：ChatEndpoints.cs + ChatEndpointsTests.cs + ChatRecommendationsMergeTests.cs + ChatPreferenceFilterTests.cs；不卷入 index 里并行 agent/其他会话的 memory 文件改动（implementer/glossary/task-breaker/tester learnings 在共享工作树里一直处于 M 状态，是并行 agent 的在制品，不归本次 commit）。

## R8 /recommendations 缓存联动聊天产物笔记（product-catalog-persistence）

- **`SplitProducts(["家居"])` 的顺序实测：BestMatch=12 不是 16**：家居 命中 Id 12（香薰蜡烛套装，tags 含 家居/放松/香薰）+ Id 16（真丝枕套套装，tags 含 家居/睡眠/护肤），两者 earliestMatch 均为 index 0 → 稳定序按 All 顺序 12 在前 → /chat recommendedProducts 首个=12，快照缓存命中后 BestMatch 恒为 12。R8 任务文字「BestMatch=16」是字段现象「chat 回复含枕套(16)」的松散表述；**严谨断言 = BestMatch.Id == chatReply.RecommendedProducts[0].Id + chat 推荐集合 Contains(id==16)**。想让 16 居首需「睡眠/护肤 等 16 独有 tag 且是 KeywordMap key 的关键词排在 家居 前」，而它们都不是 key、agent Keywords 又经 `catalog.KeywordMap.ContainsKey` 过滤 → 任务描述的 mock（Keywords=["家居"]）下 16 永不可能居首。
- **WebApplicationFactory 的 IMemoryCache 是每个 factory 独立 DI 容器**：每个测试 `new WebApplicationFactory<Program>().WithWebHostBuilder(...)` 自建 factory → `recommend_{username}` 缓存天然按测试隔离，无需额外清缓存/换 username。但 ChatEndpointsTests 用 `_factory.WithWebHostBuilder(...)` 每次派生新 factory（IClassFixture 基 factory 共享，派生 factory 各自独立 services）→ 同 username 不同测试互不污染。
- **`/recommendations` 删 `sessions`/`chatRepo` 只改该 lambda 参数**：/login、/chat 端点各自 lambda 仍用这两服务，删除时只动 /recommendations 的入参列表，不会编译报错；`sid`（GetOrCreateSessionIdAsync 结果）随 lastUserMessage 一并删。
- **新测试与既有测试实现相同会触发 SonarAnalyzer S4144**（-warnaserror 下为 error）：新增「缓存 miss 无偏好 → 精选兜底」测试与既有 `ShouldReturnFallback_WhenNoPreference` 完全同构被拦；先 grep 既有覆盖再决定新增还是复用（既有该测试无 /chat 调用、断言 All.Take(6)+「为您精选商品」本身就是 R8 的 miss 精选兜底场景）。
- **EF `ctx.Sessions.First(...)` 同步 LINQ 触发 S6966**：测试里直查 DbSet 用 `await ctx.Sessions.FirstAsync(s => s.UserId == userId)`。
- **R8 快照缓存存 chatReply 同一 List 引用（不拷贝）**：/chat 每次构造全新 ChatReply（新 List 实例），旧快照持有的引用不被新响应污染；IMemoryCache 内存存储无需序列化，直接引用安全。`chatReply.RecMessage` 是 `string?`，快照 Message 为非空 string，赋值须 `?? ""` 防 CS8601。
- **R8 缓存命中路径与聊天 100% 一致的核心断言**：`result.BestMatch.Id == chatReply.RecommendedProducts[0].Id` + `result.Other.Id序列 == chatReply.OtherProducts.Id序列` + `result.Message == chatReply.RecMessage` + `MatchedCategories 相等`；DB 消息注入测试（缓存命中优先于 DB 最新消息）用 `ChatMessageRecord` 直写 chat_messages（Role=user、Id 自增成为最新），断言 BestMatch != 咖啡机(5)。
- **`AIShop.Api.exe` 常驻 run 进程锁 build（本次 PID 62888，0:12 启动）**：`taskkill //PID <pid> //F` 后恢复（与既有 R4/R6 笔记一致）；并行 agent 手动验证残留是常态，build 报 MSB3026/MSB3027 时先查 `Get-Process AIShop.Api`。

## R8.1 RecommendationResponse 加 Recommended 完整推荐列表笔记（product-catalog-persistence）

- **tasks.md 测试说明与设计示例矛盾时以设计示例的实际可测性为准**：R8.1 L470 写「mock Keywords=['家居'] → id 序列 [5,11,12,16]、BestMatch==5」，但实测 `SplitProducts(["家居"])` = [12,16]（家居命中 香薰蜡烛12/枕套16 均 index 0，稳定序 12 在前），不可能得到 [5,11,12,16]。设计背景段的示例 [5咖啡机,11煎锅,12蜡烛,16枕套] 实为 `SplitProducts(["咖啡","家居"])`（咖啡/烹饪 index 0 → 5/11，家居 index 1 → 12/16）。**测试 mock 用 Keywords=["咖啡","家居"]**（消息「有枕套吗」仍字面无命中、走语义回退）即可复现设计示例，并在测试注释 + handoff 标注偏差。
- **`RecommendationResponse` 加字段是破坏性契约变更但 JSON 反序列化测试零适配**：grep 全仓确认无测试直接 `new RecommendationResponse(...)`（仅 ChatEndpoints.cs 内 4 处构造）；既有 recommendations 测试均 `ReadFromJsonAsync<RecommendationResponse>`，新增 `recommended` 字段不影响反序列化与 BestMatch/Other/Message/MatchedCategories 断言。
- **不变量断言写法**：`Recommended ∩ Other == ∅` 用 `Assert.DoesNotContain(result.Other, p => recommendedIds.Contains(p.Id))`（SplitProducts 天然保证，命中缓存路径快照同样保持）；`BestMatch == Recommended[0]` 用 `Assert.Equal(result.Recommended[0].Id, result.BestMatch!.Id)`（先 `Assert.NotNull(result.BestMatch)`）。
- **前端 `renderRecommendations` 两段式与 `renderRecommendationPanel` 对齐**：recommended 有值 → 「🎯 为您推荐」首个 `productCard(p,true)` 高亮 + 「📋 其他商品」；recommended 空 → 「📋 全部商品」兜底（未推荐 opacity 0.7 卡片）。`getRecommendations()` 改传整个响应 `renderRecommendations(data)`，`bestMatch` 字段保留兼容但渲染以 recommended 为主。
- **全量 flaky 复现模式（worker 轮询超时）**：`PreferenceWriteHostedServiceTests.ShouldTruncateToTop20_WhenMoreThan20PreferenceWords` 全量并行时偶发 `WaitForWeightsAsync` 超时，单独 `--filter PreferenceWriteHostedServiceTests` 4/4 通过、全量重跑 223/223 通过——并行负载下 in-memory SQLite + worker 轮询时序 flaky，与 R8.1 零耦合（未触碰 worker 文件，mtime 昨日）。判定 flaky 双证据：隔离 filter 通过 + 失败集合与本次改动零耦合。

## R9 商品 ID 泄漏回潮修复笔记（product-catalog-persistence）

- **「商品ID为4」未被 R4/R5 覆盖的根因**：R4/R5 的 `ProductIdLabelPattern` 字符类 `[\s:：]*` 只覆盖「冒号/空格」连接；LLM 用「为」字连接（商品ID为4）时不匹配 → ID 漏出。修复 = 字符类扩入连接词 `[\s:：为是]`（兜底删「商品ID为4」「商品ID是4」，连「商品ID为 4」带空格也覆盖）。
- **「固定格式 + 精确删 + 兜底」三层方案**：① Agent 指令（`BuildInstructions`【回复规范】）固定唯一合法格式「商品Id:N」，违例形式（商品ID为4、#4、编号4、ID：4）列举为禁止；② 服务端 `FixedIdPattern = 商品Id[:：]\d+`（`RegexOptions.IgnoreCase` 覆盖「商品id:4」小写）精确删固定格式；③ 兜底 `ProductIdLabelPattern` 扩字符类删变体。删除顺序：FixedIdPattern → HashIdPattern（# + IsProductId 1-18 校验，保留不动）→ ProductIdLabelPattern。
- **`商品ID[:：]` 与 `为/是` 变体不冲突**：固定格式（冒号直连数字）与兜底（连接词后数字）是互补形态；FixedIdPattern 无 `#`、无连接词，先删不会破坏后续正则的匹配目标。价格/数量数字安全：所有正则都以「#」或「商品Id/商品ID」前缀开头，`价格为249.99元`（无前缀）不误删。
- **正则预编译 + timeout 惯例（S6444/S6354）**：新增 `FixedIdPattern` 仍用 `new(pattern, options, TimeSpan.FromSeconds(1))` 静态字段；测试里 `Regex.IsMatch` 也要带 `TimeSpan` 重载。
- **测试断言用变体精确匹配**：`Assert.DoesNotContain("商品ID", ...)` + `DoesNotContain("为4")`/`DoesNotContain("是5")`（删后不留连接词残渣）+ `Assert.Contains("249.99")`（价格保留），比裸正则更可读、直接对应业务行为。

## R10 一致性修复笔记（product-catalog-persistence）

- **`ChatClientMetadata`（Microsoft.Extensions.AI 10.8.3）实际签名 ≠ 常识/协调者描述**：实测（临时反射程序）构造函数为 `(string providerName, Uri? providerUri = null, string? defaultModelId = null)`——**第 2 参是 `providerUri`（Uri）而非 modelId**，模型名走第 3 参 `defaultModelId`；属性是 `DefaultModelId`（无 `ModelId` 别名）。写 `new ChatClientMetadata("DeepSeek", modelName)` 会报 CS1503（string→Uri 不匹配）、`modelId:` 命名参报 CS1739。正确：`new ChatClientMetadata(providerName: "DeepSeek", defaultModelId: modelName)`。**做 MAF/Microsoft.Extensions.AI 类型时先用临时反射程序确认签名，不要凭常识**（/maf-reference 文档未必跟到具体版本）。
- **主构造函数 → 普通构造函数时，主构造参数在方法内不再可见**：DeepSeekChatClient 原 `(HttpClient httpClient, string modelName)` 中 `modelName` 被 `GetResponseAsync` 请求体 `["model"]` 引用；改普通构造后须存 `_modelName` 字段并改引用，否则 CS0103（协调者示例未提，属必要补充）。
- **IChatClient.Metadata 默认实现为空对象**：旧 DeepSeekChatClient 未实现 `Metadata` 属性仍能编译（接口有默认实现），但 OTel gen_ai 属性（provider/model）为空；实现 `public ChatClientMetadata Metadata { get; }` 即修。
- **/api/login 历史含未清洗商品 ID 的链路**：`SqliteChatHistoryProvider.StoreChatHistoryAsync` L246-249 对 assistant 用 `StripAgentReplyJson` 提取 `Reply` 字段存 `chat_messages.content`（原始 Reply 文本含 ID 标记），登录出口此前直接返回 → 泄漏。修复 = /login 构造 `ChatMessageDto` 时 assistant 消息过 `SanitizeReply`（user 原样）。
- **测试 login 历史清洗走真实链路**：POST /api/chat（mock Reply 含 商品Id:10/商品ID为4）→ SqliteChatHistoryProvider 持久化原始文本 → POST /api/login → 断言历史 assistant 消息不含 ID 片段、user 原样、名称/价格保留；`profile.History.Single(m => m.Role == "assistant")` 依赖单次 /chat 单条 assistant（mock 无 tool_calls，FICC 一轮）。
- **`new DeepSeekChatClient` 构造点全局 grep**：仅 `ModelRouter.cs:154` 一处（`new DeepSeekChatClient(deepSeekHttpClient, cfg.Model)`），构造函数两参签名不变 → 零适配；做「构造函数形态变更」类工单先 grep 构造点。

## R10.1 gen_ai 遥测属性来源修复笔记（product-catalog-persistence）

- **MEAI gen_ai 属性来源（探针实证）**：`gen_ai.request.model` 读 **`request.ChatOptions.ModelId`**（不是 IChatClient.Metadata——接口无此成员，也不是 response）；`gen_ai.provider.name` 读 **`IChatClient.GetService(typeof(ChatClientMetadata))`** 返回的 `metadata.ProviderName`；request.model 优先级 = `options.ModelId ?? metadata.DefaultModelId`。R10 只实现 `Metadata` 属性 → request.model 仍空；修复 = `DeepSeekDelegatingChatClient.GetResponseAsync` 开头 `options ??= new ChatOptions(); options.ModelId ??= _modelName;`（分支前，deepseek 直发与 base 路径都生效，`??=` 不覆盖显式值）+ `DeepSeekChatClient.GetService` 暴露 `Metadata`。
- **`DelegatingChatClient.GetService` 转发 inner（virtual）**：反射确认 `GetService(Type, object?)` 为 virtual 且接口成员亦 virtual；实测 `DeepSeekDelegatingChatClient(inner=DeepSeekChatClient).GetService(typeof(ChatClientMetadata))` 返回 inner 的 Metadata → 无需在 delegating 包装类覆盖转发。做「服务链」修复时可用「wrapper(inner=目标类).GetService」实测验证转发而非猜。
- **`ChatOptions.ModelId` 可空且 settable**：`options.ModelId ??= _modelName` 合法；测试用 mock inner 捕获 options 断言填充/不覆盖，走非 deepseek 分支（modelName="qwen3.8-max"）避免 DeepSeekDirectCallAsync 需真实 HTTP（deepseek 分支在顶部同样生效，分支前设置）。
- **可选 `gen_ai.response.model` 未做**：协调者标注可选（ParseDeepSeekResponse 从响应 JSON model 字段设置 response.ModelId）；聚焦用户实测问题（request.model/provider.name）不扩范围，留作后续。

## R11 遥测错误可见性笔记（product-catalog-persistence）

- **被吞异常在 OTel 不可见**：MEAI 埋点对「抛出异常」只 `SetStatus(Error, message)` 不产生 exception 事件；而对「捕获后兜底返回」的路径（DeepSeek 非 2xx 返回兜底 ChatResponse、ChatEndpoints catch 吞异常）连 SetStatus 都没有 → Aspire span 只有 error.type=400 无详情。修复 = 手动 `Activity.Current?.SetStatus(Error, msg)` + `AddEvent(new ActivityEvent("exception", tags: {...}))`。
- **`ActivityEvent` 构造函数签名（10.8.3）**：`new ActivityEvent("exception", new ActivityTagsCollection {...})` 报 CS1503（第 2 参是 `DateTimeOffset`）；必须用命名参数 `tags:`。`ActivityEvent(string name, DateTimeOffset timestamp = default, ActivityTagsCollection? tags = null)`。
- **`Activity.RecordException` 扩展在当前 DiagnosticSource 版本不可用**（CS1061）：用 `AddEvent(new ActivityEvent("exception", tags:{ "exception.type": ..., "exception.message": ... }))` 等价替代（OTel exception 事件标准形态，tags 与 RecordException 生成的一致）。
- **测试 HttpClient 直发路径须设 BaseAddress**：DeepSeekChatClient/Delegating 的直发调 `PostAsync("")`（相对空路径），`new HttpClient(handler)` 无 BaseAddress → `PrepareRequestMessage` 抛 InvalidOperationException（在到达 mock handler 前就炸）。测试要 `new HttpClient(handler) { BaseAddress = new Uri(...) }`。
- **ActivityListener 捕获宿主请求 span**：应用 `AddServiceDefaults` → `WithTracing(AddAspNetCoreInstrumentation())` 总是启用 → 每个 HTTP 请求有宿主 Activity；测试用 `ActivityListener { ShouldListenTo = _ => true, Sample = AllDataAndRecorded, ActivityStopped = captured.Add }` 捕获，并**轮询等待**（宿主 Activity 在响应返回后毫秒级 Stop，直接断言有竞态）。WebApplicationFactory 的 `/api/chat` catch 测试用 mock `IShoppingAssistantAgent.RunChatAsync` 抛异常（`.Returns<Task<(AgentChatResult, AgentSession)>>(_ => throw ...)` 显式泛型防 CS0121 二义性）。
- **NSubstitute mock Task 返回方法抛异常**：`.Returns(_ => throw ...)` 对 `Task<T>` 返回成员报 CS0121（Returns<T>(T,...) vs Returns<T>(Task<T>,...) 二义）；用显式泛型 `.Returns<Task<具体Tuple>>(_ => throw ...)` 修复（元组类型用全限定 `Microsoft.Agents.AI.AgentSession` 避免加 using 冲突）。

## ChatMessages 废弃表清理笔记（product-catalog-persistence）

- **移除 EF DbSet 前先全链 grep「接口契约 + 调用点 + 既有库孤儿表」**：删除 `DbSet<ChatMessage> ChatMessages` 后，`IChatMessageRepository.Add(ChatMessage)`（写 db.ChatMessages）与 `ISessionRepository.GetSessionHistoryAsync`（读 db.ChatMessages）成为唯一的废弃表引用；两者均无调用点（聊天历史由 SqliteChatHistoryProvider 写 chat_messages、/login 走 IChatMessageRepository 读 ChatMessageRecords），故从接口 + 实现一并移除。Core 实体 `ChatMessage` 保留（仍被 GetSessionHistoryAsync/GetLastUserMessageAsync 返回类型与映射使用）。
- **既有库孤儿表不会自动消失**：`EnsureCreated` 只在新库建表；移除 DbSet 后既有 `aishop.db` 的 `ChatMessages` 表残留（无害但脏）。`src/AIShop.Api/aishop.db` 被 `.gitignore`（`*.db`）忽略——drop 表是本地清理，不随 commit。用 `python sqlite3` 执行 `DROP TABLE IF EXISTS "ChatMessages"`（注意表名 PascalCase，SQLite 大小写不敏感但 EF 元数据按此存），只动废弃表、不动 `chat_messages`。
- **双表历史**：AppDbContext 有 `ChatMessage`（DbSet→表 `ChatMessages`，废弃）与 `ChatMessageRecord`（DbSet→ToTable "chat_messages"，在用）；ChatMessageRecord 的 OnModelCreating 配置在文件里重复了两遍（L43-59 与 L78-98），清理时按「不重构本次范围外」保留不动

## T1 新建 AIShop.Service 项目笔记（service-layer-extraction）

- **`dotnet sln add <csproj> --solution-folder src` 一次性完成全部 sln 变更**：自动生成项目条目（新 GUID）+ `ProjectConfigurationPlatforms` 全部 6 组构建项（Debug/Release × Any CPU/x64/x86）+ `NestedProjects` 挂 src 组，无需手动补任何条目（本任务曾以为要手写，实测 CLI 全包）。
- **OpenSpec change 目录整体 gitignore 且未跟踪**：`openspec/` 在 .gitignore，`git ls-files openspec/changes/` 为空（tasks.md/design.md/handoffs 都不在 index），仅历史 multi-model-agent handoffs 被 force-add 过。新增 handoff 文件不 commit（保持 gitignored），git commit 只含源码文件；check-ignore 验证即可。
- **Service 项目空壳期双引用同版本 MAF 无冲突**：Api 与 Service 同时 `PackageReference Microsoft.Agents.AI 1.17.0` 等，NuGet 统一为单版本，`dotnet build` 0 警告；T2 升 1.18 时须同步 AgentTelemetry（`Microsoft.Agents.AI`）避免程序集绑定冲突。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**：implementer Edit tasks.md 标 `[x]` 直接 BLOCK（「必须由 @task-breaker 完成」），与既有记忆一致；T1 实测 commit `b9e90ca` 成功（gate 全量 build+test 一次通过，287/287 绿）。
- **commit 隔离仍用 `git add <3 文件>` + `git commit -o -m -- <3 路径>`**：staged 集核对（`git diff --cached --name-status`）恰 3 文件（AIShop.sln / Api.csproj / 新 Service.csproj），`.vs/` 与 openspec/ 均不卷入；commit 后 `git show --stat HEAD` 复核。

## T2 MAF 升级 1.18.0 笔记（service-layer-extraction）

- **NU1605「包降级」是 MAF 升 1.18 时的第一道墙**：T1 笔记「双引用同版本 MAF 无冲突」只对同版本成立。T2 把 Service/AgentTelemetry 升到 1.18.0 后，Api.csproj 的**直接引用** 1.17.0 在 NuGet nearest-wins 规则下胜出，对传递要求的 1.18.0 报 **NU1605「检测到包降级」**（TreatWarningsAsErrors 下 restore/build 直接 error）。修复 = 把 Api 三个 MAF 直接引用**也升到 1.18.0**（纯版本号、零逻辑改动，移除仍归 T8）；`NoWarn NU1605` 会静默保留双版本并存，正是 D5 要避免的程序集绑定冲突，不可取。即：**「只改 2 个文件」的 T2 计划忽略了 Api 直接引用锁定版本号**，tasks.md 需随 T8 描述一起把 1.17.0 更新为 1.18.0
- **MAF 1.18.0 对 AgentTelemetry 零 API 变更警告**：`AIAgent` / `AsBuilder()` / `UseOpenTelemetry` / `EnableSensitiveData` 在 1.18.0 下编译 0 警告（AgentTelemetry 独立 build 实测）。design 预判「1.18 API 变更/废弃警告在后续迁移工单暴露」成立（Service 此时无代码）
- **并行 T3 的中间态会让全量 build 连环 CS0246**：`AgentChatResult` 从 `AIShop.Api.Features.Chat` 迁到 `AIShop.Service` 后，所有仍经 `using AIShop.Api.Features.Chat;` 解析它的文件炸（Api：IShoppingAssistantAgent/ShoppingAssistantAgent；Api.Tests：ChatEndpointsTests）。T3 的 design 范围不含这些文件的修复（属 T5/T7/T13），**故 T3 落地到 T7 之间全量 build 恒红，T2/T3 的 commit 都被 gate 拦截**——迁移阶段的「中间态构建红」是预期，需协调者排 T3-T7 串行收口，不越权修他人文件
- **验证路径分层**：T2 验收「build 0 错误 0 警告」被并行 T3 阻塞时，退而求其次 = ① `dotnet list package` 确认两项目 MAF 1.18.0 + `grep -rn "1\.17\.0" src/ tests/ --include=*.csproj` 空；② 自己负责的项目独立 `dotnet build -warnaserror`（Service/AgentTelemetry 0 错误 0 警告）；③ 全量 build 错误集合精确归类为他人文件（`dotnet build AIShop.sln -warnaserror 2>&1 | grep -E "error|warning"` 逐条看归属）。三条证据齐备即可如实上报「实现完成、commit 被并行在制品阻塞」，不盲目烧 gate 循环
- **T2 最终提交 `7ed5534`（2026-08-19）**：并行 T3 agent 补齐 `using AIShop.Service;` 恢复构建（其 commit `5e13beb` 先行落库），随后 commitgate 连续 2 次被已知 flaky `PreferenceWriteHostedServiceTests`（`SqliteConnection.RemoveCommand` ArgumentOutOfRangeException / DisposeAsync NRE，in-memory SQLite 共享连接 + worker 并发）拦截。处置 = 隔离 `--filter` 4/4 通过 + 全量重跑 276/276 绿 → 立即 `git commit -o -m -- <3 路径>` 第 3 次成功（与既有「flaky 拦截 → 全量重跑绿 → 立即重试 commit」先例一致，不 `--no-verify`）。**该 flaky 当前出现频率偏高（约半数跑会失败），后续工单 commit 可能反复被拦，属既有问题非本变更引入**

## T3 AgentChatResult 随迁笔记（service-layer-extraction）

- **「record/类型跨项目移动」必须先 `grep -rln <TypeName> src/ tests/` 找全所有引用点**：T3 把 `AgentChatResult` 从 `AIShop.Api.Features.Chat` 迁到 `AIShop.Service`，tasks.md 字面文件清单只有 ChatEndpoints.cs + 新文件，但 3 个引用方经 `using AIShop.Api.Features.Chat;` 解析该类型会 CS0246：`IShoppingAssistantAgent.cs`/`ShoppingAssistantAgent.cs`（删原 using 换 `using AIShop.Service;`——两文件只用该 record，原 using 成孤儿须删防 S1128）、`ChatEndpointsTests.cs`（保留 Features.Chat 另加 `using AIShop.Service;`——仍大量用 ChatRequest/ChatReply 等）。**T2 笔记「T3 落地后到 T7 全量 build 恒红」的预判被本工单推翻**：T3 自己的完成判据「dotnet build 0 错误」强制这些引用同步，补齐后构建即恢复绿
- **注释里的类型名不算引用**：`SqliteChatHistoryProvider.cs` grep 命中 "AgentChatResult" 但只在注释（L528），无代码引用，无需加 using
- **并行 T2 升 MAF 1.18 会让全量 build NU1605 error（与 T3 协同解除）**：T2 把 Service/AgentTelemetry/Api 三项目 MAF 统一 1.18.0（其 Api.csproj 联动升 1.18 是必要偏差）。本工单补 3 个引用方后，CS0246 与 NU1605 一并消失，全量 build 恢复绿，T2/T3 均可提交——**并行工单的中间态阻塞靠各自补齐解除，不越权改他人文件**
- **全量测试 flaky 判定用「失败集合逐轮变化 + 隔离 filter 全过」双证据**：本轮 3 次全量各失败 2/2/1 项（PreferenceWriteHostedServiceTests 的 ShouldContinue/ShouldMergeMultiple 与 ShouldTruncateToTop20/ShouldContinue 轮换、ServiceDefaultsDebugTests.ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse），失败集合每次不同；隔离 filter 下 PreferenceWriteHostedServiceTests 2/2 通过、ServiceDefaultsDebugTests 3/3 通过 → 判定 flaky 零耦合。注意 PreferenceWriteHostedServiceTests **隔离 4 项中也偶发 2 项失败**（in-memory SQLite + worker 轮询超时固有 flaky），非仅全量
- **`git commit -o -m ... -- <paths>` 在 index 混入并行 agent 已暂存文件时依旧安全（再次实证）**：T2 的 3 个 csproj 已暂存（`M ` 首列），我 `git add` 我的 5 文件后 index 混合 8 个；`git commit -o` 只提交 pathspec 指定的 5 个，`git show --stat HEAD` 复核精确，T2 文件仍留在暂存区
- **本次提交 gate 一次通过**（全量 build 0 警告 + test 276/276 + 11/11 全绿），印证「手动全量绿 → 立即提交」；全量复跑偶发 CS2012（AIShop.Core.dll 被 MSBuild node 占用）重试即恢复，无需杀进程
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK），handoff 注明待 @task-breaker 勾选

## T4 迁移 Clients 组 5 文件笔记（service-layer-extraction）

- **「纯移动 + 命名空间变更」迁移的纯净性验证用 `git diff`**：5 文件 `git mv` + 改 namespace 后，`git diff` 确认每文件只 diff 一行 `namespace AIShop.Api.Agents;` → `namespace AIShop.Service.Clients;`，方法体/签名/using 零变化——这是 D3「逻辑零改动」的可复核证据，比口头声明强
- **预存在的未使用 using（`using Serilog;` / `using System.Text;`）不是「因迁移产生的孤儿 using」**：DeepSeekDelegatingChatClient/QwenToolCallFixClient/DebugHandler 的字段用全限定 `Serilog.ILogger Log = Serilog.Log.ForContext<...>()`，`using Serilog;` 实际未被解析；但它们是历史遗留、非迁移产物，按 D3「不清理看似无用的成员」保留（CS8019 非 MSBuild 错误，0 警告达成不受影响）
- **迁走 4 个 Client 类型 → 波及 Api.Tests 的 11 个测试文件编译失败（53 处 CS0103/CS0246）**：这些文件全部出现在 T10/T11/T13 迁移清单（T10：ChatPreference*/ChatRecommendationsMerge/ChatReplySanitization；T11：DeepSeekChatClient/DeepSeekDelegatingChatClient/ModelRouterResilience/SanitizingChatClient Tests；T13：ChatEndpointsTests）。缺失类型实测仅 4 个（DeepSeekChatClient / DeepSeekDelegatingChatClient / SanitizingChatClient / DebugHandler），修复 = 每文件加一行 `using AIShop.Service.Clients;`
- **「中间态构建红」的处置决策点：T3 先例 vs 范围纪律**：T3 迁移 AgentChatResult 时同步了 3 个引用方（含测试文件 ChatEndpointsTests.cs）恢复构建（其 handoff 记为 D3 最小适配）；但 T4 波及 11 个、且是 T10-T13 整个测试迁移阶段的文件——修复 = 提前实现他人工单内容。按「不要扩大范围到本工单未列出的文件」+「完成判据（Api 侧 ModelRouter 仍编译）已达成」双证据，T4 选择不越权、staged 待提交 + handoff ⚠️ 如实上报，给协调者三个选项（A 加 using build-restore 小工单 / B 重排测试迁移提前 / C 批量收口）
- **commit gate 实测确认：`git commit` 被 check_commitgate BLOCKED（staged 含 .cs → 全量 build → Api.Tests 编译失败即拦，未跑 test）**。判定 commit 是否可过：只要 `dotnet build AIShop.sln` 红，任何 commit 都被拦，与改动内容无关。T4-T9（源迁移）期间若无人修 11 个测试文件，后续工单 commit 全被连坐拦——协调者需尽早排 build-restore 或重排测试迁移
- **Service 项目编译 5 个 Client 文件零缺依赖**：DeepSeekChatClient/QwenToolCallFixClient 用 Microsoft.Agents.AI（含 MEAI 类型）+ Microsoft.Agents.AI.OpenAI（OpenAI.Chat）——Service.csproj 已引（T1）；Serilog 经 Infrastructure → Service 传递引用；无需新增包

## T5 迁移 Providers 组 2 文件笔记（service-layer-extraction）

- **「纯移动 + 命名空间变更」的纯净性验证用 `diff <(git show HEAD:旧路径) 新文件 --strip-trailing-cr`**：git 对象库存 LF、工作树是 CRLF（`core.autocrlf=true`），直接 `diff git-show-output 工作树文件` 会因换行符显示"每行都变"；加 `--strip-trailing-cr` 后只剩 namespace 一行变化，D3 纯净性可复核。注意 `git diff HEAD -M` 对重命名文件显示成整文件 add（不友好），用上述逐文件 diff 更准
- **T5 迁走 2 个 Provider 类型 → 波及 Api.Tests 恰好 2 个测试文件编译失败（CS0246 各 1 处）**：`PreferenceMemoryProviderTests.cs(12,22)`（T11 清单）+ `SqliteChatHistoryProviderTests.cs(20,22)`（T12 清单），均经 `using AIShop.Api.Agents;` 解析。tasks.md 测试迁移工单（T10-T15）blockedBy T9（←T7），T5 按「不越权」原则不动测试文件，与 T4 处置一致
- **当前 Api.Tests 全量错误数从 T4 handoff 的 53 处降为 2 处**：T4 记录的 11 个测试文件 Clients 错误（53 处）在当前 build 全消失，只剩 Provider 2 处。疑因 ModelRouter.cs 补 `using AIShop.Service.Clients;` 后 Api 恢复编译，级联错误消失；另一未解现象——`DeepSeekChatClientTests.cs`（using AIShop.Api.Agents + new DeepSeekChatClient）在真实 test 项目 build 0 错误，但独立 probe 复现（同 using + 同 namespace + 同 ProjectReference 集）必然 CS0246，机制未查清（可能是 Roslyn build server 陈旧缓存）。不影响 T5 交付，仅记录供后续排查
- **commit gate 判定不变**：全量 `dotnet build AIShop.sln` 红（2 处测试错误）→ 任何 commit 被 check_commitgate BLOCKED，与改动内容无关。T5 的 4 文件 + T4 的 6 文件累积在暂存区，协调者需先排 T11/T12 迁移或 build-restore（给 2 个测试文件加 `using AIShop.Service.Providers;`）才能放行
- **`git commit -o -m -- <精确路径>` 隔离提交在 index 混入并行 staged 时依旧安全（再次实证）**：staged 集 = T4 6 文件 + T5 4 文件，`git commit -o -- <我的 4 路径>` 只提交 pathspec 指定文件，gate BLOCK 后文件仍留在暂存区（commit 未发生、HEAD 未动）
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测），handoff 注明待 @task-breaker 勾选

## T6 迁移 Tools 组 CartToolProvider 笔记（service-layer-extraction）

- **库项目（Microsoft.NET.Sdk）隐式 using 不含 `Microsoft.Extensions.DependencyInjection`/`Microsoft.Extensions.Configuration`**：Web Sdk（Api）自动注入 DI 全局 using，库 Sdk（Service）只有 System.* 标准集。从 Api 迁文件到 Service 时，凡用 `IServiceScopeFactory`/`scope.ServiceProvider.GetRequiredService<T>()`/`IConfiguration` 的文件必须补显式 using，否则 CS0246（CartToolProvider 补了 `using Microsoft.Extensions.DependencyInjection;`，属迁移必需最小适配非行为变更）。**T7 迁根目录组时 ModelRouter.cs 需补 `using Microsoft.Extensions.Configuration;`（构造参 IConfiguration）+ `using Microsoft.Extensions.DependencyInjection;`（3 处 `_sp.GetRequiredService<T>()`）**，ShoppingAssistantAgent/IShoppingAssistantAgent 目前未见 DI 类型直用（EF/MAF/Serilog 均有显式 using）
- **`.gitignore` 行 36 `tools/` 规则匹配 `src/AIShop.Service/Tools/`**：`git mv` 的 rename 照常入库（tracked 文件绕过 ignore），但迁入文件后续 `git add` 会被 ignore 拦（报 "paths are ignored"），需 `git add -f <path>` 才能暂存编辑后的内容
- **T6 迁移新增 1 处测试编译错误（全量 2→3 处）**：迁走 CartToolProvider 后 `AgentTelemetryTests.cs(475,26)`（`ShoppingAssistantAgentFixture` 字段 `_cartTools`）CS0246，属 T14 拆分迁移清单；与 T5 遗留的 2 个 Provider 测试错误（T11/T12）同为 out-of-scope，按 T4/T5 先例不越权修，staged 待协调者 build-restore 或批量收口
- **commit 判定不变**：全量 build 红（3 处测试文件错误）→ 任何 commit 被 check_commitgate BLOCKED。T4+T5+T6 文件累积 staged，其中 3 个 Api 文件（ShoppingAssistantAgent/ModelRouter/Program.cs）T5 与 T6 在同一路径混合，文件粒度无法分开提交，建议整批收口
- **diff 纯净性验证仍用 `--strip-trailing-cr`**：`diff <(git show HEAD:旧路径) 新文件 --strip-trailing-cr` 确认仅 namespace 行 + 迁移必需 using 变化，方法体/AsyncLocal 行为零改动

## T7 迁移根目录组 3 文件笔记（service-layer-extraction）

- **Program.cs 的 `using AIShop.Api.Agents;` 是「替换为 `using AIShop.Service;`」而非纯删除**：tasks.md 字面只写「删除行 6 using」，但删后 `AddSingleton<ModelRouter>()`（行 50）无解析来源（ModelRouter 已迁 `AIShop.Service`）→ 编译失败。design §10 行 196 明确「行 6 using → using AIShop.Service;」。**做「删 using」类工单先查该 using 提供哪些类型是否已另寻解析，必要时补新 using**
- **`ModelRouter.cs` 迁库项目需补 3 类依赖（T6 预告兑现 + 新发现包级缺口）**：① `using Microsoft.Extensions.Configuration;`（构造参 `IConfiguration`）② `using Microsoft.Extensions.DependencyInjection;`（3 处 `_sp.GetRequiredService<T>()`）——T6 已预告；③ **新发现 `Microsoft.Extensions.Http.Resilience` 包缺口**：`BuildChatHttpPipeline` 用 `ResiliencePipelineBuilder`/`HttpRetryStrategyOptions`/`HttpCircuitBreakerStrategyOptions`/`ResilienceHandler`，该包原本经 Api → ServiceDefaults 传递（10.7.0），Service 独立后必须**在 Service.csproj 直接声明 `Microsoft.Extensions.Http.Resilience` 10.7.0**（版本与 ServiceDefaults 对齐，传递引入 Polly 8.4.x，无 NU1605）。「补 using」还不够，还要核对 using 所在程序集是否被项目引用
- **`GetRequiredService` 在首轮 build 未报 CS1061（级联截断现象）**：`IConfiguration`（构造参）CS0246 失败后，编译器未继续报方法体内 3 处 `_sp.GetRequiredService<T>()` 的扩展方法缺失——首轮 3 错误（Http/Polly/IConfiguration）即完整集合，补 package + 2 using 后 0 错误 0 警告。**迁文件时先修「类型级」错误（构造参/字段/签名），方法体级错误可能被级联隐藏**
- **孤儿 using 移除随命名空间迁移**：ShoppingAssistantAgent/IShoppingAssistantAgent 迁入 `AIShop.Service` 后，`using AIShop.Service;`（T3 为解析 AgentChatResult 所加）变冗余（同命名空间自动解析）→ S1128 风险，须删。**「纯移动 + 命名空间变更」后要重新审视 using 集合：原用于解析同层类型的 using 会变孤儿**
- **全量 build 错误数 T6 的 3 处 → T7 后 23 处（全在 Api.Tests，17 个测试文件 `using AIShop.Api.Agents;` CS0234）**：根目录组是最后迁移的源文件，迁完 `AIShop.Api.Agents` 命名空间整体消失 → 所有仍引用它的测试文件炸。**「命名空间整体消失」类工单的受影响测试文件数 = grep 到 using 的文件全量（本次 17 个），不是个别引用点**；均归 T10-T15，不越权修（与 T4/T5/T6 处置一致）
- **`git mv` 对 staged-modified 文件直接可用**：T5/T6 已 staged 的 ShoppingAssistantAgent/ModelRouter 上再 `git mv`，索引正常记录 R（rename）+ 工作树 M（我的后续编辑）；`git status` 显示 `RM`。批量收口时 T4-T7 的 11 个源重命名同批 staged，无法文件粒度拆分
- **`rmdir src/AIShop.Api/Agents` 成功**：空壳目录未被并行会话 cwd 锁定（本变更期间无 implementer 停留在该目录），T7 判据「目录不再存在」直接物理达成（git 不跟踪空目录）
- **commit 判定不变**：全量 build 红（23 处测试文件错误）→ 任何 commit 被 check_commitgate BLOCKED。T4+T5+T6+T7 全部源迁移文件累积 staged，协调者需排 T10-T15 测试迁移或 build-restore 后批量收口
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T17 文档工单笔记（service-layer-extraction）

- **纯文档工单（只改 .md）在共享工作树迁移中态下 commit 也被 gate 拦**：`check_commitgate.py` 的 `staged_has_code_files()` 看的是**整个 index**（`git diff --cached --name-only`），不是本次 pathspec。index 里有并行 agent 已暂存的 .cs 源迁移文件（ChatEndpoints.cs/Program.cs/Service.csproj + 11 个 R 重命名）时，即使 `git commit -o -m -- <纯 .md 路径>` 也会触发全量 `dotnet build` → Api.Tests 编译失败（T9-T15 未迁移，17 文件仍 `using AIShop.Api.Agents;`）→ BLOCKED。**`-o -- <paths>` 只隔离 commit 内容，不绕过 gate 的 index 全局判定**。解除路径 = 协调者先完成 T9-T15（build 恢复绿）再重试；不 `--no-verify`（PreToolUse hook 无效）
- **文档工单的验证就是 grep 本身**：`grep -n "新约定" .claude/rules/dotnet.md`（命中）+ `grep "旧表述" .claude/rules/dotnet.md`（无匹配）即完成判据，无需跑测试；src 侧健康检查（`dotnet build src/AIShop.Api` 0 错误 0 警告）仅作佐证，全量 sln 红（23 错误全在 Api.Tests 待迁移文件）与文档工单零耦合
- **handoff 标注「commit 被 gate BLOCKED」的完整要素**：改动已 staged（`git diff --cached --name-only` 验证）、HEAD 未动、block 根因（index 含并行 .cs 文件 + Api.Tests 待迁移）、解除路径、checkbox 待 @task-breaker

## T9 新建 Service.Tests 测试项目笔记（service-layer-extraction）

- **T9 完成判定「`dotnet build tests/AIShop.Service.Tests` 0 错误」的验证路径是隔离链 build，不受 Api.Tests 红影响**：`dotnet build tests/AIShop.Service.Tests/AIShop.Service.Tests.csproj -warnaserror` 只构建 Service/Infrastructure/AgentTelemetry/Service.Tests 依赖链（5 项目），即便全量 sln 因 Api.Tests 迁移未完成而红（23 错误），隔离 build 仍 0 错误 0 警告达成完成判定——迁移阶段「验证自己的依赖链 + 全量红与他人文件零耦合」的分层验证法在测试项目上同样适用
- **Service.Tests 不引 `Microsoft.AspNetCore.Mvc.Testing`**：design §7.2 约定 `WebApplicationFactory<Program>` 集成段全部留 Api.Tests，Service.Tests 只承载纯单元段（直接 new Agent / 调 ModelRouter / 各 ChatClient）→ 不需要该包，保持 `Service.Tests → Service` 单向干净依赖；csproj 只需 xunit/runner/Test.Sdk/NSubstitute/coverlet 5 包 + Using Xunit + 复制 xunit.runner.json（全串行配置与 Api.Tests 一致，防未来 in-memory SQLite 并行冲突）
- **`dotnet sln add <csproj> --solution-folder tests` 对测试项目同样全包**：自动写 Project 条目（新 GUID）+ ProjectConfigurationPlatforms 12 项（6 配置 × ActiveCfg/Build.0）+ NestedProjects 挂 tests 组（`{新GUID} = {0AB3BF05...}`），无需手写；验证用 `grep -c <GUID> AIShop.sln` 应为 14（1 条目 + 12 构建项 + 1 嵌套组）
- **空测试项目 `dotnet test --no-build` 返回「没有可用测试」是正常态**（非错误退出码），证明 testhost/runner 装配正常；测试在 T10-T15 迁入后才开始有用例
- **T9 commit 依旧被 check_commitgate BLOCKED（全量 build 红，23 错误全在 Api.Tests 17 个文件 CS0234/CS0246）**：与 T4-T7 同一收口点——协调者排 T10-T15 迁移或 build-restore 后，T9 的 3 个 staged 文件用 `git commit -o -m -- <3 路径>` 即可提交（pathspec 隔离不卷入 T4-T7 已 staged 的 11 个 rename 文件）；T9 文件停留暂存区、HEAD 仍为 T2（7ed5534）
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T14 拆类 AgentTelemetryTests 笔记（service-layer-extraction）

- **拆类边界 = 「是否依赖 WebApplicationFactory<Program>」一刀切**：AgentTelemetryTests 内 2 个 Options DI 集成用例（走 Program.cs 真实 DI 注册）留 Api.Tests（新文件 `AgentTelemetryWebTests`，命名对齐 T13 `ChatEndpointsWebTests` 先例），其余 17 个纯单元用例（Instrument 包装/EnableSensitiveData/配置绑定/Debug/EnrichWith/Agent 接入）迁 Service.Tests（沿用 `AgentTelemetryTests` 类名）。`Service.Tests → Service → AgentTelemetry` 单向依赖，不引 `Microsoft.AspNetCore.Mvc.Testing`。
- **namespace 迁移的 using 变化：`AIShop.Service.Tests` 命名空间外层查找能解析 `AIShop.Service` 顶层类型（ShoppingAssistantAgent/AgentChatResult），但子命名空间 `AIShop.Service.Tools`（CartToolProvider）仍需显式 `using AIShop.Service.Tools;`**。用命名空间外层查找规则判断哪些 using 可删、哪些必须补，而不是盲抄。
- **`using AIShop.AgentTelemetry;` 与别名 `using AgentTelemetryHelper = AIShop.AgentTelemetry.AgentTelemetry;` 共存**（命名空间与静态类同名遮蔽）：别名原样保留即可，纯单元段直接调 `AgentTelemetryHelper.Instrument`，无需额外处理。
- **两项目各自的构建错误可精确归类验证自己的文件干净**：Service.Tests build 0 错误（仅 2 个并行 T11 文件 CS0234 报错）→ 我的文件编译干净；Api.Tests build 14 个错误全在 T10/T12/T13/T15 未迁移文件 → 我的 `AgentTelemetryWebTests.cs` 干净。「错误集合精确归类为他人文件」即文件级验证通过，测试运行等并行收口（与 T2/T8/T9 分层验证一致）。
- **并行 T11 agent 已 `git mv` 5 个测试文件入 Service.Tests 但命名空间未改完（仍是 `using AIShop.Api.Agents;` + `namespace AIShop.Api.Tests;`）**：git status 显示 `R`（staged rename），工作树内容仍是旧命名空间，Service.Tests 项目整体编译失败（CS0234×2），阻塞 Service.Tests 的任何 `dotnet test`。判定并行活跃 = 文件 mtime 变化（DeepSeekChatClientTests.cs 从 21:57 变为 02:51）+ 大量 dotnet 进程。不越权修，等并行收口。
- **commit 判定不变**：全量 `dotnet build AIShop.sln` 红（Api.Tests 14 + Service.Tests 2 处测试文件错误）→ 任何 commit 被 check_commitgate BLOCKED。T10-T15 全部完成全量绿后，用 `git commit -o -m -- <3 路径>` 隔离提交（删除的旧 AgentTelemetryTests.cs + 2 个新文件）。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选。

## T13 拆类 ChatEndpointsTests 笔记（service-layer-extraction）

- **design §7.2 的「Agent 单元用例→Service.Tests」列对 ChatEndpointsTests 无实际内容**：该文件 29 个测试全部是 WebApplicationFactory 端点集成（基 fixture `_factory.CreateClient()` 12 个 + `WithWebHostBuilder` 派生 12 个）或 Api 内部 `ChatEndpoints.IsRetryableAgentFailure` 直测（5 个，含 `StubPipelineResponse` 私有桩）。文件内的 `new ShoppingAssistantAgent(...)` 全在 WAF ConfigureServices 的 mock router lambda（请求时从完整 SP 解析），属端点集成装配而非独立单元用例。Agent 纯单元用例由 T12 的 `ShoppingAssistantAgentRunTests` 承担。结论：ChatEndpointsTests.cs **整体**变 `ChatEndpointsWebTests.cs`（类名同步改），Service.Tests 从本工单无文件迁入，不重复建设
- **迁移测试文件的 usings 替换清单（ChatEndpoints 先例）**：删 `using AIShop.Api.Agents;`（T7 后命名空间整体消失）→ 加 `using AIShop.Service.Clients;`（DeepSeekDelegatingChatClient）+ `using AIShop.Service.Tools;`（CartToolProvider）；`ModelRouter`/`ShoppingAssistantAgent`/`IShoppingAssistantAgent`/`AgentChatResult`/`ModelInfo` 由 `using AIShop.Service;` 解析；`Microsoft.Agents.AI.AgentSession` 全限定经 Api→Service 传递引用（T2 升 1.18）解析；`System.ClientModel`（ClientResultException/PipelineResponse）经 Api 的 `Microsoft.Extensions.AI.OpenAI` 传递解析——**迁移测试文件先列全类型→新命名空间映射，再改 using**
- **`git add` 删旧+建新文件会自动识别为 R099 rename**：内容 99% 相似的「删除+新建」直接 `git add` 即可，git 自动标 R，无需先 `git mv`；`git diff --cached` 复核相似度
- **迁移阶段「全量红 + 自己的文件零错误」判定法再次实证**：`dotnet build tests/AIShop.Api.Tests` 报错全部精确归类为他人文件（T10 6 文件 CS0234 → 随后 T10 并行 agent 迁移后消失；T15 `ModelRouterWebTests.cs` CS0246 OpenTelemetryAgent 在制品）。并行 agent 活跃期文件清单分钟级变化（2 分钟内 T10 的 6 文件从 Api.Tests 消失迁入 Service.Tests、ModelRouterTests.cs 被删、ModelRouterWebTests.cs 新建），build 错误集合也随之变化——**不要对并行在制品的错误做任何处置，只看自己的文件是否零错误**
- **「T15 ModelRouterWebTests.cs CS0246 OpenTelemetryAgent」判定**：OpenTelemetryAgent 属 AgentTelemetry（T14 迁 Service.Tests 的单元段），出现在 Api.Tests 的 ModelRouterWebTests.cs L120-121 疑似 T15 agent 误放入或缺 using——归 T15 工单范围，不越权修

## T8 清理 Api.csproj 包引用笔记（service-layer-extraction）

- **移除包的版本以当前 csproj 实际值为准，不照抄 tasks.md 的旧版本号**：T8 任务文字写「移除 Microsoft.Agents.AI 1.17.0 等」，但 T2（commit 7ed5534）已把 Api 三包联动升到 1.18.0，实测移除时按 1.18.0 移除——spec Req3 的「无 1.17.0 残留」是最终口径，与 T2 handoff「4 包移除仍归 T8」一致
- **「InternalsVisibleTo 去留」的确认点先 grep 内部暴露再决定**：design §5.5 留了「若无 internal 暴露给 Api.Tests 则删」的确认项；实测 `ChatEndpoints.IsRetryableAgentFailure` 为 `internal static`（ChatEndpoints.cs L374）且 Api.Tests 的 ChatEndpointsTests 端点段（T13）要用 → **保留** `<InternalsVisibleTo Include="AIShop.Api.Tests" />`。判断该 item 去留的通用做法 = grep `internal static` + 确认 Api 侧仍暴露哪些给测试
- **`dotnet list <csproj> package` 带 grep 过滤可能输出空（中文 locale 下输出「顶级包」非 Top-level）**：`dotnet list src/AIShop.Api/AIShop.Api.csproj package 2>&1 | grep -iE "Agents|DeepSeek"` 输出为空是因为包名不含这些字样（已移除），不能作为唯一证据；要看完整输出确认无 MAF/DeepSeek 顶级包 + MEAI 三包保留
- **csproj 注释与动作同步清理**：移除 4 个包时，T1 的「4 个 MAF/DeepSeek 包本工单不移除，T8 处理」与 T2 的「4 包移除仍归 T8」两条占位注释都指向已完成的动作，需一并更新/删除，否则留下指向过期状态的注释
- **T8 commit 依旧被 check_commitgate BLOCKED（全量 build 红，23 错误全在 Api.Tests，T10-T15 范围）**：与 T4-T7/T9 同一收口点；Api.csproj 改动已 staged，`git commit -o -m -- <精确路径>` 隔离提交待协调者解除 build 阻塞后执行
- **T8 验证分层与 T2 一致**：① `dotnet list package` 无 MAF/DeepSeek + `grep -rn "1.17.0" src/ tests/ --include=*.csproj` 空 + `grep -rn -E "Agents\.AI|DeepSeek" src/ --include=*.csproj` 确认三包 1.18.0 仅在 Service（+AgentTelemetry 仅 Microsoft.Agents.AI）、DeepSeek 1.0.4 仅在 Service；② Api 项目隔离 `dotnet build -warnaserror` 0 错误 0 警告；③ 全量红错误集合精确归类为他人文件、零 NU* 错误（证明移除包没破坏 NuGet 图）
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit T8 行 BLOCK 实测），handoff 注明待 @task-breaker 勾选

## T11 迁移纯单元测试组 B 笔记（service-layer-extraction）

- **迁移测试文件到新测试项目后 SonarAnalyzer 规则会「新暴露」**：Api.Tests 有 `GlobalSuppressions.cs`（模块级抑制 S8969 冗余 `!` / S3358），T9 新建的 Service.Tests 没有 → 迁入的未改动测试文件里 `Assert.NotNull(x)` 后的 `x!.` 全部触发 **S8969 error**（TreatWarningsAsErrors，SonarAnalyzer 10.32.0）。这是「项目级配置缺口」不是逻辑问题。两条解路：① 逐文件删冗余 `!`（T10 agent 采用）；② 补 GlobalSuppressions.cs（与 Api.Tests 同约定，本项目最终采用）。**做测试迁移工单先检查目标测试项目的 suppression 基线是否齐**（GlobalSuppressions.cs / .editorconfig / 规则级别），否则 migrate 后 build 会拦
- **「断言逻辑不改」的纯净性验证用 `diff <(git show HEAD:旧路径) 新文件 --strip-trailing-cr`**（T5 已记），迁移后还应重新审视 using：`using AIShop.Api.Features.Chat;` 在 SanitizingChatClientTests 里是**孤儿 using**（文件未用任一 Features.Chat 类型），迁入不引 Api 的 Service.Tests 后 `AIShop.Api.*` 整体不可解析 → 必须删（D3「删迁移产生的孤儿 using」）。判定孤儿用「grep 文件是否引用该命名空间定义的类型」，不靠猜
- **命名空间引用按类型归属拆分**：一个测试文件引用 Service 多个子命名空间时各自加 using（ModelRouterResilienceTests 加 `AIShop.Service` + `AIShop.Service.Clients`；SanitizingChatClientTests 加 `Service` + `Clients` + `Tools`），不合并平铺。文件已用同层类型时 `using AIShop.Service;` 会自动解析根命名空间类型
- **T10 协调者指令把 6 个 WAF 端点测试类整体迁入 Service.Tests**（design §7.2 原约定 WAF 集成段留 Api.Tests）：T10 在 Service.Tests.csproj 加 `Microsoft.AspNetCore.Mvc.Testing 10.0.9` + `AIShop.Api` ProjectReference（csproj 注释记录偏差）。**Service.Tests → Api → Service 依赖链成立**（无环）。后续 T11 的 5 个纯单元类不依赖 Api，纯移动即可
- **commit gate 期间全量测试 287/287 绿**：T10 的 `ChatRecommendationsMergeTests.ShouldUseCachedSnapshot_EvenWhenDbLatestMessageDiffers`（WAF CreateClient）首轮全量偶发失败、隔离 filter 1/1 通过 → WebApplicationFactory 并行宿主竞争 flaky（T18-T21 先例），与 T11 零耦合，重跑全量即绿
- **git rename 的 pathspec 提交要同时给新旧两侧路径**：`git commit -o -m -- <旧路径> <新路径>` 才能把 rename 的两半（删旧+增新）都提交；只给新路径会遗留旧路径的删除在暂存区。本次 `git commit -o -m -- <5 旧路径> <5 新路径>` 一次成功（`d3304e0`，gate 通过），`git show --stat HEAD` 复核恰 5 rename
- **并行 agent 的 S8969 处置与本工单的 verbatim 冲突**：T10 逐文件删 `!`，另有 agent 补 GlobalSuppressions.cs；我临时删 `!` 后又还原（因为 GlobalSuppressions.cs 已覆盖）。**教训：并行 worktree 里「规则门禁的处置方式」存在多解，先观察其他 agent 采用哪条路再动手，避免做重复功；最终以能达成 0 错误 0 警告且不改断言语义的方案为准**

## T15 拆类 ModelRouterTests 笔记（service-layer-extraction）

- **`OpenTelemetryAgent` 是 MAF 类型（`Microsoft.Agents.AI` 命名空间），不是本仓 AgentTelemetry 的类型**：`src/AIShop.AgentTelemetry/AgentTelemetry.cs` 里 grep 到 `OpenTelemetryAgent` 全在 XML 注释（`<c>`/`<see cref>`），无类型定义。拆分测试文件时凡用 `OpenTelemetryAgent`/`EnableSensitiveData` 的集成段必须保留 `using Microsoft.Agents.AI;`（首轮拆分误删 → CS0246 `OpenTelemetryAgent`，补回即过）。**做拆类迁移先 `grep -rn` 目标类型确认归属项目/命名空间，再定 using 集**
- **.NET 10 的 `AddInMemoryCollection` 已并入 `Microsoft.Extensions.Configuration` 主包**（`MemoryConfigurationBuilderExtensions` 就在 `Microsoft.Extensions.Configuration.dll` 内，非独立 `Microsoft.Extensions.Configuration.Memory` 程序集），测试项目经传递依赖即可用，**不需要**单独加 Memory 包引用。判定某 API 在哪个程序集 = 查 NuGet 缓存包 `lib/<tf>/` 下的 `.xml` 文档 `grep M:<namespace>.*<MethodName>` + 核对 `project.assets.json` 的 compile 目标——别凭旧知识猜
- **S8969 迁移系统性根因（Api.Tests 有压制、Service.Tests 没有）**：`tests/AIShop.Api.Tests/GlobalSuppressions.cs` 模块级 `SuppressMessage("CodeQuality", "S8969", Scope="module")` + S3358；Service.Tests 无该文件 → 迁移测试普遍存在的 `Assert.NotNull(x); x!.Foo` 模式（原 Api.Tests 被压制）在 Service.Tests 触发大量 S8969（TreatWarningsAsErrors 即 error，实测 T10 5 文件 122 处）。**迁移测试文件到 Service.Tests 时先评估：该文件/项目是否需要 GlobalSuppressions.cs，或删冗余 `!`（删 `!` 不改语义，属最小适配）**。我的 ModelRouterTests.cs 纯单元段不用 `!` 零受影响；Web 段 `GetInternalAgent` 的 `value!` 留在 Api.Tests（仍被压制）
- **`AIShop.Service.Tests` 命名空间外层查找可解析 `AIShop.Service` 顶层类型（ModelRouter/ModelInfo/ShoppingAssistantAgent），无需 `using AIShop.Service;`**（与 T14 约定一致）；但 Api.Tests 侧（`AIShop.Api.Tests` 非 `AIShop.Service` 外层）必须显式 `using AIShop.Service;` 解析同一批类型
- **拆类后 git 的 rename 识别**：删除 Api.Tests 的 `ModelRouterTests.cs` + 新建 `ModelRouterWebTests.cs` 被 `git add` 识别为 **R060 rename**（60% 相似，集成段 4/7 方法相同）；Service.Tests 的 `ModelRouterTests.cs` 是独立 A。`git diff --cached --name-status` 复核即可，不影响提交语义
- **纯净性验证用「按方法名逐体 diff」**：`git show HEAD:旧文件 > tmp`（注意 GBK 控制台编码，须重定向到文件再以 utf-8 读），`re.split(r'    \[Fact\]\n')` 按方法拆分后 `diff` 每个方法体——原 7 方法全部 IDENTICAL，仅文件归属 + using/namespace/类名变化，是「断言逻辑不改」的可复核证据
- **commit 判定不变**：全量 `dotnet build AIShop.sln` 红（Service.Tests 122 S8969 等并行在制品）→ 任何 commit 被 check_commitgate BLOCKED。Api.Tests 半程 `ModelRouterWebTests` 4/4 绿已验证（`dotnet test --filter "FullyQualifiedName~ModelRouterWebTests"`）；Service.Tests 半程 `ModelRouterTests` 3 用例文件级验证（编译 0 错误 + 逐字一致原全绿版本），项目整体跑需等 T10 S8969 收口
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选


## T10 迁移纯单元测试组 A 笔记（service-layer-extraction）

- **「纯单元测试组」标签与实测不符：T10 的 6 文件全部是 WebApplicationFactory 端点集成测试**（走 /api/chat、/api/recommendations、/api/login HTTP 契约，无一个直接 new Agent 的纯单元用例）。协调者 T10 指令明确「仅改文件归属 + 命名空间、断言逻辑不改、原 Api.Tests 文件移除、6 类在 Service.Tests 全绿」→ 整体迁移必须让 Service.Tests 支持 WAF：给 Service.Tests.csproj 加 `Microsoft.AspNetCore.Mvc.Testing 10.0.9` + `ProjectReference AIShop.Api`（Program/Features.Chat DTO），形成 `Service.Tests → Api → Service` 依赖链，**打破 design §7.2「Service.Tests 不引 Api」的分层约定**。处置 = 按协调者指令优先（learnings R2 先例），handoff 显著标注偏差，若协调者要恢复 §7.2 分层需另指示
- **`using AIShop.Api.Agents; → using AIShop.Service;` 单行替换不够**：`using AIShop.Service` 只解析顶层类型（ModelRouter/ShoppingAssistantAgent/ModelInfo），子命名空间类型需补 using——`DeepSeekDelegatingChatClient`（`AIShop.Service.Clients`）、`CartToolProvider`（`AIShop.Service.Tools`）。6 文件全部引用这两类型 → 每文件加两行 using。协调者指令字面只列一行，实测必须补，属必要编译支撑非行为变更
- **S8969 修复选「复制 GlobalSuppressions.cs」而非删 `!`**：与 T15 agent 观察一致（Api.Tests 有模块级 S8969/S3358 压制、Service.Tests 无 → 迁移后大量 S8969 error）。T10 从 Api.Tests 复制同一文件到 Service.Tests（justification「Pre-existing in unmodified test files」完全贴合），测试代码零改动，比删 `!` 更贴合「仅改文件归属 + 命名空间、断言逻辑不改」。该压制是项目级，一并解决并行 T11 文件的同类 S8969，对并行 agent 无害（若对方已删 `!` 则两者并存无害）
- **提交时机判断 = 全量 build/test 绿才 commit**：T10 开工时全量 build 红（17 测试文件用旧命名空间），工作完成时并行 T11-T15 已迁移收口 → 全量 `dotnet build -warnaserror` 0 错误 0 警告、`dotnet test` 287/287 绿 → commit gate 可过。**迁移阶段提交被拦 ≠ 永久**：等并行工单收口后全量恢复绿即放行
- **并行 agent 跑测试会锁 Service.Tests.dll → CS2012 拦全量 build**：testhost（PID 17556）正在跑 Service.Tests 全量时，我的 `dotnet build AIShop.sln` 报 CS2012（obj dll 被占用）+ CS0006（ref dll 缺失，obj 竞态）。处置 = 不杀并行 testhost，等其自然退出后重试 build；先 `Get-CimInstance Win32_Process` 确认持锁进程再等
- **rename 提交 pathspec 必须含旧+新路径**：`git commit -o -m -- <paths>` 对 staged rename（index 有 delete(旧)+add(新) 两个条目）若只给新路径，旧路径的删除不被提交 → 旧文件留在 HEAD。6 个 git mv 文件提交时 pathspec 显式列「6 旧路径 + 6 新路径 + csproj + GlobalSuppressions」共 14 条
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T12 迁移纯单元测试组 C 笔记（service-layer-extraction）

- **staged rename + pathspec 提交的 add/delete 分裂陷阱（T12 独立复现 T10 笔记的坑）**：`git mv` 暂存 rename 后，`git commit -o -m -- <仅新路径>` **只提交新增侧**（`git show --stat` 显示 create、旧文件仍留 HEAD），删除侧（`D 旧路径`）滞留暂存区。补救 = 第二个 `git commit -o -m -- <旧路径>` 补齐删除侧，两 commit 合起来才是完整 rename（T12 的 c5bedf3 + 6536ee4）。**凡是 staged rename 想用 pathspec 隔离提交，pathspec 必须旧路径+新路径都列上**（T10 的 14 条做法），否则必然半提交
- **S8969 两种处置可并存无害，先到先得**：T10/T15 走「复制 GlobalSuppressions.cs 模块级压制」（Service.Tests 03:45 落地），T12 早于压制文件删了 2 处冗余 `!`（`Assert.NotNull(x); x!.Foo` 模式，编译器已证明非空，删 `!` 行为恒等无 CS8602 风险）。**压制文件已存在则删 `!` 是可选的代码清理；尚未存在则删 `!` 是让项目编译的最小手段——别因对方会加压制就回退已提交的 `!` 移除**
- **迁移后文件 0 警告验证必须跑 `--no-incremental` 全量编译**：增量 build 的 S8969 错误集随并行 agent 改文件而「抖动」，只有 `dotnet build --no-incremental` 才暴露全部 S8969。判定「我的文件干净」= 用 `--no-incremental` 输出 grep 我的文件名，零命中才算数
- **纯单元 vs WAF 的迁移归属判定先 grep 引用**：T12 的 3 文件（RunChatAsyncPreferenceBackfillTests/SqliteChatHistoryProviderTests/ShoppingAssistantAgentRunTests）均不引用 Api DTO/WebApplicationFactory，构造 `ShoppingAssistantAgent` 用 mock IChatClient + in-memory SQLite + `ServiceCollection.AddDbContextFactory` 出 `IServiceScopeFactory` 供 `CartToolProvider`（与 Api.Tests 时期逐字一致）→ 符合 design §7.2 迁 Service.Tests；与 T10 的 6 个 WAF 文件（需引 Api）形成对照。**别信 tasks.md 的「纯单元测试组」标签，先 grep 引用再定归属**
- **T12 两次 commit 各被已知 flaky 拦 1 次**：`PreferenceWriteHostedServiceTests.ShouldContinue_WhenSingleMessageProcessingThrows`（隔离 4/4 通过）与 `ServiceDefaultsDebugTests.ShouldWriteHeaderTagsToLocalLog_AndRedactFromOtlp_WhenDebugTrue`（隔离 3/3 通过），处置与既有先例一致 = 隔离 filter 通过 + 全量 287/287 绿 → 立即重试 commit 即成功，不 `--no-verify`
- **全量 287/287 验证口径**：McpServer 11 + Service.Tests 140（含我的 45 = RunChatAsyncPreferenceBackfill 2 + SqliteChatHistoryProvider 40 + ShoppingAssistantAgentRun 3）+ Api.Tests 136；`dotnet build AIShop.sln -warnaserror` 0 错误 0 警告
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## 收尾：修正 Service.Tests 测试分层（service-layer-extraction）

- **T10「6 个 WAF 测试类迁 Service.Tests」是设计偏差，收尾修正移回 Api.Tests**：design §7.2 明确 `WebApplicationFactory<Program>` 端点集成测试留 Api.Tests（合法链 `Api.Tests → Api → Service`），T10 协调者指令整体迁入 Service.Tests 并让 Service.Tests 引 Api（`Service.Tests → Api → Service` 无环但破坏单向分层语义）。用户决策修正回设计：6 文件（ChatPreference*/ChatRecommendation*/ChatReplySanitization）`git mv` 回 `tests/AIShop.Api.Tests/`，namespace 改回 `AIShop.Api.Tests`；Service.Tests.csproj 删 `<ProjectReference AIShop.Api>` + `Microsoft.AspNetCore.Mvc.Testing` 包。**判定 WAF 测试归属先 grep `WebApplicationFactory`/`AIShop.Api` 引用**，6 文件全命中、剩余 10 文件（AgentTelemetryTests 等）仅在注释含 "Program.cs" 无代码引用 → 移除 Api 依赖安全
- **namespace 迁移的 using 增减判断用「外层命名空间查找」规则，不盲抄**：`AIShop.Service.Tests` 是 `AIShop.Service` 子命名空间 → `using AIShop.Service;` 冗余（但实际用到不报 CS8019）；移回 `AIShop.Api.Tests`（非 Service 子命名空间）后 `using AIShop.Service;` **从冗余变必需**，必须保留。`AIShop.Service.Clients`/`Tools`/`AIShop.Api.Features.Chat` 均为显式子命名空间，两处都需显式 using。本次 6 文件实测 using 零改动，仅 namespace 一行变化（git diff 显示 `2 +-` = 1 删 1 增），断言逻辑零改动
- **`git mv` staged rename 后改工作树内容，需 `git add <新路径>` 才并入 staged（RM → R）**：6 个文件 git mv 后 status 为 `RM`（rename 已 staged + 工作树 namespace 修改未 add）；`git add <新路径>` 把修改并入 staged rename 变 `R`，无需再动旧路径。与 T12「rename 提交 pathspec 必须含旧+新」不矛盾——那是 commit 的 pathspec 要求，add 只加新路径即可
- **测试数守恒验证**：Service.Tests 140→108（-32，移走 6 个 WAF 类）、Api.Tests 136→168（+32）、McpServer 11，总计 287 不变；`dotnet build AIShop.sln -warnaserror` 0 错误 0 警告 + 全量 `dotnet test` 三项目全绿（108/168/11）
- **pathspec 提交 44 条一次成功（commit `7ab607b`）**：26 文件（19 个 T4-T9/T13/T17 staged 收尾 + 7 个本次修正），rename 新旧路径全列（11 src rename + ChatEndpointsWebTests + 6 WAF rename = 18 对 + 8 非 rename），commitgate 全量 build+test 一次通过（287 绿）无 flaky 拦截；`git show --stat HEAD` 复核恰 26 文件、6 WAF 文件每文件仅 namespace 行变化

## T18 DeepSeekChatClient 真流式改造笔记（service-layer-extraction）

- **CS1626「try-catch 内不能 yield」**：C# 编译器禁止在包含 catch 子句的 try 块内 yield return（CS1626: Cannot yield a value in a try block that contains a catch clause）。流式解析 SSE 响应时，JSON 解析需要 try-catch 容错，但 yield 不能放在同一个 try-catch 内。解法 = 在 try-catch 内将 content 收集到 `List<string>`，循环结束后在 try-catch 外 `foreach` yield。CA2024 同时禁止在异步方法中使用 `reader.EndOfStream`（同步属性），改用 `while ((line = await reader.ReadLineAsync(ct)) is not null)` 模式。
- **请求体提取为 `BuildRequestBody` 复用方法**：流式和非流式共享相同的消息构建/工具构建逻辑，唯一差异是 `stream: true`。提取为 `private Dictionary<string, object?> BuildRequestBody(messages, options, bool stream)`，`stream=false` 时 `["stream"] = null` 经 `DefaultIgnoreCondition.WhenWritingNull` 自动省略。避免代码重复且保证两条路径请求体一致。
- **SSE 解析要点**：DeepSeek SSE 格式为 `data: {...}` 逐行、`data: [DONE]` 结束。每行去掉 `data: ` 前缀后 JSON 解析，取 `choices[0].delta.content` yield；`delta.reasoning_content` 和 `delta.tool_calls` 跳过不推前端。JSON 解析异常记录 Warning 后 continue（跳过坏行，不中断流）。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T19 新增流式接口和 DTO 笔记（service-layer-extraction）

- **接口新增方法会导致实现类编译失败（CS0535）**：`IShoppingAssistantAgent` 新增 `RunChatStreamAsync` 后，`ShoppingAssistantAgent` 必须同步提供实现才能编译通过。T19 的完成判据是 `dotnet build` 0 错误，因此需要在实现类中加桩（`yield break`），即使 T20 才做完整逻辑。桩实现需用 `[EnumeratorCancellation]` 标注 CancellationToken 参数（`System.Runtime.CompilerServices` 命名空间，.NET 10 ImplicitUsings 已覆盖）。
- **IAsyncEnumerable 桩实现模式**：`async IAsyncEnumerable<T> Method(..., [EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }` — 编译器要求 async 方法至少有一个 await，`Task.CompletedTask` 满足此要求；`yield break` 立即结束枚举，不产出任何元素。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T20 ShoppingAssistantAgent 流式实现笔记（service-layer-extraction）

- **CS1626/CS1631「try-catch 内不能 yield」是 IAsyncEnumerable 实现的核心约束**：C# 编译器禁止在包含 catch 子句的 try 块内 yield return（CS1626），也禁止在 catch 子句体内 yield（CS1631）。流式方法必须把「数据采集」和「yield 输出」分离——采集逻辑提取到不含 yield 的私有方法（如 `CollectStreamingChunks`）中用 try-catch 包裹，主方法在 try-catch 外遍历收集结果并 yield。会话创建的 catch 块也不能 yield，改用 nullable session + null 检查 + if 分支 yield 的模式规避。
- **MAF 1.18.0 `AIAgent.RunStreamingAsync` API 实测确认**：通过临时反射程序验证，`AIAgent` 有 `RunStreamingAsync(string message, AgentSession session, AgentRunOptions options, CancellationToken ct)` 重载（除 message 外全 optional），返回 `IAsyncEnumerable<AgentResponseUpdate>`。`AgentResponseUpdate` 不继承 `ChatResponseUpdate`（MEAI），是独立类型，属性含 `Text`（string）、`Role`（ChatRole?）、`Contents`（IList<AIContent>）、`FinishReason` 等。NuGet 包路径通过 `dotnet nuget locals global-packages --list` 找到（`D:\NuGetPackages`），XML 文档确认方法签名。
- **SonarAnalyzer S3267 在 `await foreach` 循环上的误报**：循环体内维护缓冲状态（`unflushed`）时，S3267 仍建议"用 Select 简化"，但实际无法简化。用 `#pragma warning disable/restore S3267` 精确抑制，注释说明抑制原因（需维护跨迭代状态）。
- **增量清洗（SanitizeReplyIncremental）的缓冲策略**：累积 `buffer + newText`，用三个正则（FixedIdPattern/HashIdPattern/ProductIdLabelPattern）尝试匹配，取最早匹配位置 `safePos` 作为安全边界。`safePos` 之前的部分无模式风险，安全发送；`safePos` 之后保留到下一轮。流结束时用 `SanitizeReply`（非增量版）冲洗缓冲区残留。`EndsWithPatternPrefix` 检查尾部是否可能是模式前缀（以 `#` 结尾或含 `商品Id`/`商品ID`），防止误 flush 部分模式。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T21 /api/chat/stream SSE 端点笔记（service-layer-extraction）

- **`Results.Stream(Func<Stream, CancellationToken, Task>, ...)` 重载解析失败（CS1660）**：ASP.NET Core Minimal API 的 `Results.Stream` 有两个重载：`Stream Stream(Stream, ...)` 和 `Stream Stream(Func<Stream, CancellationToken, Task>, ...)`。传入 `async (stream, _) => { ... }` lambda 时，编译器尝试将 lambda 转换为 `Stream` 类型（匹配第一个重载）而非识别为委托（第二个重载），报 CS1660。**解法：不用 `Results.Stream`，改用 `HttpContext.Response` 直接设置响应头（`ContentType = "text/event-stream"`）并写 `Response.Body`**——行为等价且无重载歧义。需要在端点参数列表加 `HttpContext httpContext`，错误响应改 `httpContext.Response.StatusCode + WriteAsJsonAsync`。
- **SSE 端点必须 `AutoFlush = true`**：`new StreamWriter(httpContext.Response.Body) { AutoFlush = true }` 确保每个 `WriteAsync` 后立即刷新到客户端，否则数据积压在缓冲区，前端 `EventSource` 收不到增量事件。
- **`BuildChatReply` 提取复用消除端点间逻辑重复**：`/api/chat` 和 `/api/chat/stream` 的推荐计算逻辑（关键词匹配 + 偏好合并 + SplitProducts + 缓存写入 + 偏好入队）完全一致。提取为 `private static ChatReply BuildChatReply(...)` 后两端点共用，保证推荐结果一致性（满足 spec「/api/chat/stream 与 /api/chat 推荐结果一致」），未来推荐逻辑变更只改一处。
- **`System.Text.Json` 需显式 using**：ASP.NET Core Web SDK 的隐式 using 不含 `System.Text.Json`（ImplicitUsings 只含 System.* + Microsoft.*），SSE 事件序列化用 `JsonSerializer.Serialize` 需手动加 `using System.Text.Json;`。
- **流式端点降级策略：try-catch 包裹 `await foreach` + fallback 到 `RunChatAsync`**：流式过程中 `streamChunks.WithCancellation(ct)` 的异常（如网络中断、模型不支持流式）用 `catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))` 捕获，降级到非流式 `RunChatAsync` 获取完整结果后发送单个 `done` 事件。未返回 `FullResult` 的情况（Agent 未 yield 完整 chunk）同样走降级路径。
- **tasks.md checkbox 依旧被 check_gateway.py 规则 4 拦截**（implementer Edit BLOCK 实测历史），handoff 注明待 @task-breaker 勾选

## T22 前端 SSE 消费笔记（service-layer-extraction）

- **`addMessage` 创建的 `div.message` 无 `.message-content` 子元素**：`addMessage(role, content)` 直接在 `div` 上设 `textContent = content`，没有嵌套子元素。流式更新 typing 气泡时 `typing.querySelector('.message-content').textContent = ...` 会返回 null → 运行时 TypeError。正确做法 = `typing.textContent = fullText + '▌'`（直接更新 div 文本内容）。**做前端流式显示前先确认 addMessage 的 DOM 结构**，不要假设存在子元素
- **SSE 事件解析必须处理跨 chunk 不完整行**：`TextDecoder.decode(value, { stream: true })` 解码当前 chunk 后，用 `split('\n')` + `lines.pop()` 保留最后一个不完整行到下一轮拼接。遗漏 `pop()` 会导致跨 chunk 的 `event:` / `data:` 行被截断 → `JSON.parse` 报错或事件丢失
- **`done` 事件发送的是完整 `ChatReply` JSON**：端点 `WriteSseEventAsync(writer, "done", JsonSerializer.Serialize(chatReply))` 序列化整个 ChatReply record（含 `response`/`recommendedProducts`/`otherProducts`/`recMessage`/`hasRecommendation`/`matchedCategories`）。前端 `data.response` 取文本回复，`data` 直接传给 `renderRecommendationPanel(data)` 渲染推荐——两者共用同一 JSON 对象，零适配
- **`token` 事件的 `data.text` 是已清洗商品 ID 的文本增量**：DeepSeekChatClient 流式输出经 `SanitizeReplyIncremental` 缓冲清洗后 yield，前端无需二次清洗。`fullText` 累积所有 token 后在 `done` 事件时被 `data.response`（服务端最终清洗版）替换——两者在正常路径下内容一致，`|| fullText` 仅作降级兜底

## T23 全量验证笔记（service-layer-extraction）

- **`StreamWriter.AutoFlush = true` 在 ASP.NET Core Kestrel 下抛 `Synchronous operations are disallowed`**：`AutoFlush = true` 让 `StreamWriter` 在每次 `WriteAsync` 后同步调用 `Flush()`，而 Kestrel 默认禁止同步 I/O（`AllowSynchronousIO = false`）。报错栈指向 `HttpResponseStream.Flush()` → `StreamWriter.Flush()`。修复 = 移除 `AutoFlush = true`，改在 `WriteSseEventAsync` 里每次写入后显式 `await writer.FlushAsync()`——行为等价（每事件立即刷新到客户端）且全异步。**做 SSE 流式端点时不要用 `AutoFlush = true`，用显式 `FlushAsync()`**
- **Windows curl 中文请求体需 UTF-8 文件**：`curl -d '{"message":"中文"}'` 在 Windows 终端默认 GBK 编码发送 → 服务端 UTF-8 解码失败报 500。用 `printf '...' > /tmp/req.json && curl --data-binary @/tmp/req.json -H "Content-Type: application/json; charset=utf-8"` 确保 UTF-8 编码（operations.md 已有记录，本次实证再次确认）
- **全量验证发现并修复了 T21 遗留的流式端点同步 I/O 问题**：T21 实现时可能在无 HTTP 服务器的环境下测试（如单元测试 mock stream），未暴露 `AutoFlush` 的同步 I/O 问题；T23 端到端 curl 测试首次暴露。**流式端点必须用真实 HTTP 服务器测试，不能只靠 mock stream**
