# Handoff: T15

## 任务
T15 - 测试：POST /api/chat 带 model 参数时路由到对应 Agent

## 改动文件
- `tests/AIShop.Api.Tests/ChatEndpointsTests.cs` — 新增 `Chat_WithModelParameter_RoutesToCorrectAgent` 测试方法

## 测试方法说明

```csharp
Chat_WithModelParameter_RoutesToCorrectAgent()
```

- mock ModelRouter，注册 "qwen" 和 "gpt-4.1" 两个模型
- 发送 `POST /api/chat` 请求体包含 `{ username: "marla", message: "推荐跑鞋", model: "gpt-4.1" }`
- 验证 `ModelRouter.GetAgent("gpt-4.1")` 被调用，且返回 Agent 生成的回复为 "GPT-4.1 推荐跑鞋"
- 使用 `usedModel` 变量捕获 `GetAgent` 实际接收的 model 参数，断言为 `"gpt-4.1"`

## 测试模式

沿用 `Chat_WithoutModel_UsesDefaultModel` 的 mock 模式：
- `_factory.WithWebHostBuilder` 创建独立 factory
- NSubstitute mock `ModelRouter`，`GetAgent()` 捕获参数并返回 mockAgent
- 通过 `usedModel` 变量验证路由目标

## 验证

- `dotnet build`: 0 错误
- `dotnet test`: 57/57 通过（含新增测试）
- Commit: `a6d77a8`

## 后续依赖

- T15 无后续依赖
- tasks.md 中 T15 已标记为 [x]
