# handoff-T3

## 完成内容

- 新建 `src/AIShop.Api/Agents/ModelRouter.cs`
  - `public record ModelInfo(string Id, string Name, bool IsDefault)` — 对外暴露的模型信息（不含 Key/Endpoint）
  - 内部 `ModelConfig` record（Endpoint, Key, Model, Name）
  - 构造函数从 `IConfiguration` 读取 `"Models"` 节解析多模型配置
  - 向后兼容：若 `"Models"` 节不存在但 `"OpenAI"` 节存在，自动包装为单模型字典（id="legacy"）
  - 若两者都不存在，抛出 `InvalidOperationException` 说明配置格式
  - `GetAvailableModels()` 返回 `IEnumerable<ModelInfo>`（不含敏感字段）
  - `IsOpenAIModel()` 委托到 `ShoppingAssistantAgent.IsOpenAIModel`

## 验证

- `dotnet build src/AIShop.Api` 通过，0 警告 0 错误

## 下一步依赖

- T4：ModelRouter Lazy 路由（依赖 T2、T3）
- T5：Program.cs DI 注册变更（依赖 T3、T4）
