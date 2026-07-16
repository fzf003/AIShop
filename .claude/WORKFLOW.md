# AIShop 多 Agent 工作流程 — 增强版

## 🚀 四阶段超级工作流

```
┌─────────────────────────────────────────────────────────────────────────┐
│                                                                         │
│  架构师Agent          开发Agent           测试Agent       (可选)部署     │
│  (设计 + OpenSpec)  (代码 + OpenSpec) (QA + 安全)      (Aspire)       │
│                                                                         │
│  1. brainstorming  2. openspec-apply  3. gstack-qa   4. aspire      │
│  2. spec           3. scaffold         4. security-scan  5. deploy   │
│  3. plan           4. tdd              5. code-review    6. health   │
│  4. openspec-propose 5. verify         6. verify                     │
│  5. 交付 OpenSpec 变更                                                │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
   ↓ OpenSpec ready-for-implementation   ↓ ready-for-testing   ↓ verified
```

## 📋 工作流解析

### 第1阶段：设计阶段（架构师Agent）

**触发条件**
- 用户输入新功能需求（含或不含细节）
- 用 `[arch-agent]` 显式触发

**工作步骤**

```
Step 1: 澄清需求
├─ 调用 brainstorming 技能
├─ 确认业务目标、性能要求、集成点
└─ 输出：需求澄清文档

Step 2: 技术选型
├─ 调用 architecture-advisor (选架构模式)
├─ 调用 technology-selection (选 AI/ML 技术)
├─ 调用 messaging (选异步通讯方案)
└─ 输出：技术决策

Step 3: 创建 OpenSpec 变更
├─ 调用 spec 技能生成规范
├─ 调用 plan 技能生成实现计划
├─ 调用 openspec-propose 创建完整变更
│  ├─ design.md (架构和技术方案)
│  ├─ spec.md (功能规范和验收标准)
│  └─ tasks.md (拆分任务清单)
└─ 输出：OpenSpec 变更 + 文档

Step 4: 方案锁定
├─ 与用户/团队确认设计
├─ 更新 ADR (如需要)
└─ 标记 OpenSpec 状态: ready-for-implementation
```

**交付物**
```
openspec/changes/{变更ID}/
├─ design.md          # 架构设计、技术决策、数据模型
├─ spec.md            # 功能规范、API 契约、验收标准
└─ tasks.md           # 开发任务清单（给开发Agent）

docs/design/
├─ {功能名}-design.md # 详细设计文档
└─ {功能名}-api.md   # API 规范

docs/adr/
└─ ADR-{序号}.md      # 架构决策记录
```

### 第2阶段：开发阶段（开发Agent）

**触发条件**
- 收到来自架构师Agent的 OpenSpec 变更（状态：ready-for-implementation）
- 用 `[dev-agent]` 显式触发 + OpenSpec 链接

**工作步骤**

```
Step 1: 启动 OpenSpec 追踪
├─ 调用 openspec-apply-change 技能
├─ 读取 design.md 和 tasks.md
└─ 标记任务开始状态

Step 2: 自动化代码生成（可选）
├─ 调用 scaffold 技能自动生成垂直切片
├─ 或手工编写关键组件
└─ 输出：初始项目结构

Step 3: TDD 编写测试
├─ 调用 tdd 技能
├─ 编写失败的测试 (Red)
├─ 实现功能使测试通过 (Green)
└─ 重构代码优化设计 (Refactor)

Step 4: 端点和服务实现
├─ 创建 {功能}Endpoints.cs
├─ 实现业务逻辑 Service
├─ 配置依赖注入
├─ 集成 MAF (如需要)
└─ 使用 maf-reference 技能获取准确 API

Step 5: 编译验证
├─ 调用 build-fix 技能进行自动修复循环
├─ 或手工运行 dotnet build
├─ 确保编译无警告
└─ 更新 OpenSpec: 标记任务完成

Step 6: 完整验证管道
├─ 调用 verify 技能运行 7 阶段检查
│  ├─ 阶段1: 构建
│  ├─ 阶段2: 分析器
│  ├─ 阶段3: 反模式检测
│  ├─ 阶段4: 测试
│  ├─ 阶段5: 安全基线
│  ├─ 阶段6: 格式化
│  └─ 阶段7: Diff 审查
├─ 所有检查通过
└─ 标记 OpenSpec 状态: ready-for-testing

Step 7: 交付给测试Agent
├─ 更新 OpenSpec: 标记里程碑 ready-for-testing
├─ 通知测试Agent
└─ 保持待命修复问题
```

