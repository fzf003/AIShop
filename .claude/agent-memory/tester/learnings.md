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
