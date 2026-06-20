# AIShop MCP Server — 接入说明

> 版本：1.0 | 日期：2026-06-20

## 1. 概述

AIShop MCP Server 通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 的 Streamable HTTP 方式暴露商品匹配能力，外部服务可直接通过 HTTP 调用。

### 1.1 适用场景

- 微服务间调用：另一个业务服务需要商品推荐/匹配能力
- AI 应用集成：支持 MCP 协议的 AI 助手挂载本服务
- 第三方集成：合作伙伴系统通过标准协议获取商品数据

### 1.2 协议说明

本服务实现了 MCP 的 **Streamable HTTP Transport**（见 [MCP 规范 2025-03-26](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports/#streamable-http)）。

- **端点**: `POST /mcp`
- **方法**: `tools/list`（发现工具）, `tools/call`（调用工具）
- **内容类型**: `application/json`

## 2. 快速开始

### 2.1 本地运行

```bash
# 前提：.NET 10 SDK
cd src/AIShop.McpServer
dotnet run
# 默认监听 http://localhost:5000
```

### 2.2 Aspire 方式（开发调试）

```bash
# 通过 Aspire 一键启动所有服务
dotnet run --project src/AIShop.AppHost
# Dashboard 自动打开 → 查看日志/追踪 → 选择 McpServer
```

### 2.3 自定义端口

```bash
# Windows
set ASPNETCORE_URLS=http://+:9000 && dotnet run

# Linux/macOS
ASPNETCORE_URLS=http://+:9000 dotnet run
```

## 3. MCP 工具

### 3.1 `match_products`

根据中文关键词匹配商品，返回按相关度排序的商品列表（最多 6 条）。

**参数:**

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `keywords` | `string[]` | 是 | 中文关键词列表 |

**关键词参考：**

| 关键词 | 扩展标签 | 典型命中 |
|--------|----------|----------|
| `运动` | 运动、健身、体育、跑步、瑜伽、健康 | 跑鞋、瑜伽垫、蛋白粉 |
| `户外` | 户外、徒步、自然、冒险、园艺 | 徒步靴、园艺工具 |
| `咖啡` | 咖啡、浓缩、早晨、厨房 | 咖啡机 |
| `音乐` | 音乐、音频、唱片、复古 | 耳机、唱片机 |
| `科技` | 科技、电子、数码、无线、充电、穿戴 | 耳机、手表、充电板 |
| `健身` | 健身、运动、体育、锻炼、跑步、瑜伽 | 跑鞋、瑜伽垫、手表、蛋白粉 |
| `送礼` | 礼物、美食、香薰、巧克力 | 巧克力礼盒、蜡烛套装 |
| `阅读` | 阅读、看书、书、悬疑、小说 | 悬疑小说 |
| `家居` | 家居、放松、睡眠、香薰、护肤 | 蜡烛套装、丝枕套 |

完整关键词映射共 23 项，见 `ProductCatalog.KeywordMap`。

**返回格式:**

```json
[
  {
    "id": 3,
    "name": "专业跑鞋",
    "category": "鞋类",
    "tags": ["跑步", "运动", "健身", "体育", "鞋子"],
    "price": 129.99,
    "emoji": "👟"
  }
]
```

## 4. API 协议

### 4.1 发现工具 — `tools/list`

**请求:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/list"
}
```

**响应:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [
      {
        "name": "match_products",
        "description": "根据中文关键词匹配商品。传入关键词列表，返回最相关的商品列表（最多6条）。",
        "inputSchema": {
          "type": "object",
          "properties": {
            "keywords": {
              "type": "array",
              "items": { "type": "string" },
              "description": "中文关键词列表，例如 [\"运动\", \"户外\", \"咖啡\"]"
            }
          },
          "required": ["keywords"]
        }
      }
    ]
  }
}
```

### 4.2 调用工具 — `tools/call`

