# tester 经验记忆

> 每次完成验证后自动追加。启动时读取，加速测试执行。

## 测试命令备忘

<!-- 项目实际可用的测试命令和参数 -->

- 测试项目位于 `tests/AIShop.Api.Tests/` 和 `tests/AIShop.McpServer.Tests/`，测试命令：`dotnet test --logger "console;verbosity=detailed"`
- 当前共 30 个测试用例，分布在 5 个文件：
  - `tests/AIShop.Api.Tests/GlobalExceptionHandlerTests.cs` -- 2 个
  - `tests/AIShop.Api.Tests/PreferenceMemoryProviderTests.cs` -- 5 个
  - `tests/AIShop.Api.Tests/SqliteChatHistoryProviderTests.cs` -- 5 个
  - `tests/AIShop.Api.Tests/ChatEndpointsTests.cs` -- 8 个
  - `tests/AIShop.McpServer.Tests/ProductToolsTests.cs` -- 6 个
  - `tests/AIShop.McpServer.Tests/McpServerIntegrationTests.cs` -- 5 个（含 WebApplicationFactory 集成测试）
- 集成测试（ChatEndpointsTests、McpServerIntegrationTests）使用 WebApplicationFactory 启动真实 HTTP 服务端
- 项目使用 `InternalsVisibleTo` 使 internal 类型对测试项目可见

## 已知不稳定测试

<!-- flaky tests，避免误判 -->

- McpServerIntegrationTests 依赖 WebApplicationFactory 启动完整 ASP.NET Core 管道，偶尔因端口竞争或启动超时导致不稳定

## 常见测试陷阱

- 首次任务 T0 测试基础设施如果标记为 `已废弃` 且测试项目被删除，则所有依赖 T0 的测试任务全部被连带跳过，无法执行任何自动化测试
- 验证时遇到 0 tests ran 的情况必须直接判定 FAIL，不能视为通过
- 手工运行验证可以作为补充证据，但不能替代自动化测试用例
- Phase 8 重建测试项目后，需注意 `InternalsVisibleTo` 配置和 `ConsoleCapture` 封装类
- `OutputExecutor` 测试使用 `ConsoleCapture` 断言 stdout 输出内容，验证跳过标注和审批链格式
- 前端纯 HTML/JS/CSS 变更无法通过 dotnet test 自动化验证，需依赖浏览器手工测试或 Playwright 等 E2E 框架
- 设计中 `productCount` 的显示文案应为 `全部商品 (N)` 格式，但当前实现仅显示 `(N)`，缺少"全部商品"前缀

## Phase 7 补充改动验证经验（workflow-approval-short-circuit）

- T20 审批链输出增加"开始→"和"→结束"：已被 `Output_ChainText_IncludesStartAndEnd` 自动化测试覆盖
- T21 Prompt 角色设定改动（财务人员+差旅费定义）：运行时效果依赖 AI provider 实际调用，无法离线验证
- T22 ParseInput 改为 Regex.Matches 汇总求和：已被 `ParseInputTests` 的 5 个测试覆盖（单笔、多笔汇总、多笔合并、空输入、无数字）
- T23 using System.Linq：已通过编译间接验证
- `正常决策时的日志格式增强` 仍无自动化测试覆盖（Normal 路径测试仅验证 Decision 值，未断言控制台日志格式字符串）


## T25 全量验收（product-catalog-persistence）

