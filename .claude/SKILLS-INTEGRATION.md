# AIShop 技能增强说明

## 🎯 现有技能整合

你的项目已安装以下强大技能，现已与 Agent 工作流深度集成：

### 1️⃣ OpenSpec 变更管理
**相关技能**: `openspec-propose`, `openspec-apply-change`, `openspec-verify-change`

- ✅ **架构师**: 调用 `openspec-propose` 创建完整设计变更
  ```
  openspec/changes/{id}/
  ├─ design.md (设计文档)
  ├─ spec.md   (功能规范)
  └─ tasks.md  (开发任务)
  ```

- ✅ **开发者**: 调用 `openspec-apply-change` 开始实现并自动追踪任务
  - 运行命令: 标记任务为进行中
  - 完成功能: 标记任务为完成
  - 编译验证: 自动关联代码位置

- ✅ **测试者**: 调用 `openspec-apply-change` 追踪测试进度
  - 测试完成: 更新 OpenSpec 状态为 `verified`
  - 附加报告: 测试覆盖率、安全扫描结果

### 2️⃣ GStack 自动化 QA + 修复
**相关技能**: `gstack-qa`, `gstack-qa-only`, `code-review`, `gstack-review`

- ✅ **测试者**: 调用 `gstack-qa` 进行端到端测试
  ```
  工作流:
  1. 系统自动测试应用每个功能
  2. 发现 bug: 自动修复 → 重新测试验证
  3. 生成报告: bug 修复汇总、测试覆盖率
  ```

- ✅ **代码审查**: 调用 `code-review` 或 `gstack-review`
  ```
  检查重点:
  - SQL 安全 (SQL injection 预防)
  - LLM 信任边界 (不泄露 secrets)
  - 并发安全 (race conditions)
  - 集成点脆弱性
  - 数据访问层安全
  ```

### 3️⃣ Aspire 分布式应用
**相关技能**: `aspire`, `aspire-deployment`, `aspireify`

- ✅ **架构师**: 设计分布式系统时调用 `aspire` 技能
  ```
  用途:
  - 设计 AppHost 资源编排
  - 选择分布式通讯方案
  - 规划服务间集成
  ```

- ✅ **部署阶段** (可选): 调用 `aspire-deployment` 生成部署工件
  ```
  支持生成:
  - Docker Compose (本地开发)
  - Kubernetes 清单 (K8s 部署)
  - Azure Container Apps 配置
  - 健康检查和监控配置
  ```

### 4️⃣ 安全扫描
**相关技能**: `security-scan`

- ✅ **测试者**: 调用 `security-scan` 进行深度安全审计
  ```
  扫描范围:
  - 依赖漏洞 (NuGet packages CVE)
  - Secrets 检测 (硬编码密钥)
  - OWASP Top 10 代码模式
  - 授权配置 (JWT, CORS)
  - 数据保护
  ```

### 5️⃣ 完整验证管道
**相关技能**: `verify`

- ✅ **开发者** + **测试者**: 调用 `verify` 运行 7 阶段检查
  ```
  7 个阶段 (auto-stop on failure):
  1. 构建 (dotnet build)
  2. 分析器 (Roslyn / SonarAnalyzer)
  3. 反模式检测 (不安全的 patterns)
  4. 测试运行 (xUnit)
  5. 安全基线扫描
  6. 代码格式化检查
  7. Diff 审查 (变更影响分析)
  ```

## 📊 工作流增强矩阵

### 架构师Agent 现在可以做

```
旧: 写设计文档 + 手工拆分任务
新: 
  1. brainstorming (澄清需求)
  2. spec (转化为规范)
  3. plan (生成实现计划)
  4. openspec-propose (创建变更 + 任务清单)
     ↓ 变更包含 design.md + spec.md + tasks.md
  5. 交付给开发 Agent (自动路由)
```

### 开发Agent 现在可以做

```
旧: 按设计文档手工编码 + 编译验证
新:
  1. openspec-apply-change (启动任务追踪)
  2. scaffold (自动生成切片骨架)
  3. tdd (测试驱动实现)
  4. verify (7 阶段完整检查)
     ├─ ✅ 编译无警告
     ├─ ✅ 分析器检查通过
     ├─ ✅ 反模式检测通过
     ├─ ✅ 单元测试通过
     ├─ ✅ 安全基线通过
     ├─ ✅ 格式化无误
     └─ ✅ Diff 审查通过
  5. 交付给测试 Agent (自动标记 ready-for-testing)
```

### 测试Agent 现在可以做