**交付物**
```
src/AIShop.Api/
└─ Features/{功能}/
   ├─ {功能}Endpoints.cs      # API 端点
   ├─ {功能}Service.cs        # 业务逻辑
   ├─ Create{功能}Command.cs  # DTO 命令
   ├─ {功能}Response.cs       # DTO 响应
   └─ (其他相关实现)

tests/AIShop.Api.Tests/
└─ {功能}Tests.cs            # 集成测试骨架

openspec/changes/{id}/
└─ (updated with code links & implementation notes)
```

### 第3阶段：测试阶段（测试Agent）

**触发条件**
- 收到来自开发Agent的通知（OpenSpec 状态：ready-for-testing）
- 用 `[test-agent]` 显式触发

**工作步骤**

```
Step 1: 启动 OpenSpec 追踪
├─ 调用 openspec-apply-change 技能追踪测试任务
├─ 读取 spec.md 和代码实现
└─ 标记测试阶段开始

Step 2: 编写完整测试套件
├─ 调用 testing 技能
├─ 正常场景测试
├─ 异常场景测试
├─ 边界情况测试
├─ 集成测试 (WebApplicationFactory)
└─ Mock 所有外部依赖

Step 3: 运行单元测试
├─ dotnet test
├─ 所有测试通过
└─ 检查测试覆盖率 (>80%)

Step 4: 自动化端到端 QA
├─ 调用 gstack-qa 技能进行自动化测试
├─ 系统自动修复找到的 bug
├─ 重新运行测试验证修复
└─ 生成 QA 报告

Step 5: 安全扫描
├─ 调用 security-scan 技能
├─ 检查 OWASP Top 10
├─ 扫描 secrets 和硬编码
├─ 评估依赖安全性
└─ 标记严重级别

Step 6: 完整验证管道
├─ 调用 verify 技能再次运行完整检查
├─ 确保没有回归
└─ 最终通过门禁

Step 7: 代码审查
├─ 调用 code-review 技能
├─ blast radius 优先
├─ 生成审查意见
└─ 必要时与开发 Agent 沟通修复

Step 8: 完成并交付
├─ 更新 OpenSpec: 标记状态 verified
├─ 附上完整的测试报告、安全扫描结果
├─ 记录所有已修复的 bug
└─ 可选：通知部署 Agent 进入部署阶段
```

**交付物**
```
tests/AIShop.Api.Tests/
├─ {功能}Tests.cs            # 完整集成测试
├─ {功能}FixtureTests.cs     # Fixture 和 Mock
└─ (覆盖率报告)

openspec/changes/{id}/
├─ 测试覆盖率报告
├─ 安全扫描结果
├─ QA bug 修复汇总
└─ 状态: verified ✅
```

### 第4阶段：部署阶段（可选，架构师Agent）

**触发条件**
- OpenSpec 状态为 verified
- 用户选择进入部署阶段
- 用 `[arch-agent] 部署 {功能}` 触发

**工作步骤**

```
Step 1: AppHost 配置
├─ 调用 aspire 技能
├─ 读取 Aspire AppHost 架构
├─ 添加新资源到 AppHost
└─ 配置资源间通讯

Step 2: 编排编排
├─ 使用 AddViteApp / AddNextJsApp (前端)
├─ 使用 AddAzureServiceBus / AddRabbit (消息)
├─ 使用 AddPostgres / AddRedis (存储)
└─ 配置 WithEnvironment 环境变量

Step 3: 容器化编排
├─ 调用 container-publish 或 docker 技能
├─ 生成 Dockerfile 或 MSBuild 配置
├─ 配置多架构构建
└─ 设置健康检查

Step 4: 部署工件生成
├─ 调用 aspire-deployment 技能
├─ 生成 Docker Compose (本地测试)
├─ 生成 Kubernetes 清单 (生产)
├─ 生成 Azure Container Apps 配置
└─ 生成部署文档

Step 5: 验证部署
├─ 本地测试 (aspire start)
├─ 运行健康检查
├─ 验证资源间通讯
└─ 性能基线测试

Step 6: 完成并记录
├─ 更新 OpenSpec: 标记状态 deployed
├─ 生成部署文档到 docs/deployment/
└─ 记录部署配置
```

