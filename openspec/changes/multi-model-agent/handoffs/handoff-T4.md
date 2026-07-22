# handoff-T4

## 完成内容

- `src/AIShop.Api/Agents/ModelRouter.cs` 补充以下内容：
  - 新增 using 指令：`System.Collections.Concurrent`、`System.ClientModel`、`System.ClientModel.Primitives`、`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.AI`、`OpenAI`、`AIShop.Core.Interfaces`、`AIShop.Infrastructure.Data`、`Microsoft.EntityFrameworkCore`
  - 新增私有字段 `_sp`（`IServiceProvider`）和 `_agents`（`ConcurrentDictionary<string, Lazy<ShoppingAssistantAgent>>`）
  - 构造函数增加 `IServiceProvider sp` 参数，在构造方法体末尾赋值 `_sp = sp`
  - 新增 `CreateChatClient(ModelConfig cfg)` 私有方法：根据每个模型的 Endpoint/Key/Model 创建独立的 `IChatClient` 实例（含无代理 HttpClient + DebugHandler + OpenAIClientOptions 管道）
  - 新增 `GetAgent(string modelName)` 方法：
    - 验证 modelName 存在，不存在则抛出 `KeyNotFoundException`
    - `_agents.GetOrAdd(modelName, key => new Lazy<...>(() => { ... })).Value` 延迟创建 + 线程安全复用
    - 首次创建时通过 `IServiceProvider` 解析 `IDbContextFactory<AppDbContext>`、`IProductCatalogService`、`CartToolProvider`
  - 新增 `GetDefaultAgent()` 委托到 `GetAgent(_activeModel)`

## 注意点

- 遵守 SonarAnalyzer S6612：`GetOrAdd` 的 lambda 使用 `key =>` 参数而非捕获 `modelName` 变量
- 每个 Agent 实例拥有独立的 `IChatClient` 和 `HttpClient`，避免并发竞争

## 验证

- `dotnet build src/AIShop.Api` 通过，0 警告 0 错误

## 下一步依赖

- T5：Program.cs DI 注册变更（依赖 T3、T4）
