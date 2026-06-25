# AIShop — 工程化开发指南

## 项目信息

- **解决方案**：`AIShop.sln`
- **目标框架**：`net10.0`
- **语言版本**：C# 14
- **SDK**：.NET 10 SDK

## 架构

```
Vertical Slice Architecture
依赖方向: Core ← Infrastructure ← Api

src/
  AIShop.Api/              # Minimal API 端点 + Agent 定义
  AIShop.Core/             # 实体、接口、领域逻辑 (零依赖)
  AIShop.Infrastructure/   # EF Core、仓储、外部服务实现
tests/
  AIShop.Api.Tests/        # xUnit 集成/单元测试
```

## 技术栈

| 类别 | 版本 | 用途 |
|------|------|------|
| .NET | 10.0 | 运行时 |
| Microsoft Agent Framework | 1.10.0 | AI Agent 编排 |
| EF Core | 10.0.9 | ORM（SQLite 开发 / PostgreSQL 生产） |
| Serilog | 10.x | 结构化日志 |
| xUnit | 2.9.3 | 单元测试 |
| SonarAnalyzer | 10.* | 代码质量门禁 |

## 前置条件

```bash
# 检查 SDK 版本
dotnet --version              # 必须 >= 10.0
dotnet --list-sdks

# 如需安装：https://dotnet.microsoft.com/download/dotnet/10.0
```

## 常用命令

```bash
# ── 构建 ──────────────────────────────────────
dotnet build                  # 完整解决方案构建
dotnet build src/AIShop.Api   # 单个项目构建
dotnet build -c Release       # Release 配置

# ── 运行 ──────────────────────────────────────
dotnet run --project src/AIShop.Api
# 默认监听 http://localhost:5206，自动打开浏览器

# ── 测试 ──────────────────────────────────────
dotnet test                   # 运行所有测试
dotnet test --no-build        # 仅运行，不构建
dotnet test --filter "Category=Integration"

# ── 代码质量 ──────────────────────────────────
dotnet build /warnaserror     # 警告即错误（已在 Directory.Build.props 启用）
# SonarAnalyzer 规则在 IDE 中实时生效，构建时也会检查

# ── NuGet ─────────────────────────────────────
dotnet restore                # 还原包
dotnet list package           # 查看所有包引用
dotnet list package --vulnerable --deprecated  # 安全检查
dotnet outdated               # 需安装: dotnet tool install -g dotnet-outdated-tool

# ── 数据库 ────────────────────────────────────
# SQLite 开发：自动通过 EnsureCreated() 初始化
# EF 迁移（如需）：
dotnet ef migrations add Init --project src/AIShop.Infrastructure
dotnet ef database update --project src/AIShop.Api

# ── 清理 ──────────────────────────────────────
dotnet clean
dotnet clean -c Release
```

## 代码规范

### 全局设置（`Directory.Build.props`）

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<!-- SonarAnalyzer.CSharp 10.* 已全局引用 -->
```

### 命名约定

| 元素 | 规范 | 示例 |
|------|------|------|
| 命名空间 | `PascalCase.分层` | `AIShop.Features.Chat` |
| 类/方法 | PascalCase | `ChatEndpoints`, `MapChatEndpoints()` |
| 参数/变量 | camelCase | `chatClient`, `sessionId` |
| 接口 | `I` 前缀 | `IUserRepository` |
| 私有字段 | `_camelCase` | `_logger`, _dbContext` |
| 文件系统 | 文件名 = 类型名 | `ChatEndpoints.cs` |

### 架构规则

1. **Core 零依赖**：`AIShop.Core` 不引用任何其他项目，仅引用 `Microsoft.Agents.Core`
2. **垂直切片**：新增功能在 `Api/Features/{功能名}/` 下创建切片目录
3. **端点模式**：扩展方法 `static void Map{功能名}Endpoints(this WebApplication app)`
4. **Agent 分离**：Agent 定义在 `Api/Agents/`，端点逻辑在 `Features/`
5. **DI 批量注册**：Infrastructure 通过 `AddInfrastructure()` 扩展方法统一注册
6. **MAF 技能参考**：涉及 MAF API 时，先调用 `/maf-reference` 技能

### 异步规范

- 所有 I/O 操作使用 `async`/`await`
- Controller/Endpoint handler 返回 `Task<*>`
- 避免 `.Result` 或 `.Wait()` — 使用 `await`

### NuGet 规范

