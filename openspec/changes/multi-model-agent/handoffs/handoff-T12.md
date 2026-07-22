# Handoff: T12 — 测试：ModelRouter.GetAvailableModels 不含敏感字段

## 完成内容

在 `tests/AIShop.Api.Tests/ModelRouterTests.cs` 中新增测试方法 `GetAvailableModels_DoesNotExposeSensitiveFields`：

1. 配置 3 个模型（qwen, gpt-4.1, deepseek），`ActiveModel` 设为 `"qwen"`
2. 验证 `GetAvailableModels()` 返回列表长度为 3
3. 使用 `System.Text.Json.JsonSerializer` 序列化每个 `ModelInfo` 对象
4. 反序列化为 `Dictionary<string, JsonElement>` 后验证仅包含 `id`、`name`、`isDefault` 三个键（camelCase 策略）
5. 验证 `isDefault: true` 的模型与 `ActiveModel`（qwen）一致

同时修复了 `ChatEndpointsTests.cs` 中一个残缺的测试方法（`GetModels_ReturnsAvailableModels` 缺少方法签名），确保编译通过。

## 测试结果

- 所有 5 个 ModelRouterTests 测试通过
- 全部 55 个 AIShop.Api.Tests 测试通过
- dotnet build 0 错误 0 警告

## 涉及文件

- `tests/AIShop.Api.Tests/ModelRouterTests.cs` — 新增 `GetAvailableModels_DoesNotExposeSensitiveFields`
- `tests/AIShop.Api.Tests/ChatEndpointsTests.cs` — 修复残缺的方法签名

## 待办

- [ ] 在 tasks.md 中将 T12 标记为 [x]（需要 @task-breaker 执行）
- [ ] git commit + push
