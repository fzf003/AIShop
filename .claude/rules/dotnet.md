---
name: dotnet
description: .NET 编码规则和项目规范，适用于 src/ 和 tests/ 目录
paths: src/**, tests/**
---

# .NET 编码规则

## 代码规范

- TreatWarningsAsErrors: true（已在 Directory.Build.props 启用）
- Nullable: enable
- ImplicitUsings: enable

## 命名约定

| 元素 | 规范 | 示例 |
|------|------|------|
| 命名空间 | PascalCase.分层 | AIShop.Features.Chat |
| 类/方法 | PascalCase | ChatEndpoints, MapChatEndpoints() |
| 参数/变量 | camelCase | chatClient, sessionId |
| 接口 | I 前缀 | IUserRepository |
| 私有字段 | _camelCase | _logger, _dbContext |
| 文件系统 | 文件名 = 类型名 | ChatEndpoints.cs |

## 架构规则

- Core 零依赖：AIShop.Core 不引用其他项目，仅引用 Microsoft.Agents.Core
- 垂直切片：新增功能在 Api/Features/{功能名}/ 下创建切片目录
- 端点模式：扩展方法 static void Map{功能名}Endpoints(this WebApplication app)
- Agent 分离：Agent 定义在 Api/Agents/，端点逻辑在 Features/
- DI 批量注册：Infrastructure 通过 AddInfrastructure() 扩展方法统一注册
- 涉及 MAF 代码时，先调用 /maf-reference 获取准确 API 签名

## 异步规范

- 所有 I/O 操作使用 async/await
- Endpoint handler 返回 Task<*>
- 避免 .Result 或 .Wait()

## 测试规范

- 框架：xUnit v3
- 集成测试：WebApplicationFactory<Program>
- Mock 外部 AI 服务，不调用真实 LLM
- 命名：Should{预期结果}_When{条件}
- 覆盖率：coverlet.collector，关键路径 > 80%

## NuGet 规范

- 包版本集中管理（考虑引入 Directory.Packages.props）
- 临时项目不从解决方案引用
- 测试项目用 coverlet.collector 收集覆盖率
