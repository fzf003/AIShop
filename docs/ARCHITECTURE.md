# AIShop — 架构说明

> 最后更新：2026-06-24 | 框架：net10.0 / C# 14

## 一、项目概览

AIShop 是一个**智能购物助手**，通过 LLM 对话理解用户购物意图，基于关键词匹配推荐商品。同时通过 MCP (Model Context Protocol) 将商品匹配能力以 HTTP JSON-RPC 方式暴露给外部系统。

### 核心能力

- LLM 对话购物助手（语义理解 → 关键词提取 → 商品推荐）
- MCP 服务器（外部系统可直接调用 `match_products`）
- SQLite 持久化聊天历史（自动加载最近 20 条）
- .NET Aspire 编排（本地开发一键启动 + 日志追踪）

---

## 二、项目拓扑

```
AIShop.sln
├── src/
│   ├── AIShop.AppHost/          Aspire 编排项目（本地开发入口）
│   ├── AIShop.Api/              Minimal API + Agent + 前端页面
│   ├── AIShop.McpServer/        MCP HTTP 服务（商品匹配工具）
│   ├── AIShop.Core/             领域实体 + 接口定义（零依赖）
│   ├── AIShop.Infrastructure/   仓储实现 + EF Core + ProductCatalog
│   └── AIShop.ServiceDefaults/  OTLP 导出 + 健康检查 + 弹性
└── tests/
    ├── AIShop.Api.Tests/        xUnit 集成测试（WebApplicationFactory）
    └── AIShop.McpServer.Tests/  商品匹配工具单元测试 + MCP 集成测试
```

### 依赖方向

```
AIShop.Api ──────┐
AIShop.McpServer ─┤
                  ├──→ AIShop.Infrastructure ──→ AIShop.Core
                  │
                  └──→ AIShop.ServiceDefaults
                  ↑
AIShop.AppHost ───┘（引用 Api + McpServer + ServiceDefaults）
```

Core 零对外依赖（仅 `Microsoft.Agents.Core`），Infrastructure 引用 Core，Api 和 McpServer 引用 Infrastructure。

---

## 三、技术栈

| 类别 | 技术 | 版本 |
|------|------|------|
| 运行时 | .NET | 10.0 |
| 语言 | C# | 14 |
| Agent 框架 | Microsoft.Agents.AI | 1.10.0 |
| LLM | OpenAI / Azure AI Inference | GPT-4.1 |
| ORM | EF Core (SQLite) | 10.0.9 |
| 日志 | Serilog | 10.0.0 |
| MCP | ModelContextProtocol.AspNetCore | 1.0.0 |
| 编排 | .NET Aspire | 13.4.6 |
| 可观测性 | OpenTelemetry | 1.15.x |
| 测试 | xUnit + NSubstitute | 2.9.3 |
| 代码质量 | SonarAnalyzer | 10.* |

---

## 四、架构模式

### Vertical Slice Architecture

每个功能在 `Api/Features/{功能名}/` 下独立切片：

```
Api/Features/
├── Chat/
│   └── ChatEndpoints.cs        聊天端点 + DTO
├── Mcp/
│   ├── McpProductClient.cs     MCP HTTP 客户端
│   └── ProductMatchResult.cs   匹配结果模型
└── McpIntegration/
    └── McpEndpoints.cs         MCP 集成测试端点
```

### Agent 与端点分离

- **Agent 定义** → `Api/Agents/`（LLM 交互逻辑）
- **端点** → `Api/Features/`（HTTP 请求处理）
- **Infrastructure** → 通过 `AddInfrastructure()` 统一注册

### 代码规范

| 规则 | 说明 |
|------|------|
| `TreatWarningsAsErrors` | 编译警告即为错误 |
| `Nullable: enable` | 所有引用类型默认非空 |
| async/await | 所有 I/O 操作异步，禁止 `.Result` / `.Wait()` |
| 接口 `I` 前缀 | `IUserRepository`、`ISessionRepository` |

