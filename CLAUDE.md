# AIShop — 工程化开发指南

## 项目信息

- **解决方案**：`AIShop.sln`
- **目标框架**：`net10.0` | **语言**：C# 14 | **SDK**：.NET 10

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

## 常用命令

```bash
dotnet build                  # 完整解决方案构建
dotnet run --project src/AIShop.Api  # 运行（默认 http://localhost:5206）
dotnet test                   # 运行所有测试
dotnet build /warnaserror     # 警告即错误
dotnet restore                # 还原包
openspec new change {id}      # 创建 OpenSpec 变更
openspec archive {id}         # 归档变更
```

## 开发流程

当前项目使用 OpenSpec 流程进行变更管理。入口：

- **启动变更**：`/openspec-workflow`
- **OpenSpec 命令**：`/opsx:new`、`/opsx:apply`、`/opsx:verify`、`/opsx:archive`

详细规范请参考：
- 代码规范 → `.claude/rules/dotnet.md`（含命名、架构、异步、测试规范）
- 技能路由 → `.claude/rules/skill-routing.md`

## Git 工作流

```
main ── 生产就绪，只合入 PR
  ├── feature/xxx ── 功能开发
  ├── fix/xxx      ── 缺陷修复
  └── refactor/xxx ── 重构
```

```bash
git checkout -b feature/xxx
dotnet build && dotnet test
git push origin feature/xxx   # 创建 PR
```

## MAF 参考文件使用规则

当 `/maf-reference` 技能被调用时：
- 优先使用不带 `.zh-CN` 后缀的版本（英文原版）
- 如果某个文件只有 `.zh-CN` 版本则使用 `.zh-CN` 版本
- 官方文档：https://learn.microsoft.com/zh-cn/agent-framework/

## Agent skills

### Issue tracker

Issues tracked in GitHub Issues. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## 快速开始

```bash
git clone <repo> && cd AIShop
dotnet restore && dotnet build
dotnet run --project src/AIShop.Api
curl -X POST http://localhost:5206/api/chat -H "Content-Type: application/json" -d '{"username":"marla","message":"你好"}'
```
