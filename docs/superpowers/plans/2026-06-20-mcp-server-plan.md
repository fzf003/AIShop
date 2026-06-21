# AIShop MCP Server — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 创建独立的 MCP HTTP Server 项目，将 `ProductCatalog.MatchProducts` 能力通过 MCP 协议暴露为 `match_products` 工具，配合 Aspire 编排与 xUnit 测试。

**Architecture:** 新增 `AIShop.McpServer`（Web 项目，引用 Infrastructure），`AIShop.McpServer.Tests`（xUnit，WebApplicationFactory 集成测试），`AIShop.AppHost`（Aspire 编排，引用 McpServer + Api）。McpServer 零 DI 注册，直接 static 调用 `ProductCatalog`。

**Tech Stack:** .NET 10 / C# 14, `ModelContextProtocol.AspNetCore`, Serilog, xUnit 2.9.3, NSubstitute, .NET Aspire 10.0

## 全局约束

- `net10.0`, `Nullable:enable`, `ImplicitUsings:enable`, `TreatWarningsAsErrors:true`
- SonarAnalyzer.CSharp 10.* (全局 `Directory.Build.props` 已覆盖)
- 命名规范: PascalCase 类/方法, camelCase 参数, `_camelCase` 私有字段
- 不引用 `Microsoft.Agents.AI`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.AI.OpenAI`
- 不引用 `AIShop.Api`
- 所有 I/O 使用 async/await
- 环境变量: `ASPNETCORE_URLS` 控制监听地址

---

### Task 1: ✅ 搭建 MCP Server 项目骨架

**Files:**
- Create: `src/AIShop.McpServer/AIShop.McpServer.csproj`
- Create: `src/AIShop.McpServer/Program.cs`
- Create: `src/AIShop.McpServer/appsettings.json`
- Create: `src/AIShop.McpServer/appsettings.Development.json`

**Interfaces:**
- Produces: `AIShop.McpServer` 项目 — 编译通过并能启动空 MCP HTTP Server

- [x] **Step 1: 创建 csproj 文件**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AIShop.Infrastructure\AIShop.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.0.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
  </ItemGroup>

</Project>
```

- [x] **Step 2: 创建 appsettings.json**

```json
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

- [x] **Step 3: 创建 appsettings.Development.json**

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

- [x] **Step 4: 创建 Program.cs（最小可运行）**

```csharp
using Serilog;
using ModelContextProtocol.Server;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    app.MapMcp();
    app.MapGet("/health", () => Results.Ok(new { Status = "healthy" }));

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MCP Server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
```

- [x] **Step 5: 验证构建**

```bash
dotnet build src/AIShop.McpServer/AIShop.McpServer.csproj
```

Expected: Build succeeded with no errors.

- [x] **Step 6: 将 McpServer 加入解决方案**

```bash
dotnet sln AIShop.sln add src/AIShop.McpServer/AIShop.McpServer.csproj --solution-folder src
```

- [x] **Step 7: 提交**

```bash
git add src/AIShop.McpServer/ AIShop.sln
git commit -m "feat: scaffold MCP Server project with Program.cs and appsettings"
```

---

### Task 2: ✅ 实现 ProductTools 工具类 + ProductDto

**Files:**
- Create: `src/AIShop.McpServer/Tools/ProductTools.cs`

**Interfaces:**
- Consumes: `ProductCatalog.MatchProducts(string[]): Product[]` (AIShop.Infrastructure)
- Consumes: `Product` 实体: `Id: int, Name: string, Category: string, Tags: string[], Price: decimal, Emoji: string` (AIShop.Core)
- Produces: `ProductTools` — `[McpServerToolType]` static class，工具 `match_products(string[] keywords)` 返回匹配商品

- [x] **Step 1: 创建 ProductTools.cs**

```csharp
using System.ComponentModel;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Services;
using ModelContextProtocol.Server;

namespace AIShop.McpServer.Tools;

[McpServerToolType]
public static class ProductTools
{
    [McpServerTool, Description("根据中文关键词匹配商品。传入中文关键词列表（如 [\"运动\", \"户外\", \"咖啡\"]），返回按相关度排序的最多 6 条匹配商品。")]
    public static MatchProductDto[] MatchProducts(
        [Description("中文关键词列表，例如 [\"运动\", \"户外\", \"咖啡\"]。支持 23 个预定义关键词，自动扩展匹配标签。")]
        string[] keywords,
        CancellationToken cancellationToken = default)
    {
        var products = ProductCatalog.MatchProducts(keywords);
        return products.Select(MatchProductDto.FromEntity).ToArray();
    }
}