**交付物**
```
src/AIShop.AppHost/
├─ Program.cs (updated with new resource)

docker-compose.yml / kubernetes/ / azure-deploy/
└─ 部署配置文件

docs/deployment/
└─ 部署指南

openspec/changes/{id}/
└─ 状态: deployed ✅
```

## 🔧 技能编排矩阵

| 阶段 | Agent | 核心技能 | 辅助技能 |
|------|--------|--------|----------|
| **设计** | 架构师 | brainstorming, spec, plan, openspec-propose | architecture-advisor, technology-selection, messaging, aspire |
| **开发** | 开发者 | openspec-apply-change, scaffold, tdd, verify | maf-reference, build-fix, ef-core, minimal-api, dependency-injection |
| **测试** | 测试者 | testing, gstack-qa, security-scan, verify | code-review, openspec-apply-change, build-fix |
| **部署** | 架构师 | aspire, aspire-deployment | container-publish, docker |

## 📌 快速命令参考

```bash
# 架构师：设计新功能
[arch-agent] 请设计{功能}功能
# 自动调用: brainstorming → spec → plan → openspec-propose

# 开发者：实现功能
[dev-agent] 根据 openspec/changes/{id}/ 实现{功能}
# 自动调用: openspec-apply-change → scaffold → tdd → verify

# 测试者：完成测试
[test-agent] 为{功能}编写完整测试并进行 QA
# 自动调用: testing → gstack-qa → security-scan → verify

# 部署：可选发布
[arch-agent] 使用 Aspire 部署{功能}到生产
# 自动调用: aspire-deployment → container-publish
```

## ✅ 工作流检查清单

### 设计阶段完成标志
- [ ] `openspec/changes/{id}/design.md` 存在且完整
- [ ] `openspec/changes/{id}/spec.md` 存在且包含验收标准
- [ ] `openspec/changes/{id}/tasks.md` 存在且任务清晰
- [ ] OpenSpec 状态标记为 `ready-for-implementation`
- [ ] 设计已与用户/团队确认

### 开发阶段完成标志
- [ ] `src/AIShop.Api/Features/{功能}/` 存在
- [ ] 所有端点实现完整
- [ ] 所有代码编译无警告
- [ ] 单元测试编写完整
- [ ] `dotnet build` 成功
- [ ] `verify` 技能 7 个阶段全部通过
- [ ] OpenSpec 状态标记为 `ready-for-testing`

### 测试阶段完成标志
- [ ] `tests/AIShop.Api.Tests/{功能}Tests.cs` 存在
- [ ] 所有集成测试通过
- [ ] 代码覆盖率 >80%
- [ ] `gstack-qa` 未找到 critical/high 严重 bug
- [ ] `security-scan` 无严重威胁
- [ ] 完整 `verify` 管道通过
- [ ] OpenSpec 状态标记为 `verified`

### 部署阶段完成标志（可选）
- [ ] AppHost 配置更新
- [ ] 部署工件生成（Docker/K8s/ACA）
- [ ] 本地部署测试通过
- [ ] 健康检查通过
- [ ] OpenSpec 状态标记为 `deployed`

## 🎯 最佳实践

### 设计阶段
1. **充分澄清** — 比后期改代码节省时间
2. **多方案对比** — 展示权衡和理由
3. **及早验证可行性** — 与开发 Agent 确认技术方案
4. **冻结设计** — 进入开发后不再改设计

### 开发阶段
1. **遵守设计** — 不要自作聪明改设计
2. **TDD 优先** — 测试驱动确保需求实现
3. **及时编译验证** — 不要堆积编译错误
4. **完整测试骨架** — 给测试 Agent 铺好路

### 测试阶段
1. **深度覆盖** — 正常/异常/边界都要测
2. **自动化优先** — 利用 gstack-qa 自动修复
3. **安全不可忽** — security-scan 必须运行
4. **及时反馈** — 发现问题立即通知开发 Agent

### 部署阶段（可选）
1. **基础设施即代码** — 所有配置入库
2. **健康检查必备** — 部署后验证系统
3. **文档同步** — 更新部署文档

## 🔗 相关文档

- [AGENTS-README.md](.claude/AGENTS-README.md) — 快速开始
- [AGENTS.md](../AGENTS.md) — 项目 Agent 指引
- [CLAUDE.md](../CLAUDE.md) — 技术栈和规范
- [.claude/settings.json](.claude/settings.json) — 工作流配置
