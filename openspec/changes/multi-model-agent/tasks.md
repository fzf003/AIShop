# 任务清单: Multi-Model Agent 支持

## 阶段 0：前提条件检查与基础设施

### T0 (预计 5min) 确认 .NET SDK 版本和测试项目存在
- [x] (预计 2min) 检查当前 .NET SDK 版本是否为 10.0+（`dotnet --version`）
- [x] (预计 3min) 检查 `tests/AIShop.Api.Tests/` 是否存在且可构建（`dotnet build tests/AIShop.Api.Tests/`）
  - 如果不存在，先搭建测试项目再继续（参照 tasks.md 模板）

### T1 (预计 5min) 验证 Multi-Model 前端原型存在
- [x] (预计 5min) 确认 `src/AIShop.Api/wwwroot/prototype-multi-model.html` 存在，且包含正确的模型选择 UI（复选框、下拉等）

---

## 阶段 1：ChatEndpoints 改造

### [x] T2 (预计 10min) ChatEndpoints：请求体新增 ModelName 字段
- [x] (预计 5min) 在 `ChatRequest` 记录中新增 `string? ModelName` 字段（可选，向后兼容）
- [x] (预计 5min) 测试：验证旧请求（无 ModelName）能被正确反序列化，ModelName 为 null（对应 spec.md 第 2 条）

---

## 阶段 2：ModelRouter 核心

### [x] T3 (预计 10min) ModelRouter：ModelInfo 记录 + appsettings 配置读取 + 向后兼容
- [x] 新建 `src/AIShop.Api/Agents/ModelRouter.cs`：
  - [x] 定义 `public record ModelInfo(string Id, string Name, bool IsDefault)`
  - [x] 定义内部 `ModelConfig` 记录（Endpoint, Key, Model, Name）
  - [x] 构造时读取 `IConfiguration` 的 `"Models"` 节：
    - [x] 从 `appsettings.json` 的 `"Models"` 数组读取每个模型的配置
    - [x] 映射为 `ModelInfo` 列表（包含默认模型标记）
    - [x] 无配置时返回空列表（向后兼容）
- [x] (预计 5min) 测试：验证 appsettings 中配置的 3 个模型（gpt-4o, gpt-4o-mini, o3-mini）能被正确读取（对应 spec.md 第 3 条）
- [x] (预计 2min) 测试：验证 `"Models"` 节不存在时返回空列表（对应 spec.md 向后兼容要求）
- [x] (预计 2min) 测试：验证 IsDefault 标记正确设置（对应 spec.md 第 3 条）

---

## 阶段 3：ShoppingAssistantAgent 改造

### [x] T4 (预计 10min) ShoppingAssistantAgent：接收 modelName 参数 + 动态创建 ChatClient
- [x] (预计 3min) 修改 `ShoppingAssistantAgent` 类，添加 `modelName` 参数到入口方法（如 `ChatAsync` 或 `ExecuteAsync`）
- [x] (预计 3min) 根据 `modelName` 从 `ModelRouter` 获取对应的 `ModelConfig`
- [x] (预计 4min) 使用动态的 Endpoint/Key/Model 创建 `ChatClient` 实例，替代硬编码的构造函数
- [x] (预计 3min) 测试：验证传入 `"gpt-4o-mini"` 创建对应配置的 ChatClient（对应 spec.md 第 4 条）
- [x] (预计 2min) 测试：验证传入 null/空字符串使用默认模型（对应 spec.md 第 4 条）
- [x] (预计 2min) 测试：验证传入不存在的模型名抛出合理异常（对应 spec.md 第 4 条）

### [x] T5 (预计 5min) Program.cs：注册 ModelRouter + 传递 ModelName
- [x] (预计 3min) 在 DI 容器中注册 `ModelRouter`（Singleton）
- [x] (预计 2min) 修改 Chat Endpoint 调用链，从 `ChatRequest` 提取 `ModelName` 传递给 Agent

---

## 阶段 4：前端集成

