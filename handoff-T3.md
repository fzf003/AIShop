# T3 Handoff — SanitizingChatClient + 注册到 ShoppingAssistantAgent

## 状态

**已完成**。所有 72 个 Api.Tests 测试通过（含 26 个 SanitizingChatClient 专有测试），11 个 McpServer.Tests 通过。

## 修改文件

### 新增

1. `src/AIShop.Api/Agents/SanitizingChatClient.cs` — 发前清洗管线装饰器
   - `SanitizingChatClient(IChatClient inner)` — 实现 IChatClient 接口
   - 4 个 `public static` 步骤方法（各带单元测试）
   - `GetResponseAsync` 中按序编排 4 步并透传给 inner
   - `GetStreamingResponseAsync` 原样透传
   - `GetService` / `Dispose` 透传

2. `tests/AIShop.Api.Tests/SanitizingChatClientTests.cs` — 26 个单元测试
   - T3.1: Dispose/GetService/Streaming 透传、ShoppingAssistantAgent 构造验证
   - T3.2: 删空 tool_calls（4 个测试：空移除、有文本保留、有 FCC 保留、非assistant）
   - T3.3: 补缺失工具结果（4 个测试：全缺追加、完全匹配跳过、无 FCC 不变、部分缺失）
   - T3.4: 合并连续同角色（6 个测试：user/user、assistant/assistant、跳过含 FCC prev、跳过含 FCC curr、3合1、保留 tool 消息）
   - T3.5: 重编号 CallId（5 个测试：基础重编号、顺序递增、无 FCC 不变、跨消息映射、保留其他内容）
   - T3.6: 完整管线（2 个测试：GetResponseAsync 整体验证、手动 4 步 OpenAPI 兼容验证）

### 修改

3. `src/AIShop.Api/Agents/ShoppingAssistantAgent.cs` — 构造函数中添加 `chatClient = new SanitizingChatClient(chatClient);` 包装
4. `src/AIShop.Api/Features/Chat/ChatEndpoints.cs` — 修复 pre-existing S8969 警告
5. `tests/AIShop.Api.Tests/CartEndpointsTests.cs` — 批量修复 pre-existing S8969 警告
6. `tests/AIShop.Api.Tests/ChatEndpointsTests.cs` — 批量修复 pre-existing S8969 警告
7. `tests/AIShop.Api.Tests/GlobalExceptionHandlerTests.cs` — 修复 pre-existing S8969 警告

## 注意点

- 步骤方法的可见性从 `internal static` 改为 `public static`，因 InternalsVisibleTo 在完整解决方案构建中未完全生效
- 修复了 Step2 中原始实现会重复添加已有 tool 消息的 bug
- 所有 pre-existing S8969 警告已批量清理（避免 blocking build），非本变更引入
