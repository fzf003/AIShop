# implementer 经验记忆

> 每次完成实现后自动追加。启动时读取，指导本次实现。

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
- **双表历史**：AppDbContext 有 `ChatMessage`（DbSet→表 `ChatMessages`，废弃）与 `ChatMessageRecord`（DbSet→ToTable "chat_messages"，在用）；ChatMessageRecord 的 OnModelCreating 配置在文件里重复了两遍（L43-59 与 L78-98），清理时按「不重构本次范围外」保留不动。