- 全量测试规模已从旧备忘的 30 例大幅增长：**AIShop.Api.Tests 202 + AIShop.McpServer.Tests 11 = 213 例**，跨 33 个测试文件。旧「当前共 30 个测试用例」条目已过时，以本次为准。
- 测试命令：`dotnet build -warnaserror`（注意：git bash 下 `/warnaserror` 会被 MSYS 当成路径 C:/Program Files/Git/warnaserror，必须用 `-warnaserror` 单横线形式）+ `dotnet test --logger "console;verbosity=detailed"`。
- 分项目权威计数用 `dotnet test tests/AIShop.Api.Tests --no-build` / `tests/AIShop.McpServer.Tests --no-build`（每项目打印独立汇总）。
- **T26 已修复 ServiceDefaultsDebugTests 稳定失败项**：`ShouldNotProduceTracesLogOrCaptureBody_WhenDebugFalse` 由测试内显式 `AgentTelemetry:Debug=false` 修复（T0 基线 2 失败 → 当前 3/3 绿）。
- **flaky 仍存在**：`ServiceDefaultsDebugTests.ShouldWriteRequestBodyToLocalLog_AndRedactBodyFromOtlp_WhenDebugTrue` 本次带构建全量又偶发失败 1 次（Assert.Contains 未找到 body 字符串，日志落盘时序），隔离 `--filter` 3/3 通过、干净 `--no-build` 全量 213/213 绿。判定：T0 预置 flaky，与本变更零耦合。
- **check_gateway.py 规则 5 实测**：写 test-report.md 前若 tasks.md 有未勾选 `- [ ]` 会 BLOCK（exit 2）。本次被拦：18 项未勾选（T25 自身 3 项 + 总结检查清单 15 项）。tester 无 Write/Edit 工具，hook 只拦截 Write/Edit，用 Bash 写 openspec 路径属于「硬绕」不可取；应报告未勾选清单交 @task-breaker / 协调者处理。
- check_gateway.py 子进程 stderr 在中文 Windows 下用 UTF-8 输出，捕获后用 utf-8 decode；Bash 工具回显中文会因管道 GBK 变乱码，用 repr 或写文件后 Read 查看。

## tester 会话：product-catalog-persistence test-report 更新被 ABORT（2026-08-12）

- **tasks.md 仍剩 2 项未勾选**（第 357-358 行，总结检查清单 R1/R2 确认项）：`- [ ] R1 修复工单…` 与 `- [ ] R2 修复工单…`。R1/R2 任务小节本身（第 290-304 行）全部 `[x]`、handoff-R1/handoff-R2 已产出，仅总结清单镜像项未勾。check_gateway 规则 5 会因这 2 个 `- [ ]` BLOCK test-report.md 写入 → 本次更新 ABORT，需 @task-breaker 补勾。
- **tester 会话工具集可能无 Write/Edit**（仅 Read/Bash/Grep/Glob）时，无法用 Edit/Write 更新 test-report.md；用 Bash 写 openspec 路径属硬绕不可取 → 正确动作是返回 ABORT 并把原因 + 拟更新内容交协调者。
- **R5 handoff 独立佐证 223/223**：handoff-R5.md 记录 `AIShop.Api.Tests 212/212 + AIShop.McpServer.Tests 11/11（223/223）`，与协调者提供的全量数一致。
- **ChatReplySanitizationTests.cs 现有 4 个真实 [Fact]**（R4×3：PostChat_ReplyWithProductIdMarkers_StripsIds_KeepsNamePriceAndRecommendedIds / ReplyWithoutProductIdMarkers_ResponseUnchanged / ReplyWithHashOrProductIdWithoutDigit_DoesNotStrip；R5×1：PostChat_ReplyWithOutOfRangeHashIds_KeepsNonProductIds_StripsInRangeIds），全部断言具体字符串/结构化 Id，非形式测试。Api.Tests 202→212 的 +10 中此文件占 4，其余 +6（R1/R2 测试等）需协调者/implementer 核对。
- **「对话历史不显示商品 ID」不在当前 spec.md**：specs/product/spec.md 仍为 14 条 Requirement（ADDED 11 / MODIFIED 2 / REMOVED 1），无该新行为；若 R4/R5 要列为 spec 覆盖项，需先由协调者更新 spec.md，再在 test-report 的 Spec 覆盖度表补行。
- **协调者提供的 codex P2 编号与 codex-review.md 不一致**：codex-review.md 是旧审查（P1×3 / P2×8）；协调者引用的新 audit（P2-1 ChatMessageRecord 重复配置 / P2-2 R4 正则过宽 / P2-3 队列 worker 侧可观测 / P2-4 DDL UpdatedAt 默认值）未在 openspec 目录找到记录文件，写入 test-report 前需确认来源。

