# AIShop MCP Server — 设计文档

> 状态：已确认 | 日期：2026-06-20 | 作者：架构师 Agent

## 1. 背景与目标

将 AIShop 的产品目录匹配能力通过 MCP (Model Context Protocol) 以 HTTP 方式暴露，供外部服务/系统调用。当前暴露 `match_products` 这一个核心工具。

## 2. 项目定位

```
src/
  AIShop.AppHost/               ← 新增：Aspire 编排项目
  AIShop.McpServer/             ← 新增：MCP Server 项目
  AIShop.Api/                   ← 现有：Minimal API
  AIShop.Core/                  ← 现有：领域实体
  AIShop.Infrastructure/        ← 现有：ProductCatalog
tests/
  AIShop.McpServer.Tests/       ← 新增：MCP Server 测试
  AIShop.Api.Tests/             ← 现有：Api 测试
```

## 3. 技术栈

| 类别 | 选型 | 说明 |
|------|------|------|
| .NET | 10.0 / C# 14 | 对齐现有项目 |
| MCP SDK | `ModelContextProtocol.AspNetCore` | HTTP 传输 |
| 日志 | Serilog.AspNetCore | 对齐 Api 项目 |
| 代码质量 | SonarAnalyzer 10.* | 全局 `Directory.Build.props` |

## 4. 架构

```
外部系统 (HTTP Client)
    │
    ▼ POST /mcp (Streamable HTTP)
┌─────────────────────────────┐
│  AIShop.McpServer           │
│  ├─ Program.cs              │  ← AddMcpServer() + MapMcp()
│  └─ Tools/                  │
│     └─ ProductTools.cs      │  ← [McpServerToolType] match_products
└──────────┬──────────────────┘
           │ 直接 static 调用，零 DI
┌──────────▼──────────────────┐
│  AIShop.Infrastructure      │
│  └─ ProductCatalog (static) │
│     ├─ MatchProducts()      │
│     ├─ KeywordMap (23项)    │
│     └─ All (18件商品)       │
└──────────┬──────────────────┘
           │
┌──────────▼──────────────────┐
│  AIShop.Core                │
│  └─ Product Entity          │
└─────────────────────────────┘
```

**关键决策：**
- **零 DI 注册** — `ProductCatalog` 无状态、无依赖，纯 static 调用
- **不接触数据库** — 商品数据硬编码，无需 EF Core / SQLite
- **不引用 MAF** — 无需 `Microsoft.Agents.AI`，MCP Server 纯粹是工具暴露
- **不引用 Api** — 独立项目，仅依赖 Infrastructure（传递 Core）

## 5. MCP 工具定义

### 5.1 `match_products`

| 属性 | 内容 |
|------|------|
| 工具名 | `match_products` |
| 参数 | `keywords: string[]` — 中文关键词列表 |
| 返回 | `Product[]` JSON 数组，最多 6 条 |
| 核心逻辑 | `ProductCatalog.MatchProducts(keywords)` |
| 空参数 | 返回空数组 `[]` |

### 5.2 调用流程

```
match_products(["运动", "户外", "咖啡"])
    │
    ├─→ KeywordMap["运动"] → ["运动", "健身", "体育", "跑步", "瑜伽", "健康"]
    ├─→ KeywordMap["户外"] → ["户外", "徒步", "自然", "冒险", "园艺"]
    ├─→ KeywordMap["咖啡"] → ["咖啡", "浓缩", "早晨", "厨房"]
    │
    └─→ 遍历 18 件商品，按标签命中数排序 → Top 6
         例：[
           { "id": 3,  "name": "专业跑鞋",       "score": 4 },
           { "id": 13, "name": "户外徒步靴",      "score": 3 },
           { "id": 6,  "name": "高级瑜伽垫",      "score": 2 },
           { "id": 5,  "name": "意式浓缩咖啡机",  "score": 2 },
           { "id": 10, "name": "智能运动手表",    "score": 2 },
           { "id": 14, "name": "植物蛋白粉",      "score": 2 },
         ]
```

## 6. 项目依赖

| 包 | 用途 |
|----|------|
| `ModelContextProtocol.AspNetCore` | MCP HTTP Server |
| `Serilog.AspNetCore` | 结构化日志 |
| `AIShop.Core` (传递) | Domain entities |
| `AIShop.Infrastructure` (直接) | `ProductCatalog` |

**不引入：**
- `Microsoft.Agents.AI` — MCP Server 不需要 MAF
- `Microsoft.EntityFrameworkCore.Sqlite` — 不访问数据库
- `Microsoft.Extensions.AI.OpenAI` — 不调用 LLM

## 7. 配置

```json
// appsettings.json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    },
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

环境变量：
- `ASPNETCORE_URLS` — 监听地址（容器默认 `http://+:8080`）
- `ASPNETCORE_ENVIRONMENT` — `Development` / `Production`

## 8. 代码结构

```
src/AIShop.McpServer/
├── AIShop.McpServer.csproj
├── Program.cs
├── Tools/
│   └── ProductTools.cs          # [McpServerToolType] 工具类
├── appsettings.json
├── appsettings.Development.json
└── Properties/
    └── launchSettings.json
```