```
旧: 手工编写测试 + 手工修复 bug
新:
  1. openspec-apply-change (启动测试追踪)
  2. testing (编写 xUnit 集成测试)
  3. gstack-qa (自动 QA 测试 + 自动修复)
     ├─ 系统自动找 bug
     ├─ 自动修复代码
     ├─ 重新测试验证
     └─ 生成报告
  4. security-scan (深度安全扫描)
     ├─ 依赖漏洞
     ├─ Secrets 检测
     ├─ OWASP patterns
     ├─ JWT/CORS 配置
     └─ 数据保护
  5. verify (完整管道再过一遍)
  6. 交付给部署 Agent (自动标记 verified)
```

## 🚀 使用场景示例

### 场景1：快速迭代新功能
```
用户: [arch-agent] 设计商品搜索功能，要支持 AI 排序

架构师:
  ✓ brainstorming: 澄清需求
  ✓ technology-selection: 选择 AI 技术 (embedding / reranker)
  ✓ spec: 生成规范
  ✓ openspec-propose: 创建变更 + 任务
  ✓ 交付给开发

开发者:
  ✓ openspec-apply-change: 启动任务追踪
  ✓ scaffold: 自动生成切片
  ✓ tdd: 测试驱动实现
  ✓ verify: 完整检查 (7 阶段)
  ✓ 交付给测试

测试者:
  ✓ openspec-apply-change: 启动测试追踪
  ✓ testing: 编写集成测试
  ✓ gstack-qa: 自动 QA + 修复
  ✓ security-scan: 安全检查
  ✓ verify: 最终验收
  ✓ 标记 verified
```

### 场景2：应急 Bug 修复
```
用户: [dev-agent] 紧急修复聊天 API 崩溃问题

开发者:
  ✓ gstack-investigate: 诊断根本原因
  ✓ tdd: 写一个失败的测试重现 bug
  ✓ 修复代码使测试通过
  ✓ verify: 确保没有回归

测试者:
  ✓ gstack-qa: 验证修复有效
  ✓ 无需安全扫描 (只是 bug 修复)
```

### 场景3：分布式系统设计
```
用户: [arch-agent] 设计订单处理系统，要异步处理 + 事件驱动

架构师:
  ✓ brainstorming: 需求澄清
  ✓ technology-selection: 选 Wolverine / MassTransit
  ✓ messaging: 设计发布-订阅拓扑
  ✓ aspire: 规划 AppHost 资源编排
  ✓ openspec-propose: 创建完整变更
  ✓ 交付给开发

(后续同样开发 → 测试流程)

部署阶段 (可选):
  ✓ aspire-deployment: 生成 K8s 清单
  ✓ 配置事件总线、队列、监控
```

## ⚡ 新能力对比

| 任务 | 旧方式 | 新方式 | 效率提升 |
|------|--------|--------|---------|
| 设计转规范 | 手工写 | openspec-propose 自动生成 | **5 倍** |
| 代码生成 | 从零手写 | scaffold 自动生成切片 | **3 倍** |
| QA 测试 | 手工测试 + 手工修复 | gstack-qa 自动测试 + 修复 | **10 倍** |
| 安全扫描 | 部分检查 | security-scan 完整深度扫描 | **2 倍** |
| 部署工件 | 手工写 Dockerfile/K8s | aspire-deployment 自动生成 | **4 倍** |
| 验证门禁 | 分散运行检查 | verify 7 阶段集中管道 | **3 倍** |

## 🎓 学习路径

1. **快速入门** (5分钟)
   - 读 [AGENTS-README.md](.claude/AGENTS-README.md)
   - 用 `[arch-agent]` 设计一个小功能

2. **深入理解** (15分钟)
   - 读本文件完整内容
   - 读 [WORKFLOW.md](.claude/WORKFLOW.md) 详细流程

3. **高级应用** (30分钟)
   - 尝试 `gstack-qa` 自动 QA
   - 尝试 `security-scan` 安全扫描
   - 尝试 `aspire-deployment` 生成部署配置

## 📚 参考文档

- **工作流指南**: [.claude/WORKFLOW.md](.claude/WORKFLOW.md)
- **快速开始**: [.claude/AGENTS-README.md](.claude/AGENTS-README.md)
- **配置文件**: [.claude/settings.json](.claude/settings.json)
- **Agent 定义**: [.claude/agents/](.claude/agents/)

## 💡 提示

- 所有 OpenSpec 变更自动保存在 `openspec/changes/` 目录
- 设计、开发、测试三个 Agent 会自动追踪 OpenSpec 状态
- 调用任何技能前，先读一下对应的 skill 文件（位置在底部引用）
- 如遇卡顿，使用 `verify` 技能进行完整诊断