### [x] T6 (预计 10min) 前端：模型选择器交互 + 发送模型信息到后端
- [x] (预计 5min) 在 prototype-multi-model.html 实现模型选择器 UI（下拉列表或单选按钮）
- [x] (预计 5min) 修改前端 AJAX 请求，在请求体中包含 `modelName` 字段
- [x] (预计 3min) 测试：验证前端选择不同模型后发送的请求体包含正确的 modelName（对应 spec.md 第 6 条）
- [x] (预计 2min) 测试：验证默认选中"默认模型"时请求体可以不传 modelName（向后兼容）（对应 spec.md 第 6 条）

### [x] T7 (预计 5min) 前端：响应展示当前使用的模型信息
- [x] (预计 3min) 在后端响应中添加 `modelUsed` 字段（当前实际使用的模型信息）
- [x] (预计 3min) 在前端展示当前使用的模型名称（如气泡/标签样式）
- [x] (预计 2min) 测试：验证后端返回的 `modelUsed` 字段与请求的 modelName 一致（对应 spec.md 第 7 条）
- [x] (预计 2min) 测试：验证 modelName 为 null 时 `modelUsed` 返回默认模型名（对应 spec.md 第 7 条）

---

## 阶段 5：集成测试与验证

### [x] T8 (预计 10min) 端到端集成测试
- [x] (预计 5min) 编写集成测试：使用 WebApplicationFactory 启动应用，发送请求验证完整链路
- [x] (预计 5min) 验证不同模型参数下的 Agent 响应差异（烟雾测试）
- [x] (预计 3min) 验证向后兼容性：不传 modelName 的旧请求依然正常响应（对应 spec.md 第 9 条）

---

## 阶段 6：Header 模型切换 UI 完善

### [x] T9 (预计 5min) Header 模型徽章 + hover 下拉切换
- [x] (预计 2min) Header 区域标题旁显示当前模型名徽章
- [x] (预计 2min) hover 弹出可用模型列表，当前模型打勾
- [x] (预计 1min) 点击其他模型切换 selectedModel，不重置聊天界面
- [x] (预计 2min) 后续 POST /api/chat 请求 body 附带 model: selectedModel
- [x] (预计 1min) 徽章更新为新的模型名
- [x] (预计 1min) 从 GET /api/models 获取或缓存 JS 变量

## 总结检查清单

- [x] 所有 spec.md 中的功能点都有对应的实现任务和测试任务
- [x] 每项任务预计耗时不超过 10 分钟
- [x] 每个实现类任务都有配对的测试任务，测试任务标注了对应的 spec.md 条目
- [x] 向后兼容性已覆盖（默认可选、旧请求不中断）
- [x] 测试框架已存在且可运行

### [x] T10 (预计 5min) 更新 ChatEndpointsTests 适配 ModelRouter 注册
- [x] (预计 5min) 构造方法由 mock IShoppingAssistantAgent 改为 mock ModelRouter

---

## 阶段 7：修复 — T3 缺失的多 Agent 实例化

### [x] T11 (预计 10min) ModelRouter：IServiceProvider + 每个模型独立 Agent 实例化

**背景**：T3 实现时简化了 `ModelRouter.GetAgent()`——验证模型名后始终返回同一个 `_defaultAgent`，没有为每个模型创建独立的 `IChatClient` 和 `ShoppingAssistantAgent`。这导致前端能切换模型名，但后端始终走同一个 Agent。

- [x] `ModelRouter` 构造函数依赖从 `(IConfiguration, IShoppingAssistantAgent)` 改为 `(IConfiguration, IServiceProvider)`
- [x] `GetAgent()` 用 `ConcurrentDictionary<string, Lazy<ShoppingAssistantAgent>>` 管理多 Agent
- [x] 每个模型首次访问时 Lazy 创建：`CreateChatClient(cfg)` → `new ShoppingAssistantAgent(...)`
- [x] `GetDefaultAgent()` 调用 `GetAgent(ActiveModel)`，不再返回固定实例
- [x] `Program.cs` 移除 `IShoppingAssistantAgent` 单例注册，`ModelRouter` 注册保留
- [x] `ModelRouterTests` 构造参数适配：`mockAgent` → `Substitute.For<IServiceProvider>()`
- [x] `appsettings.json` 从旧版 `"OpenAI"` 单节升级为 `"Models"` 多节格式
- [x] `.env_sample` 更新为多模型 Key 配置格式