### 8.1 Program.cs

```csharp
using Serilog;
using ModelContextProtocol.Server;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
app.MapGet("/health", () => Results.Ok(new { Status = "healthy" }));
app.Run();
```

### 8.2 ProductTools.cs

```csharp
[McpServerToolType]
public static class ProductTools
{
    [McpServerTool, Description("根据中文关键词匹配商品。传入关键词列表，返回最相关的商品列表（最多6条）。")]
    public static ProductDto[] MatchProducts(
        [Description("中文关键词列表，例如 [\"运动\", \"户外\", \"咖啡\"]")]
        string[] keywords,
        CancellationToken cancellationToken = default)
    {
        var products = ProductCatalog.MatchProducts(keywords);
        return products.Select(ProductDto.FromEntity).ToArray();
    }
}
```

## 9. Aspire 编排（开发调试）

### 9.1 定位

`AIShop.AppHost` — .NET Aspire 编排项目，用于本地开发时一键启动服务组、查看日志和链路追踪。不参与生产部署。

### 9.2 编排拓扑

```
┌──────────────────────────────────────────────┐
│  AIShop.AppHost                              │
│  ├── Api (ProjectResource)                   │
│  │   └── http://localhost:5206               │
│  ├── McpServer (ProjectResource)             │
│  │   ├── POST /mcp ← MCP 端点               │
│  │   └── GET /health                         │
│  └── Dashboard                               │
│      ├── 结构化日志 (Serilog → OTLP)          │
│      ├── HTTP 请求追踪                        │
│      └── 资源状态监控                         │
└──────────────────────────────────────────────┘
```

### 9.3 AppHost 配置

```csharp
// src/AIShop.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.AIShop_Api>("api");
var mcpServer = builder.AddProject<Projects.AIShop_McpServer>("mcp");

builder.Build().Run();
```

### 9.4 McpServer 接入 Aspire

`AIShop.McpServer` 项目添加 Aspire 的 ServiceDefaults：

```csharp
// Program.cs — 增加 Aspire 集成
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();              // ← Aspire: 自动 OTLP 导出 + 健康检查
builder.Host.UseSerilog();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
app.MapDefaultEndpoints();                 // ← Aspire: /health → /alive, /health
app.Run();
```

```xml
<!-- AIShop.McpServer.csproj 增加 -->
<PackageReference Include="Aspire.StackExchange.Redis" Version="10.0.0" />
<!-- ServiceDefaults 引用: Aspire 自动添加 OpenTelemetry, 健康检查, 弹性等 -->
```

### 9.5 开发工作流

```bash
# 一键启动
dotnet run --project src/AIShop.AppHost
# Aspire Dashboard 自动打开 → 查看日志/追踪
# Api: http://localhost:5206
# McpServer: 通过 Aspire 分配的端口

# 调试 MCP 工具 — 配合 MCP Inspector
npx @modelcontextprotocol/inspector
# 在 Inspector 中输入 McpServer 的代理 URL 进行工具测试
```

### 9.6 不纳入 Aspire 的内容

- 生产部署仍走独立进程（`dotnet run` / `dotnet publish`）
- 不引入 Redis/消息队列等额外基础设施
- Serilog 写 Console，不强制走 OTLP

## 10. 部署

独立进程运行，通过命令行启动：

```bash
cd src/AIShop.McpServer
dotnet run
# 或发布后直接运行
dotnet publish -c Release -o ./publish
./publish/AIShop.McpServer.exe
```

通过环境变量配置监听地址：

```bash
# Windows
set ASPNETCORE_URLS=http://+:8080

# Linux/macOS
export ASPNETCORE_URLS=http://+:8080
```

默认监听 `http://localhost:5000`。

## 11. 测试策略

```
tests/AIShop.McpServer.Tests/
├── AIShop.McpServer.Tests.csproj
├── ProductToolsTests.cs              # 关键词匹配逻辑单元测试
└── McpServerIntegrationTests.cs      # WebApplicationFactory 集成测试
```

| 测试用例 | 类型 | 验证点 |
|----------|------|--------|
| `MatchProducts_ValidKeywords_ReturnsResults` | 单元 | 正常关键词→匹配商品 |
| `MatchProducts_EmptyKeywords_ReturnsEmpty` | 单元 | 空数组→空数组 |
| `MatchProducts_UnknownKeyword_ReturnsEmpty` | 单元 | 未匹配→空数组 |
| `MatchProducts_ResultLimit_Six` | 单元 | 结果不超过 6 条 |
| `McpServer_StartsAndResponds` | 集成 | HTTP 服务启动 + 健康检查 |
| `MatchProducts_JsonRpc_ReturnsValidSchema` | 集成 | MCP 协议 Schema 正确 |

## 12. 与 Api 的关系

```
AIShop.McpServer ──→ AIShop.Infrastructure ←── AIShop.Api
                        │
                        └── ProductCatalog (共享，无状态)
```

- 两个入口共享同一套 `ProductCatalog`，各自独立部署
- Api 路径：对话式购物助手（有状态，Session + LLM）
- McpServer 路径：纯工具调用（无状态，HTTP MCP）

## 13. 接入文档

详见 `docs/mcp-integration-guide.md`。