**请求:**
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "match_products",
    "arguments": {
      "keywords": ["运动", "户外"]
    }
  }
}
```

**响应:**
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "[{\"id\":3,\"name\":\"专业跑鞋\",\"category\":\"鞋类\",\"tags\":[\"跑步\",\"运动\",\"健身\",\"体育\",\"鞋子\"],\"price\":129.99,\"emoji\":\"👟\"},{\"id\":13,\"name\":\"户外徒步靴\",\"category\":\"鞋类\",\"tags\":[\"徒步\",\"户外\",\"冒险\",\"自然\",\"靴子\"],\"price\":159.99,\"emoji\":\"🥾\"},{\"id\":6,\"name\":\"高级瑜伽垫\",\"category\":\"健身\",\"tags\":[\"瑜伽\",\"健身\",\"健康\",\"运动\"],\"price\":59.99,\"emoji\":\"🧘\"},{\"id\":10,\"name\":\"智能运动手表\",\"category\":\"电子产品\",\"tags\":[\"健身\",\"科技\",\"健康\",\"穿戴\"],\"price\":199.99,\"emoji\":\"⌚\"},{\"id\":14,\"name\":\"植物蛋白粉\",\"category\":\"健康\",\"tags\":[\"健身\",\"营养\",\"素食\",\"健康\"],\"price\":39.99,\"emoji\":\"💪\"},{\"id\":17,\"name\":\"园艺工具套装\",\"category\":\"户外\",\"tags\":[\"园艺\",\"户外\",\"自然\",\"爱好\"],\"price\":54.99,\"emoji\":\"🌱\"}]"
      }
    ]
  }
}
```

### 4.3 健康检查

```bash
GET /health → 200 OK
{"status":"healthy"}
```

## 5. SDK 接入示例

### 5.1 C# (ModelContextProtocol Client)

```csharp
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

// 连接到 MCP Server
var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
var transport = new StreamableHttpTransport(httpClient);

await using var client = await McpClientFactory.CreateAsync(
    new McpClientOptions { Id = "my-service" },
    transport);

// 列出工具
var tools = await client.ListToolsAsync();
Console.WriteLine($"可用工具: {string.Join(", ", tools.Select(t => t.Name))}");

// 调用 match_products
var result = await client.CallToolAsync("match_products", new Dictionary<string, object>
{
    ["keywords"] = new[] { "运动", "户外" }
});

Console.WriteLine(result.Content[0].Text);
```

### 5.2 Python (mcp)

```python
import asyncio
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

async def main():
    async with streamablehttp_client("http://localhost:5000/mcp") as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            # 列出工具
            tools = await session.list_tools()
            print(f"可用工具: {[t.name for t in tools.tools]}")

            # 调用
            result = await session.call_tool(
                "match_products",
                arguments={"keywords": ["运动", "户外"]}
            )
            print(result.content[0].text)

asyncio.run(main())
```

### 5.3 TypeScript (@modelcontextprotocol/sdk)

```typescript
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

const transport = new StreamableHTTPClientTransport(
    new URL("http://localhost:5000/mcp")
);

const client = new Client({ name: "my-service", version: "1.0.0" });
await client.connect(transport);

// 列出工具
const { tools } = await client.listTools();
console.log("可用工具:", tools.map(t => t.name));

// 调用
const result = await client.callTool({
    name: "match_products",
    arguments: { keywords: ["运动", "户外"] }
});
console.log(result.content[0].text);
```

### 5.4 curl（调试用）

```bash
# 健康检查
curl http://localhost:5000/health

# 发现工具
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# 调用工具
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"match_products","arguments":{"keywords":["运动","户外"]}}}'
```

## 6. 错误处理

| HTTP 状态码 | 场景 |
|-------------|------|
| `200` | 正常响应（JSON-RPC 错误在 `error` 字段中） |
| `400` | 无效的 JSON-RPC 请求 |
| `500` | 服务器内部错误 |

**JSON-RPC 错误示例:**
```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "error": {
    "code": -32602,
    "message": "Invalid params: keywords is required"
  }
}
```

## 7. 限流与容量

| 指标 | 限制 |
|------|------|
| 单次返回商品上限 | 6 条 |
| 支持关键词 | 23 个中文词 |
| 商品目录 | 18 件（硬编码） |
| 并发连接 | 无硬限制（ASP.NET Core 默认） |

## 8. 进程部署

### 8.1 发布

```bash
cd src/AIShop.McpServer
dotnet publish -c Release -o ./publish
```

### 8.2 运行

```bash
# Windows
set ASPNETCORE_URLS=http://+:8080
./publish/AIShop.McpServer.exe

# Linux/macOS
ASPNETCORE_URLS=http://+:8080 ./publish/AIShop.McpServer
```

### 8.3 作为后台服务

**Windows (sc.exe):**
```cmd
sc create AIShopMcp binPath="D:\AIShop\src\AIShop.McpServer\publish\AIShop.McpServer.exe" start=auto
sc start AIShopMcp
```

**Linux (systemd):**
```ini
# /etc/systemd/system/aishop-mcp.service
[Unit]
Description=AIShop MCP Server
After=network.target

[Service]
WorkingDirectory=/opt/aishop-mcp
ExecStart=/opt/aishop-mcp/AIShop.McpServer
Environment=ASPNETCORE_URLS=http://+:8080
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable aishop-mcp
sudo systemctl start aishop-mcp
```