**验证**：dotnet build 0 错误 + dotnet test 52/52 通过 ✅（对应 commit `f6b2293` `b58570d` `9786eb6`）

---

## 阶段 8：测试补充

### [x] T12 (预计 5min) 测试：ModelRouter.GetAvailableModels 不含敏感字段
- [x] (预计 2min) 配置 3 个模型（qwen, gpt-4.1, deepseek），其中 "qwen" 标记为 ActiveModel
- [x] (预计 1min) 验证返回列表长度为 3
- [x] (预计 1min) 验证每个元素仅包含 Id、Name、IsDefault 三个属性（使用 JsonSerializer 序列化检查）
- [x] (预计 1min) 验证 isDefault: true 的模型与 ActiveModel 一致

### [x] T13 (预计 5min) 测试：GET /api/models 端点返回正确信息
- [x] 在 `ChatEndpointsTests` 中新增 `GetModels_ReturnsModelInfoWithCorrectFields` 测试
- [x] 使用纯 mock 方式（NSubstitute mock ModelRouter）
- [x] 发送 `GET /api/models`
- [x] 验证响应 200，且正文可反序列化为 `List<ModelInfo>`
- [x] 验证每个元素有 `id`/`name`/`isDefault` 字段
- [x] 验证 JSON 不包含 `Key`/`Endpoint` 等敏感字段
- [x] 验证 `isDefault: true` 的模型与 ActiveModel 配置一致

---

## 阶段 8：POST /api/login 响应验证

### [x] T17 (预计 5min) 测试：POST /api/login 响应包含 models 字段
- [x] 发送 POST /api/login { username: "marla" }
- [x] 验证响应包含 models 数组
- [x] 验证 models 内容与 GET /api/models 返回一致（数量、id、name）

**验证**：dotnet build 0 错误 + dotnet test 55/55 通过 ✅（对应 commit `c110b77`）

---

### [x] T14 (预计 5min) 测试：POST /api/chat 省略 model 时使用默认模型路由
- [x] mock ModelRouter，设置默认模型 ActiveModel="qwen"
- [x] 发送 POST /api/chat 仅含 { username, message }（无 model）
- [x] 验证 ModelRouter.GetAgent("qwen") 被调用，即路由到默认模型 Agent
- [x] 验证返回的 ChatReply 内容由默认 Agent 生成

**验证**：dotnet build 0 错误 + dotnet test 56/56 通过 ✅（对应 commit `eb2dee5`）

### [x] T15 (预计 5min) 测试：POST /api/chat 带 model 参数时路由到对应 Agent
- [x] mock ModelRouter，注册 "gpt-4.1" 和 "qwen"
- [x] 发送 POST /api/chat 含 { username: "marla", message: "推荐跑鞋", model: "gpt-4.1" }
- [x] 验证 ModelRouter.GetAgent("gpt-4.1") 被调用且返回 Agent 处理请求

**验证**：dotnet build 0 错误 + dotnet test 57/57 通过 ✅（对应 commit `a6d77a8`）

### [x] T18 (预计 5min) 测试：跨模型切换保持同一会话的历史连续
- [x] 创建两个独立 mock Agent（qwen, gpt-4.1），各返回不同回复内容
- [x] 先发送 model="qwen" 的消息，再发送 model="gpt-4.1" 的消息（同一用户 -> 同一 sessionId）
- [x] 调用 POST /api/login 获取完整历史
- [x] 验证历史中包含来自两次对话的用户消息和助手回复

**验证**：dotnet build 0 错误 + dotnet test 46/46 通过 ✅
