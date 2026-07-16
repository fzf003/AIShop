---
name: development-flow
description: 开发流程规范 — 实现前确认、小步提交、及时更新状态、测试资源清理
---

# 开发流程规范

## 实现前确认

- 实现前端 UI 前，先 Read design.md / handoff 确认交互细节（按键样式、位置、状态变化），不猜
- Agent 指令注入前先想清楚是否必要。少量上下文提示优于全量数据注入
- 如果不确定某个设计细节，先问，不要猜

## 小步提交

- 每完成一个独立功能点（一个 Task）就 git commit，前缀 feat/fix/chore
- 不把多个不相关的改动攒在一个 commit 里
- 提交前确保 dotnet build 0 错误 + 涉及功能自测通过

## 状态同步

- tasks.md 的实现步骤 checkbox 标记为 [x]
- test-report.md 的验收标准同步更新
- 产出 handoff 文件

## 测试资源清理

- xUnit 集成测试中 WebApplicationFactory 必须释放（实现 IDisposable / IAsyncLifetime）
- 测试后清理：临时数据库文件应删除，dotnet 进程不应残留
- 运行 dotnet test 后检查 tasklist / dotnet 进程是否已退出

## Agent 指令设计原则

- Agent 指令应保持精简：只给推理规则，不给原始数据
- 如果 Agent 需要查询数据（商品目录、用户列表等），定义为工具（Tool）让 Agent 按需调用，而非注入到 system prompt
- system prompt 过长（>4K tokens）时应检查是否可以精简或拆分为工具调用