public sealed record MatchProductDto(
    int Id,
    string Name,
    string Category,
    string[] Tags,
    decimal Price,
    string Emoji)
{
    public static MatchProductDto FromEntity(Product product) => new(
        product.Id,
        product.Name,
        product.Category,
        product.Tags,
        product.Price,
        product.Emoji);
}
```

- [x] **Step 2: 验证构建**

```bash
dotnet build src/AIShop.McpServer/AIShop.McpServer.csproj
```

- [x] **Step 3: 提交**

```bash
git add src/AIShop.McpServer/Tools/
git commit -m "feat: add match_products MCP tool wrapping ProductCatalog.MatchProducts"
```

---

### Task 3: ✅ 创建测试项目 + ProductTools 单元测试

**Files:**
- Create: `tests/AIShop.McpServer.Tests/AIShop.McpServer.Tests.csproj`
- Create: `tests/AIShop.McpServer.Tests/ProductToolsTests.cs`

**Interfaces:**
- Consumes: `ProductTools.MatchProducts(string[]): MatchProductDto[]` (Task 2)
- Consumes: `ProductCatalog.MatchProducts(string[]): Product[]` (via ProductTools)

- [x] **Step 1: 创建测试项目 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AIShop.McpServer\AIShop.McpServer.csproj" />
  </ItemGroup>

</Project>
```

- [x] **Step 2: 将测试项目加入解决方案**

```bash
dotnet sln AIShop.sln add tests/AIShop.McpServer.Tests/AIShop.McpServer.Tests.csproj --solution-folder tests
```

- [x] **Step 3: 创建 ProductToolsTests.cs**

```csharp
using AIShop.McpServer.Tools;

namespace AIShop.McpServer.Tests;

public sealed class ProductToolsTests
{
    [Fact]
    public void MatchProducts_ValidKeywords_ReturnsResults()
    {
        var result = ProductTools.MatchProducts(["运动", "户外"]);

        Assert.NotEmpty(result);
        Assert.True(result.Length <= 6);
        // "专业跑鞋" tags: ["跑步", "运动", "健身", "体育", "鞋子"] — matches "运动"
        Assert.Contains(result, p => p.Name == "专业跑鞋");
        // "户外徒步靴" tags: ["徒步", "户外", "冒险", "自然", "靴子"] — matches "户外"
        Assert.Contains(result, p => p.Name == "户外徒步靴");
    }

    [Fact]
    public void MatchProducts_EmptyKeywords_ReturnsEmpty()
    {
        var result = ProductTools.MatchProducts([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_UnknownKeyword_ReturnsEmpty()
    {
        var result = ProductTools.MatchProducts(["不存在的关键词xyz"]);

        Assert.Empty(result);
    }

    [Fact]
    public void MatchProducts_ResultLimit_Six()
    {
        // 用多个关键词触发足够多的匹配，验证上限为 6
        var result = ProductTools.MatchProducts(["运动", "户外", "音乐", "科技", "咖啡", "健身", "阅读"]);

        Assert.True(result.Length <= 6);
    }

    [Fact]
    public void MatchProducts_SingleKeyword_ReturnsMatches()
    {
        // "咖啡" → 意式浓缩咖啡机
        var result = ProductTools.MatchProducts(["咖啡"]);

        Assert.NotEmpty(result);
        Assert.Contains(result, p => p.Name == "意式浓缩咖啡机");
    }

    [Fact]
    public void MatchProducts_ReturnedDto_HasAllFields()
    {
        var result = ProductTools.MatchProducts(["运动"]);

        Assert.NotEmpty(result);
        var product = result[0];
        Assert.True(product.Id > 0);
        Assert.False(string.IsNullOrEmpty(product.Name));
        Assert.False(string.IsNullOrEmpty(product.Category));
        Assert.NotEmpty(product.Tags);
        Assert.True(product.Price > 0);
        Assert.False(string.IsNullOrEmpty(product.Emoji));
    }
}
```

- [x] **Step 4: 运行测试验证通过**

```bash
dotnet test tests/AIShop.McpServer.Tests/AIShop.McpServer.Tests.csproj
```

Expected: All 6 tests pass.

- [x] **Step 5: 提交**

```bash
git add tests/AIShop.McpServer.Tests/ AIShop.sln
git commit -m "test: add ProductTools unit tests for match_products"
```

---

### Task 4: McpServer HTTP 集成测试

**Files:**
- Create: `tests/AIShop.McpServer.Tests/McpServerIntegrationTests.cs`

**Interfaces:**
- Consumes: `Program` (AIShop.McpServer — 作为 WebApplicationFactory 入口)
- Consumes: MCP 协议 `/mcp` 端点：`tools/list`, `tools/call`

- [ ] **Step 1: 将 InternalsVisibleTo 添加到 McpServer csproj**

在 `src/AIShop.McpServer/AIShop.McpServer.csproj` 的 `<PropertyGroup>` 中追加：

```xml
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InternalsVisibleTo>AIShop.McpServer.Tests</InternalsVisibleTo>
  </PropertyGroup>
```

- [ ] **Step 2: 在测试项目中添加 WebApplicationFactory 包引用**

更新 `tests/AIShop.McpServer.Tests/AIShop.McpServer.Tests.csproj`，在 `<ItemGroup>` 中添加：

```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.9" />
```

- [ ] **Step 3: 创建 McpServerIntegrationTests.cs**

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIShop.McpServer.Tests;

