# AIShop 安装说明

> 适用于 Windows / Linux / macOS | 需要 .NET 10 SDK

## 1. 前置条件

| 环境 | 最低版本 | 检查命令 |
|------|---------|---------|
| .NET SDK | 10.0 | `dotnet --version` |
| Git | 2.x | `git --version` |

```bash
# 检查 SDK
dotnet --version    # 必须 >= 10.0.x

# 如果未安装，去 https://dotnet.microsoft.com/download/dotnet/10.0 下载
```

## 2. 获取代码

```bash
git clone <repo-url>
cd AIShop
```

## 3. 配置 LLM

项目使用 Azure AI Inference（GitHub Models）作为 LLM 后端。需要 GitHub Personal Access Token。

```bash
# 在 src/AIShop.Api/ 下创建 .env 文件
echo "OpenAI__Key=github_pat_<你的token>" > src/AIShop.Api/.env
```

> GitHub PAT 需包含 **`models:read`** 权限。在 https://github.com/settings/tokens 创建。

如果需要换 LLM 提供商，编辑 `src/AIShop.Api/appsettings.json`：

```json
{
  "OpenAI": {
    "Endpoint": "https://api.openai.com/v1",
    "Model": "gpt-4o"
  }
}
```

## 4. 运行方式

### 方式一：Aspire 编排（推荐，开发时用）

一键启动所有服务 + Dashboard 日志追踪：

```bash
cd src/AIShop.AppHost
dotnet run
```

Dashboard 会自动打开，可在浏览器中查看日志、追踪和资源状态。

启动的服务：

| 服务 | HTTP | HTTPS |
|------|------|-------|
| Api（前端 + 聊天） | http://localhost:5206 | https://localhost:7010 |
| McpServer（MCP 工具） | http://localhost:6500 | — |
| Dashboard | 自动打开 → 查看日志/追踪 | — |

打开 `http://localhost:5206/index.html` 即可使用。

### 方式二：单独启动

启动聊天服务（API + 前端）：

```bash
cd src/AIShop.Api
dotnet run
# → http://localhost:5206
```

启动 MCP 服务（如需要）：

```bash
cd src/AIShop.McpServer
dotnet run
# → http://localhost:5000（可通过 ASPNETCORE_URLS 修改）
```

### 方式三：发布部署

```bash
# API
cd src/AIShop.Api
dotnet publish -c Release -o ./publish
./publish/AIShop.Api

# McpServer
cd src/AIShop.McpServer
dotnet publish -c Release -o ./publish
./publish/AIShop.McpServer
```

环境变量配置：

```bash
# Windows
set ASPNETCORE_URLS=http://+:8080

# Linux / macOS
export ASPNETCORE_URLS=http://+:8080
```

### 作为系统服务

**Linux (systemd):**

```ini
# /etc/systemd/system/aishop-api.service
[Unit]
Description=AIShop API
After=network.target

[Service]
WorkingDirectory=/opt/aishop/api
ExecStart=/opt/aishop/api/AIShop.Api
Environment=ASPNETCORE_URLS=http://+:8080
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable aishop-api
sudo systemctl start aishop-api
```

## 5. 测试

```bash
# 运行所有测试
dotnet test

# 仅运行不重新构建
dotnet test --no-build

# 按分类过滤
dotnet test --filter "Category=Integration"
```

测试结果应该全部通过（32 个测试用例）。

## 6. 常见问题

### `.env` 中的 API key 不生效

确保 `DotNetEnv` 包已安装且 `.env` 文件在 `AIShop.Api` 项目目录下。

### 切换到 OpenAI 而非 Azure AI

编辑 `src/AIShop.Api/appsettings.json` 的 `OpenAI.Endpoint` 为 `https://api.openai.com/v1`，并确保 `.env` 中的 `OpenAI__Key` 是有效的 OpenAI API key。

### Aspire 启动失败

```bash
# 查看 Aspire 版本
aspire --version

# 确保运行时版本匹配
dotnet --list-sdks
```

### 端口冲突

修改 `src/AIShop.AppHost/Program.cs` 中的 `WithHttpEndpoint(port: ...)`，或修改 `Properties/launchSettings.json` 中的端口。
