# 架构师Agent 工作流优化 — 设计文档

## 1. 概述

优化 AIShop 项目中架构师Agent 的工作流程，解决"流程切换靠手动"和"设计冻结后接力空档"两个核心痛点。引入 Workflow 编排 + 人工审批门禁 + OpenSpec 任务拆分，实现架构设计→人工审查→开发执行→测试验证的标准化流水线。

## 2. 架构决策

- **切片边界**：`架构师Agent工作流` — 跨越 .github/agents/、.workflows/、docs/design/、openspec/ 四个区域
- **依赖方向**：Workflow → 架构师Agent/开发Agent/测试Agent → OpenSpec CLI
- **涉及项目**：仅配置文件变更，不涉及 src/ 和 tests/ 代码
- **核心工具**：Workflow 工具 + OpenSpec CLI（`/opsx:propose` / `/opsx:apply` / `/opsx:archive`）

## 3. 数据流

```
用户发起需求
    │
    ▼
Workflow A (arch-design) 触发
    │
    ├─ 架构师Agent 读取需求
    ├─ 产出设计文档 → docs/design/{功能名}/v1.md
    ├─ git 分支 docs/{功能名}
    ├─ 提交设计文档 + 创建 Draft PR
    │
    ▼
═══ 人工门禁：用户审查设计文档 ═══
    │
    ├─ 不通过 → 架构师Agent 更新 → v2.md → 重新审查
    │
    └─ 通过 → 合并 PR → 用户确认"进入开发"
                      │
                      ▼
              Workflow B (dev-execute) 触发
                      │
                      ├─ 读取设计文档交接清单
                      ├─ /opsx:propose → 创建 change
                      │   ├─ proposal.md
                      │   ├─ specs/
                      │   ├─ design.md
                      │   └─ tasks.md (≤10 任务/迭代)
                      │
                      ├─ /opsx:apply → 逐迭代开发
                      │   ├─ 开发Agent 实现
                      │   ├─ 每任务 → dotnet build
                      │   ├─ 每迭代 → 通知用户检查
                      │   └─ 用户确认 → 下一迭代
                      │
                      ├─ 测试Agent 编写测试
                      ├─ dotnet test 通过
                      ├─ /opsx:verify → 验证一致性
                      └─ /opsx:archive → 归档 change
                      │
                      ▼
              通知"功能完成"
```

## 4. 设计文档模板

设计文档分为上下两部分：**上部给人看**（架构决策），**下部给开发Agent 看**（交接清单）。

```markdown
# {功能名} — 设计文档

## 1. 概述
{功能背景和业务目标，1-3句话}

## 2. 架构决策
- **切片边界**：{所属垂直切片名称}
- **依赖方向**：{调用的接口/服务}
- **涉及项目**：{Api / Core / Infrastructure}

## 3. 数据流
{从请求到响应的交互流程}

## 4. 数据模型
{新增或修改的实体定义}

## 5. API 定义
{端点列表：方法、路径、请求/响应模型、状态码}

## 6. 风险与注意事项
{已知风险、兼容性约束、待决策项}

---

## 7. 交接清单

### 7.1 涉及技术栈
- {影响的 NuGet 包 / MAF 组件 / 基础设施服务}
- {涉及 MAF 时标注需读取的参考文件}

### 7.2 需要实现/修改的文件清单

| 项目 | 文件路径 | 操作 | 说明 |
|------|---------|------|------|
| Core | `AIShop.Core/Entities/...` | 新增/修改 | ... |
| Core | `AIShop.Core/Interfaces/...` | 新增 | ... |
| Infra | `AIShop.Infrastructure/Services/...` | 新增 | ... |
| Api | `AIShop.Api/Features/{功能}/...` | 新增 | 切片目录 |
| Api | `AIShop.Api/Agents/...` | 新增/修改 | ... |

### 7.3 接口/端点清单

| 方法 | 路径 | 请求体 | 响应体 | 错误码 |
|------|------|--------|--------|--------|
| POST | /api/... | {Request} | {Response} | 400/404/... |

### 7.4 实体变更清单

| 实体 | 操作 | 字段变更 |
|------|------|---------|
| {EntityName} | 新增/修改 | {字段名、类型、说明} |

### 7.5 测试边界说明

- {哪些场景需要单元测试}
- {哪些端点需要集成测试}
- {需要 Mock 的外部依赖}
- {边界条件和异常路径}

### 7.6 迭代任务拆分

每次迭代 ≤10 个任务：

| # | 任务 | 预估 | 依赖 |
|---|------|------|------|
| 1 | {具体任务} | {S/M/L} | - |
| ... | ... | ... | ... |

---

## 8. 变更记录
| 版本 | 日期 | 作者 | 变更说明 |
|------|------|------|---------|
| v1 | YYYY-MM-DD | 架构师Agent | 初始版本 |
```

## 5. 版本管理

### 文件结构

```
docs/
  design/
    {功能名}/
      v1.md          ← 架构师Agent 初稿
      v2.md          ← 审查修改后（如需）
      latest.md      ← 指向当前版本的链接（方便开发Agent 定位）
```

### 版本规则

| 版本 | 时机 | 说明 |
|------|------|------|
| v1 | 架构师Agent 初稿 | 自动产出 |
| v2+ | 审查后要求修改 | 每轮递增，旧版本保留 |
| latest | 合并后 | 以此版本为准进入开发 |