public sealed class McpServerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpServerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthCheck_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }

    [Fact]
    public async Task ToolsList_ReturnsMatchProducts()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list"
        };

        var response = await client.PostAsJsonAsync("/mcp", request);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tools = json.GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();

        Assert.Contains("match_products", names);
    }

    [Fact]
    public async Task ToolsCall_MatchProducts_ReturnsResults()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "match_products",
                arguments = new { keywords = new[] { "运动" } }
            }
        };

        var response = await client.PostAsJsonAsync("/mcp", request);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = json.GetProperty("result");
        var content = result.GetProperty("content")[0];
        var text = content.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains("专业跑鞋", text); // "运动" keyword should match 专业跑鞋
    }

    [Fact]
    public async Task ToolsCall_EmptyKeywords_ReturnsEmptyArray()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "match_products",
                arguments = new { keywords = Array.Empty<string>() }
            }
        };

        var response = await client.PostAsJsonAsync("/mcp", request);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();

        Assert.Equal("[]", text);
    }

    [Fact]
    public async Task ToolsCall_InvalidMethod_ReturnsError()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "nonexistent_tool",
                arguments = new { }
            }
        };

        var response = await client.PostAsJsonAsync("/mcp", request);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("error", out _));
    }
}
```

- [ ] **Step 4: 运行集成测试验证通过**

```bash
dotnet test tests/AIShop.McpServer.Tests/AIShop.McpServer.Tests.csproj
```

Expected: All 11 tests pass (6 unit + 5 integration).

- [ ] **Step 5: 提交**

```bash
git add tests/AIShop.McpServer.Tests/ src/AIShop.McpServer/
git commit -m "test: add McpServer HTTP integration tests with WebApplicationFactory"
```

---

### Task 5: 搭建 Aspire AppHost 编排项目

**Files:**
- Create: `src/AIShop.AppHost/AIShop.AppHost.csproj`
- Create: `src/AIShop.AppHost/Program.cs`
- Create: `src/AIShop.AppHost/appsettings.json`
- Create: `src/AIShop.AppHost/Properties/launchSettings.json`

**Interfaces:**
- Consumes: `AIShop.McpServer` 项目引用
- Consumes: `AIShop.Api` 项目引用
- Produces: `AIShop.AppHost` — Aspire 编排，一键启动 Api + McpServer

- [ ] **Step 1: 安装 Aspire 工作负载**

```bash
dotnet workload install aspire
```

等待安装完成。如果已安装则跳过。

- [ ] **Step 2: 从 Api 项目中获取 Aspire 包版本参考**

```bash
dotnet new aspire-apphost --dry-run 2>&1 || echo "checking template"
```

用模板干跑确认版本信息。

- [ ] **Step 3: 创建 AppHost.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AIShop.Api\AIShop.Api.csproj" />
    <ProjectReference Include="..\AIShop.McpServer\AIShop.McpServer.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: 创建 AppHost/Program.cs**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.AIShop_Api>("api");
var mcp = builder.AddProject<Projects.AIShop_McpServer>("mcp");

builder.Build().Run();
```

- [ ] **Step 5: 创建 AppHost/appsettings.json**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Aspire.Hosting.Dcp": "Warning"
    }
  }
}
```

- [ ] **Step 6: 创建 launchSettings.json**

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:15887;http://localhost:15888",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:15887",
        "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:15887"
      }
    }
  }
}
```

- [ ] **Step 7: 将 AppHost 加入解决方案**

```bash
dotnet sln AIShop.sln add src/AIShop.AppHost/AIShop.AppHost.csproj --solution-folder src
```

- [ ] **Step 8: 验证 Aspire 启动**

```bash
dotnet build src/AIShop.AppHost/AIShop.AppHost.csproj
```

Expected: Build succeeded.

```bash
dotnet run --project src/AIShop.AppHost/AIShop.AppHost.csproj
```

验证 Dashboard 能打开，Api 和 McpServer 资源显示为 Running。

- [ ] **Step 9: 提交**

```bash
git add src/AIShop.AppHost/ AIShop.sln
git commit -m "feat: add Aspire AppHost orchestrating Api + McpServer"
```

---

### Task 6: 全局验证 + 文档

**Files:**
- 无新建文件

- [ ] **Step 1: 完整解决方案构建**

```bash
dotnet build
```

Expected: 全部 5 个项目构建通过（Core, Infrastructure, Api, McpServer, AppHost）。

- [ ] **Step 2: 运行全部测试**

```bash
dotnet test
```

Expected: 所有现有测试 + 新增 MCP Server 测试全部通过。

- [ ] **Step 3: 验证 MCP Server 独立启动可访问**

```bash
# 后台启动 McpServer
ASPNETCORE_URLS=http://+:15206 dotnet run --project src/AIShop.McpServer &
sleep 3
# 健康检查
curl -s http://localhost:15206/health
# tools/list
curl -s -X POST http://localhost:15206/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# 清理
kill %1
```

Expected 输出:
- `/health` 返回 `{"status":"healthy"}`
- `tools/list` 返回包含 `match_products` 的工具列表

- [ ] **Step 4: 提交**

```bash
git add .
git commit -m "chore: final verification — full build + all tests passing"
```
