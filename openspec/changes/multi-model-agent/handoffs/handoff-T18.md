# Handoff — T18: 跨模型切换保持同一会话的历史连续

## 状态：已完成

## 改动文件
- `tests/AIShop.Api.Tests/ChatEndpointsTests.cs` — 新增 `History_IsPreserved_WhenSwitchingModels` 测试

## 测试内容
- 创建两个独立的 mock Agent（qwen, gpt-4.1），各返回不同回复
- 先发送 model="qwen" + 消息1 → 再发送 model="gpt-4.1" + 消息2（同一用户 marla，同一 sessionId）
- POST /api/login 获取完整历史
- 验证历史中包含来自两次对话的用户消息和助手回复

## 验证结果
- `dotnet build`: 0 错误 / 0 警告
- `dotnet test`: 46/46 通过

## 涉及的 AC
- AC3: POST /api/chat 支持 model 参数 — 同一 sessionId 跨模型切换保留历史

## 接手提示
下一步可验证前端实际多模型切换场景（T19+），无技术债。