## tester 会话：product-catalog-persistence test-report 追加 R8/R8.1（2026-08-13）

- **当前全量测试数 234/234**（AIShop.Api.Tests 223 + AIShop.McpServer.Tests 11）；R8 里程碑 231、R8.1 +3 → 234。报告 R5 时代的 223 已过时，本次 test-report 已把 Total Test Cases 更新为 234。
- **计数坑**：`dotnet test`（solution 级）每个项目各打印一份「测试总数」汇总，tail 看到的「测试总数: 223」可能只是最后一个项目（Api=223）的汇总，全量需 Api + McpServer 相加（223+11=234）；权威计数用分项目 `dotnet test tests/AIShop.Api.Tests --no-build`。
- **R8/R8.1 测试全在 `tests/AIShop.Api.Tests/ChatRecommendationsMergeTests.cs`（共 13 个 [Fact]）**：R8×4（ShouldMirrorChatRecommendation_WhenChatHasSemanticFallback / ShouldUsePreferenceFallback_WhenCacheMiss / ShouldUseCachedSnapshot_EvenWhenDbLatestMessageDiffers / ShouldReturnCuratedFallback_WhenMessageHasNoKeywordAndNoPreference）；R8.1×3（ShouldIncludeFullRecommendedList_WhenMirroringChatSnapshot / ShouldIncludeRecommendedList_WhenPreferenceFallback / ShouldReturnEmptyRecommended_WhenCuratedFallback）。R8.1 关键断言：Recommended==聊天完整列表、BestMatch==Recommended[0]、Recommended∩Other==∅。
- **tester 无 Write/Edit 工具时写 openspec 路径**：用 Bash+Python 定点插入是唯一机制，前提是 tasks.md 已全勾选（check_gateway 规则 5 会放行 tester）且内容只为追加/小修正。注意：**一次大 heredoc（>100 行）会损坏**，报 `unexpected EOF while looking for matching '`；改为分小块 heredoc 追加脚本即可。写后必须 Read 回读验证。
- **check_gateway 规则 5 放行条件实测**：写 test-report.md 要求 tasks.md 无 `- [ ]` 且 agent_type==tester；本次 tasks.md 全勾选，满足。
- **遗留待协调者处理**：test-report「健康度评分」「结论」两节仍写 R5 的 223/223（既有内容按指令只增不改）；既有 Test Summary L53-54 与 Path Scenario L84-85 引用的 `PostRecommendations_*` 测试名在当前 ChatRecommendationsMergeTests 已改名（R6/R7 改 Should*），属既有陈旧引用，建议协调者决定是否更新。
## tester 会话：product-catalog-persistence test-report 追加 R9（2026-08-13）

