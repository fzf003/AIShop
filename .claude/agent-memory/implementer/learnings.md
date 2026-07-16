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

## 项目结构笔记

<!-- 发现的模块间依赖关系 -->

## 测试习惯

<!-- 测试框架偏好、mock 策略等 -->

- 集成测试中使用临时 SQLite 数据库时，每个测试必须使用独立的 WebApplicationFactory + 数据库文件，否则并行执行会导致 EF Core DbUpdateConcurrencyException（"expected 1 row(s) but affected 0"）
- 使用 [CollectionDefinition(DisableParallelization = true)] 和 [Collection] 确保串行执行，避免 SQLite 文件写入冲突
- 两次连续 POST 到同一个用户在独立 Factory + DB 场景下仍可能因 EF Core 的状态追踪机制产生并发问题，建议同一用户的操作在同一请求中验证
