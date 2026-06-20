# AIShop — Agent Instructions（代理指令）

> 本文件是 Agent 最高优先级指令。任何代码生成/修改前必须读取此文件。

## 架构

- **架构模式**：Vertical Slice Architecture（垂直切片架构）
- **基本分层**：Core（核心） → Infrastructure（基础设施） → Api（API 层）
- **依赖方向**：始终指向 Core（Core 不依赖任何项目）

```
src/
  AIShop.Api/              # Minimal API 端点、Agent 定义
  AIShop.Core/             # 实体、接口、领域逻辑
  AIShop.Infrastructure/   # EF Core、仓储实现、外部服务
tests/
  AIShop.Api.Tests/        # xUnit 集成/单元测试
```

## 技术栈

| 类别 | 选择 |
|------|------|
| 运行时 | .NET 10 / C# 14 |
| AI SDK | Microsoft Agent Framework（`Microsoft.Agents.*`） |
| 数据库 | **开发**：SQLite，**生产**：PostgreSQL |
| ORM | Entity Framework Core 10 |
| 缓存 | **内存缓存**（`IMemoryCache` + `ICacheService` 包装） |
| 日志 | Serilog |
| OpenAPI | `Microsoft.AspNetCore.OpenApi`（开发时启用） |
| 测试 | xUnit +（可选：Testcontainers 用于集成测试） |

## 核心领域实体

- **User**：用户（Id, Username, DisplayName, CreatedAt）
- **Session**：会话（Id, UserId, CreatedAt, LastActivityAt）
- **ChatMessage**：聊天消息（Id, SessionId, Role, Content, Timestamp）

## 关键接口（定义在 Core）

- `IChatHistoryProvider` — 加载/保存对话历史
- `IUserRepository` / `ISessionRepository` — 用户与会话持久化
- `ICacheService` — 缓存抽象，基于内存实现
- `IProductRepository` / `IPreferenceAnalyzer` — 商品与偏好分析

## MAF 技能参考（读取顺序）

当涉及 Microsoft Agent Framework 代码时，按以下顺序读取 `.codex/skills/maf-reference/` 下的参考文件：

1. [core-abstractions.zh-CN.md](.codex/skills/maf-reference/references/core-abstractions.zh-CN.md) — AIAgent、AIContext、AgentSession
2. [chat-client-agent.zh-CN.md](.codex/skills/maf-reference/references/chat-client-agent.zh-CN.md) — ChatClientAgent 构造和 RunAsync
3. [context-provider.zh-CN.md](.codex/skills/maf-reference/references/context-provider.zh-CN.md) — AIContextProvider 中间件模式
4. [chat-history.zh-CN.md](.codex/skills/maf-reference/references/chat-history.zh-CN.md) — ChatHistoryProvider
5. [tools.md](.codex/skills/maf-reference/references/tools.md) — AITool / 函数工具注册
6. [decorators.md](.codex/skills/maf-reference/references/decorators.md) — DelegatingAIAgent 和装饰器
7. [compaction.md](.codex/skills/maf-reference/references/compaction.md) — 上下文压缩策略
8. [hosting-di.md](.codex/skills/maf-reference/references/hosting-di.md) — AddAIAgent / DI 注册
9. [workflows.md](.codex/skills/maf-reference/references/workflows.md) — Sequential/Handoff/GroupChat/MagenticOne
10. [mcp.md](.codex/skills/maf-reference/references/mcp.md) — MCP 客户端集成

> 任何 MAF 相关问题可参考 [官方文档](https://learn.microsoft.com/zh-cn/agent-framework/)

## 开发指南

1. **每个功能一个切片**：新增功能在 `Api/Features/{功能名}/` 下创建
2. **Minimal APIs**：端点扩展方法统一用 `Map{功能名}Endpoints(this WebApplication app)`
3. **依赖注入**：Infrastructure 通过 `AddInfrastructure()` 批量注册服务
4. **Agent 放在 `Api/Agents/`**：与端点分离，保持关注点隔离
5. **TreatWarningsAsErrors**：启用，SonarAnalyzer 用于代码质量门禁
6. **MAF 代码必须参考 skill**：涉及 `ChatClientAgent`/`AIContextProvider`/`Workflow` 等 MAF API 时，先读取对应 `.codex/skills/maf-reference/references/*.md` 获取正确签名，再生成代码
7. **遵循 Agent 开发历程最佳实践**：参考 `docs/agent-journey-best-practices.md`，遵循渐进式复杂度原则（从简单模式开始，仅在需要时增加复杂度）
8. **温度设置**：Agent 应用使用 `Temperature = 0.2f`（范围 0–0.3），创意任务用 0.7–1.0

## 自定义 Agent

项目预置了两个专用 Agent，可在对话中用 `@` 引用：

| Agent | 文件 | 用途 |
|---|---|---|
| `@架构师Agent` | `.github/agents/arch.agent.md` | 架构设计、技术选型、设计文档编写 |
| `@开发Agent` | `.github/agents/dev.agent.md` | 实现功能、写代码、操作 MAF、创建端点 |
| `@测试Agent` | `.github/agents/test.agent.md` | 编写测试、修复测试失败、集成测试 |

用法示例：
```
@架构师Agent 帮我设计商品搜索功能的架构
@开发Agent 实现商品搜索功能
@测试Agent 给 ChatEndpoints 写集成测试
```

### 工作流程

```
架构师Agent（设计文档）→ 开发Agent（代码实现）→ 测试Agent（测试验证）
```

三个阶段顺序执行，前一阶段冻结后才进入下一阶段。架构师Agent 交付设计文档后，开发Agent 介入实现；开发Agent 完成功能后，测试Agent 介入验证。

## 快速开始

```bash
# 构建
dotnet build

# 运行
dotnet run --project src/AIShop.Api

# 测试
# POST /api/chat
# {"username": "test_user", "message": "Hello"}
```

## 后续步骤

- [/scaffold] 添加新功能切片
- [/health-check] 代码库健康检查
- 集成实际 LLM（替换 `ShoppingAgent.cs` 中的回显占位符）