---

## 五、关键数据流

### 聊天推荐流程

```
用户消息 ──→ POST /api/chat
                │
                ├── 1. 查找用户 + 获取 Session
                ├── 2. ShoppingAssistantAgent.RunChatAsync()
                │       ├── SqliteChatHistoryProvider 加载最近 20 条
                │       ├── Instructions 含 KeywordMap（23 个关键词）
                │       └── RunAsync<AgentChatResult> → { Reply, Keywords }
                ├── 3. 保存 user + assistant 消息到 SQLite
                ├── 4. 白名单过滤 keywords → SplitProducts()
                │       ├── 推荐区（匹配商品，按优先级排序）
                │       └── 其他区（不匹配商品）
                └── 5. 返回 ChatReply（双列表 + 推荐标识）
```

### MCP 匹配流程

```
外部系统 ──→ POST /mcp (JSON-RPC)
                │
                ├── 1. initialize（握手 + 获取 sessionId）
                ├── 2. tools/call { name: "match_products", arguments: { keywords: [...] } }
                │       └── ProductTools.MatchProducts()
                │           └── ProductCatalog.MatchProducts() → Top 6
                └── 3. 返回 [{ id, name, price, tags, emoji }]
```

### Aspire 编排拓扑

```
┌──────────────────────────────────┐
│ AIShop.AppHost                    │
│                                    │
│  api (AIShop.Api)                  │
│  ├── HTTP: http://localhost:5206  │
│  ├── HTTPS: https://localhost:7010│
│  └── WithReference(mcp) ──────┐   │
│                                 │   │
│  mcp (AIShop.McpServer)        │   │
│  ├── HTTP: http://localhost:6500│←─┘  服务发现
│  └── ExcludeFromMcp()            │
│                                    │
│  Dashboard ← OTLP 日志 + 追踪    │
└──────────────────────────────────┘
```

---

## 六、数据库

SQLite（开发环境），通过 EF Core 管理：

| 表 | 用途 |
|----|------|
| `Users` | 用户名 + 显示名 |
| `Sessions` | 用户会话 |
| `ChatMessages` | 聊天消息（所有历史保留，Provider 加载最近 20 条） |

`EnsureCreatedAsync()` 自动建表 + Seeding（marla、steve、fzf003 三个测试用户）。

---

## 七、部署架构

```
┌──────────────────────────────────────────┐
│  生产环境                                  │
│                                            │
│  AIShop.Api ────独立进程───→ :8080         │
│  AIShop.McpServer ──独立进程───→ :8080     │
│                                            │
│  Aspire AppHost 仅用于本地开发             │
└──────────────────────────────────────────┘
```

---

## 八、测试策略

| 项目 | 测试文件 | 类型 |
|------|---------|------|
| `AIShop.Api.Tests` | `ChatEndpointsTests.cs` | 集成测试（WebApplicationFactory + Mock IChatClient） |
| `AIShop.Api.Tests` | `ProductCatalogTests.cs` | 单元测试 |
| `AIShop.Api.Tests` | `SqliteChatHistoryProviderTests.cs` | 单元测试 |
| `AIShop.McpServer.Tests` | `ProductToolsTests.cs` | 单元测试 |
| `AIShop.McpServer.Tests` | `McpServerIntegrationTests.cs` | 集成测试 |

**规则：** 不调用真实 LLM，外部服务一律 Mock。

---

## 九、配置清单

| 配置项 | 来源 | 说明 |
|--------|------|------|
| `OpenAI__Endpoint` | `appsettings.json` | `https://models.inference.ai.azure.com` |
| `OpenAI__Model` | `appsettings.json` | `gpt-4.1` |
| `OpenAI__Key` | `.env` 文件 | GitHub PAT（不提交到 Git） |
| `ASPNETCORE_URLS` | 环境变量 | 覆盖监听地址 |