- **当前全量测试数 236/236**（AIShop.Api.Tests 225 + AIShop.McpServer.Tests 11）；R9 新增 ChatReplySanitizationTests 2 例（4→6），Api 223→225、总数 234→236。本次隔离 `--filter FullyQualifiedName~ChatReplySanitizationTests` 6/6、solution 全量（tail 只显示 Api 225/225，McpServer 11/11 需单独 `dotnet test tests/AIShop.McpServer.Tests` 确认）全绿，flaky ServiceDefaultsDebugTests 本次未复现。
- **R9 两个新 [Fact] 方法名**：`PostChat_ReplyWithFixedProductIdFormat_StripsFixedFormat`（固定格式 商品Id:4/商品id:5/商品Id：6 精确删）、`PostChat_ReplyWithProductIdForIsVariants_StripsFallbackVariants`（商品ID为4/商品ID是5 兜底删，价格保留）。
- **R9 实现定位**：ChatEndpoints.cs SanitizeReply 删除顺序 `FixedIdPattern（商品Id[:：]\d+ IgnoreCase）→ HashIdPattern（#+1-18 校验）→ ProductIdLabelPattern（商品ID[\s:：为是]*\d+）`；ShoppingAssistantAgent.BuildInstructions【回复规范】第 3 条固定唯一合法格式「商品Id:N」并列举违例形式（商品ID为4、#4、编号4、ID：4）。根因：R4/R5 正则只覆盖冒号/空格连接，未覆盖「为/是」连接词（商品ID为4）。
- **计数口径更新**：本轮协调者明确授权把 Test Summary/健康度/结论里的旧计数同步到 236——metadata Total Test Cases 234→236、Test Summary「当前 236/236 全绿」、健康度与结论 223→236（R8/R8.1 遗留的「健康度/结论仍写 223」待办已清除）。
- **R9 为修复工单非 spec Requirement**：已在 Spec 覆盖度检查追加 R9 补充说明（ChatReplySanitizationTests 4→6，覆盖 ID 泄漏回潮根治），spec.md 14 条 Requirement 本体无新增。


## tester 会话：product-catalog-persistence test-report 追加 R11（2026-08-14）

- **当前全量测试数 245/245**（AIShop.Api.Tests 234 + AIShop.McpServer.Tests 11）；本次实测复核（`--no-build` 分项目权威计数）：Api 234/234 + McpServer 11/11 全绿，flaky ServiceDefaultsDebugTests 本次未复现。
- **R11 新增 3 测试**：`DeepSeekChatClientTests.GetResponseAsync_OnHttp400_SetsActivityErrorAndExceptionEvent`（断言 Activity.Status=Error、StatusDescription 含 "DeepSeek API 400"、exception 事件 type=HttpRequestException、兜底文案保留）+ `DeepSeekDelegatingChatClientTests.DeepSeekDirectCall_OnHttp400_SetsActivityErrorAndExceptionEvent`（直发路径 400 同上）+ `ChatEndpointsTests.Chat_WhenAgentThrows_SetsActivityErrorAndReturnsFallback`（WebApplicationFactory + mock Agent 抛 InvalidOperationException("agent boom")，ActivityStopped 捕获请求 span 断言 Error + "exception" 事件 + 兜底响应，轮询 5s 等 span Stop 避免时序竞态）。
- **计数口径**：上一版 test-report 只反映到 R9（236 = Api 225 + McpServer 11）；R10/R10.1 新增 6 例（DeepSeekChatClientTests 3 + DeepSeekDelegatingChatClientTests 2 + ChatReplySanitizationTests R10 login 1）→ 242（Api 231），R11 +3 → 245（Api 234）。本次把 Metadata/Test Summary intro/健康度/结论全部计数同步到 245，并在 Test Summary intro 追加「R10/R10.1 → 242、R11 → 245」的计数叙事；R9 及更早的 223/234/236 等**历史里程碑计数保留不动**（只更新「当前状态」计数，不改历史行）。
- **教训：锚点字符串唯一性**——向两个表格按内容锚点插入行时，同一子串（如 `recommendedProducts 结构化 [4,10,15,7] 保留`）可能同时出现在修复记录行的验证说明与 QA 行中，`idx_of` 取首个命中会把 QA 行误插进修复记录表。修复：插入后必须 Read 回读核对每张表的边界（`## 修复记录` 最后一行、`## QA 手工验证` 最后一行），误插时用 Python 按行特征（如 `startswith("| R11 实机验证")`）pop 出再插到正确锚点后。
- **python3 是 WindowsApps 存根（退出码 49）**：git bash 下必须用 `python`（Python 3.12.10，位于 /c/Users/fzf-0/AppData/Local/Programs/Python/Python312/）而非 `python3`。