- 包版本集中管理（考虑引入 `Directory.Packages.props`）
- 临时项目（如 `MafCheck`, `OpenAICheck`, `temp-maf`）不从解决方案引用
- 测试项目用 `coverlet.collector` 收集覆盖率

## Git 工作流

```
main ── 生产就绪，只合入 PR
  │
  ├── feature/xxx ── 功能开发
  ├── fix/xxx      ── 缺陷修复
  └── refactor/xxx ── 重构
```

```bash
git checkout -b feature/xxx   # 从 main 新建功能分支
# ... 开发 & 提交 ...
dotnet build                  # 确保构建通过
dotnet test                   # 确保测试通过
git push origin feature/xxx   # 推送 + 创建 PR
```

## 专用 Agent 角色

你可以通过以下关键词唤醒对应的 Agent 角色：

### 架构师Agent
在 prompt 中包含 `[arch-agent]` 或描述架构/设计任务即可激活：
- 系统架构设计、技术选型、设计文档编写
- 设计输出以文档为主（`docs/design/`、`docs/api/`、`docs/adr/`）
- 开发任务拆分后创建 PR
- **设计冻结后**，通知开发Agent 介入

### 开发Agent
在 prompt 中包含 `[dev-agent]` 或描述开发任务即可激活：
- 实现功能、写 C#/MAF 代码、创建端点、搭建垂直切片
- 开发过程中有分歧先和架构师Agent 讨论，然后和客户确定之后再改代码
- 写 MAF 代码前自动调用 `/maf-reference` 技能获取准确 API 参考和代码模板
- **需要架构师Agent 的设计文档就绪后再开始实现**
- 完成后运行 `dotnet build` 验证编译

### 测试Agent
在 prompt 中包含 `[test-agent]` 或描述测试任务即可激活：
- 编写 xUnit 测试、集成测试、Mock 依赖、修复测试失败
- 使用 `WebApplicationFactory<Program>` 做 API 集成测试
- Mock 外部 AI 服务，不调用真实 LLM
- **开发Agent 完成功能后介入检查**
- 完成后运行 `dotnet test` 验证

### 工作流程

```
架构师Agent（设计文档）→ 开发Agent（代码实现）→ 测试Agent（测试验证）
```

三个阶段顺序执行，前一阶段冻结后才进入下一阶段。

## 开发历程最佳实践 

参考 `docs/agent-journey-best-practices.md`，遵循以下原则：
- **渐进式复杂度**：从最简单的模式开始，仅在需要时增加复杂度
- **智能光谱**：在完全自主与完全确定之间选择合适的平衡点
- **Temperature 设置**：Agent 应用 `Temperature = 0.2f`，创意任务用 0.7–1.0

## MAF 参考文件使用规则

当 `/maf-reference` 技能被调用并从 `references/` 读取参考文件时：
- **优先使用不带 `.zh-CN` 后缀的版本**（英文原版）
- 如果某个文件只有 `.zh-CN` 版本而没有英文原版，则使用 `.zh-CN` 版本

> 任何 MAF 相关问题可参考 [官方文档](https://learn.microsoft.com/zh-cn/agent-framework/)

## 快速开始

```bash
# 首次运行
git clone <repo>
cd AIShop
dotnet restore
dotnet build
dotnet run --project src/AIShop.Api

# 验证
curl -X POST http://localhost:5206/api/chat \
  -H "Content-Type: application/json" \
  -d '{"username":"marla","message":"你好"}'
```

## Skill 路由规则

当用户的请求匹配到可用技能时，通过 Skill 工具调用它。不确定时也优先调用技能。

关键路由规则：
- MAF/Agent 开发、写 Agent 代码/工作流 → 调用 /maf-reference
- Agent 相关 Bug 排查/DI 注册失败 → 调用 /maf-reference
- 产品创意/头脑风暴 → 调用 /office-hours
- 战略/范围界定 → 调用 /plan-ceo-review
- 架构设计 → 调用 /plan-eng-review
- 设计系统/方案评审 → 调用 /design-consultation 或 /plan-design-review
- 全流程评审 → 调用 /autoplan
- Bug/错误排查 → 调用 /investigate
- QA/网站测试 → 调用 /qa 或 /qa-only
- 代码审查/diff 检查 → 调用 /review
- 视觉评审 → 调用 /design-review
- 发布/部署/提 PR → 调用 /ship 或 /land-and-deploy
- 保存进度 → 调用 /context-save
- 恢复上下文 → 调用 /context-restore
- 编写 backlog-ready 的 issue/spec → 调用 /spec