### 分支策略

```
git checkout -b docs/{功能名}     ← 架构师Agent 创建分支
git add docs/design/{功能名}/     ← 提交设计文档
git commit -m "docs: {功能名} 设计文档 v{版本}"
                                   ← 创建 Draft PR
用户审查通过 → 合并到 main
```

## 6. Workflow 脚本设计

### 6.1 Workflow A：arch-design.wf.js

```
触发方式：用户输入"设计[功能名]"

phase("架构设计")
  架构师Agent 分析需求
  按模板产出设计文档 → docs/design/{功能名}/v1.md
  创建 git 分支 docs/{功能名}
  提交文档 + 创建 Draft PR
  log("设计文档已产出：[path]，请审查")
  // Workflow 在此结束
```

### 6.2 Workflow B：dev-execute.wf.js

```
触发方式：用户审查通过后输入"开发[功能名]"

phase("任务拆分")
  读取设计文档 latest
  /opsx:propose {功能名}
  → 生成 proposal.md + specs/ + design.md + tasks.md

phase("迭代执行")
  逐迭代执行 /opsx:apply：
    - 开发Agent 逐个完成任务
    - 每任务 dotnet build
    - 每迭代结束通知用户确认
    - 用户确认后继续

phase("测试验证")
  所有迭代完成后：
    测试Agent 编写 xUnit 测试
    dotnet test 通过
    /opsx:verify → 验证一致性
    /opsx:archive → 归档
```

### 6.3 文件位置

```
.workflows/
  arch-design.wf.js    ← Workflow A
  dev-execute.wf.js    ← Workflow B
```

## 7. OpenSpec 任务拆分规则

### 粒度标准

每个任务对应一次原子的代码变更：

| 类型 | 一个任务示例 | 预估 |
|------|-------------|------|
| 实体 | `定义 SearchRequest record` | S |
| 接口 | `定义 ISearchService` | S |
| 实现 | `实现 SearchService（EF Core 查询）` | M |
| 端点 | `实现 POST /api/products/search` | M |
| DI注册 | `注册 SearchService 到容器` | S |
| 测试 | `添加 SearchEndpoints 集成测试` | M |

### 迭代约束

- 每个迭代 **≤10 个任务**
- **按层分组**：先 Core → Infrastructure → Api（→ Tests）
- **顺序依赖**：后置任务依赖前置任务完成
- **每迭代可独立构建验证**

### 拆分工序

```
读取设计文档 7.6 节 → 手动拆分到 tasks.md
  ↓
/opsx:apply 逐任务执行
  ↓
每迭代完成 → 用户确认
```

## 8. 交接清单规范

设计文档 §7（交接清单）必须包含让开发Agent 零疑问启动的所有信息：

| 清单项 | 必须包含 | 给谁用 |
|--------|---------|--------|
| 技术栈 | NuGet 包、MAF 组件、参考文件 | 开发Agent |
| 文件清单 | 完整路径 + 操作类型 + 说明 | 开发Agent |
| 接口/端点 | 方法、路径、请求/响应 Schema、错误码 | 开发Agent |
| 实体变更 | 字段名、类型、约束 | 开发Agent |
| 测试边界 | 单元/集成/边界条件/Mock 清单 | 测试Agent |
| 任务拆分 | 编号 + 描述 + 预估 + 依赖 | 开发Agent |

## 9. OpenSpec 集成

### 生命周期

```
/opsx:propose {功能名}
  ├─ proposal.md    ← 从设计文档 §1-2 自动生成
  ├─ specs/         ← 从设计文档 §3-5 提取需求场景
  ├─ design.md      ← 引用 docs/design/{功能名}/latest.md
  └─ tasks.md       ← 从设计文档 §7.6 生成（≤10 任务/迭代）

/opsx:apply {功能名}
  → 逐任务实现（每任务 dotnet build）

/opsx:verify {功能名}
  → 验证实现 vs 设计一致性

/opsx:archive {功能名}
  → 归档到 openspec/changes/archive/
```

### config.yaml

```yaml
schema: spec-driven
context: |
  Tech stack: .NET 10, C# 14, EF Core 10, MAF 1.10, xUnit
  Architecture: Vertical Slice (Core → Infrastructure → Api)
  TreatWarningsAsErrors enabled
  MAF code must reference .codex/skills/maf-reference/references/*.md
rules:
  tasks:
    - Each iteration ≤ 10 tasks
    - Tasks must be atomically implementable (one file/class change per task)
    - Order: Core → Infrastructure → Api → Tests
```

## 10. 风险与注意事项

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| 设计文档过于简略导致开发Agent 无法执行 | 开发阻塞 | 交接清单必须包含完整接口签名和数据模型 |
| Workflow 脚本出错 | 流程中断 | 每个 Workflow 独立可重入 |
| OpenSpec CLI 未安装 | 任务拆分无法执行 | 在 Workflow B 前做前置检查 |
| 任务拆分过粗/过细 | 迭代管理困难 | 明确粒度标准：一个任务 = 一个文件变更 |
| 用户审查周期过长 | 流程闲置 | 设计文档控制在可 15 分钟内审完的篇幅 |

## 变更记录

| 版本 | 日期 | 作者 | 变更说明 |
|------|------|------|---------|
| v1 | 2026-06-17 | 架构师Agent | 初始版本 |
