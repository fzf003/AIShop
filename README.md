# AIShop — 智能购物助手

基于 .NET 10 + LLM 的智能购物助手，支持自然语言对话推荐商品，并通过 MCP 协议将商品匹配能力暴露给外部系统。

## 快速开始

```bash
# 1. 安装 .NET 10 SDK
# https://dotnet.microsoft.com/download/dotnet/10.0

# 2. 克隆项目
git clone <repo-url> && cd AIShop

# 3. 配置 LLM
echo "OpenAI__Key=<你的 API key>" > src/AIShop.Api/.env

# 4. 启动
cd src/AIShop.AppHost && dotnet run
```

启动后自动打开 `http://localhost:5206/index.html`，点击用户头像即可开始对话。

默认使用 GitHub Models（Azure AI Inference），也可在 `src/AIShop.Api/appsettings.json` 中切换到 OpenAI。

## 核心功能

| 功能 | 说明 |
|------|------|
| 对话购物 | LLM 理解自然语言需求，从 18 件商品中推荐最匹配的 |
| 推荐分层 | 匹配商品排"为您推荐"区，不匹配的放"其他商品"区 |
| 关键词匹配 | 23 个中文关键词覆盖全部商品标签 |
| MCP 工具 | 外部系统可通过 HTTP JSON-RPC 调用 `match_products` |
| 聊天持久化 | SQLite 存储所有对话，登录后恢复历史 |

## API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/api/login` | 用户登录，返回历史和 session |
| POST | `/api/chat` | 发送消息，返回对话 + 推荐商品 |
| POST | `/api/recommendations` | 分析对话偏好，获取推荐 |
| GET | `/api/products` | 获取全部 18 件商品 |
| POST | `/api/mcp/match` | MCP 商品匹配 |
| POST | `/mcp` | MCP JSON-RPC 端点（McpServer） |

## 项目结构

```
src/
├── AIShop.Api/              Minimal API + Agent + 前端
├── AIShop.Core/             实体 + 接口（零依赖）
├── AIShop.Infrastructure/   EF Core + 仓储 + ProductCatalog
├── AIShop.McpServer/        MCP HTTP 服务
├── AIShop.AppHost/          Aspire 编排（开发用）
└── AIShop.ServiceDefaults/  OTLP + 健康检查
tests/
├── AIShop.Api.Tests/        21 个集成测试
└── AIShop.McpServer.Tests/  11 个单元/集成测试
```

## 技术栈

| 类别 | 技术 |
|------|------|
| 运行时 | .NET 10 / C# 14 |
| Agent | Microsoft Agent Framework 1.10 |
| LLM | GPT-4.1（GitHub Models） |
| 数据库 | SQLite (EF Core) |
| 日志 | Serilog |
| MCP | ModelContextProtocol 1.0 |
| 编排 | .NET Aspire |
| 测试 | xUnit + NSubstitute |
| 代码质量 | SonarAnalyzer（警告即错误） |

## 开发

```bash
dotnet build              # 构建
dotnet test               # 32 个测试全部通过
dotnet run --project src/AIShop.Api      # 单独启动 API
dotnet run --project src/AIShop.AppHost  # Aspire 一键启动
```

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [安装指南](docs/INSTALL.md)
- [MCP 接入说明](docs/mcp-integration-guide.md)
- [设计文档](docs/design/)
