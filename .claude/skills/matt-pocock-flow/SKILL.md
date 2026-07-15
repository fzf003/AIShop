---
name: matt-pocock-flow
description: >
  Matt Pocock AI 辅助开发工作流：grilling → spec → tickets → implement → code-review → QA测试 → test-report → archive。
  仅手工触发，不自动建议。用户输入 /matt-pocock-flow 时启动。
disable-model-invocation: true
argument-hint: [功能描述]
---

# Matt Pocock AI 辅助开发工作流

收到请求后，先检查 openspec/.current-change 判断当前变更状态，然后从当前步骤继续或启动 Step 1。

所有产出物放在 openspec/changes/{change-name}/ 下，结构如下：

```
openspec/changes/{change-name}/
├── design.md
├── specs/{domain}/spec.md
├── tasks.md（含 blocking edges）
├── test-report.md
└── handoffs/
    ├── handoff-A.md
    ├── handoff-B.md
    └── ...
```

归档时直接运行 openspec archive，handoffs/ 会一并带入 archive 目录。

## 流程总览

每次向用户汇报进度时，使用下面这张状态表，实时反映当前变更的真实进展。表格里的状态必须如实对应文件系统的实际情况。

```
+------------------------------------------+--------------------------------------+
|                  步骤                    |                状态                 |
+------------------------------------------+--------------------------------------+
| Step 1 Grill（想法打磨）                 |  /  /                              |
+------------------------------------------+--------------------------------------+
| Step 2 Spec（规范文档）                  |  design.md + spec.md                |
+------------------------------------------+--------------------------------------+
| Step 3 Tickets（拆工单+blocking edges）   |  tasks.md（全部 [x]）               |
+------------------------------------------+--------------------------------------+
| Step 4 Implement（执行）                  |  代码已实现，编译0错误              |
+------------------------------------------+--------------------------------------+
| Step 5 Code Review（审查）                |  审查通过，无遗留问题                |
+------------------------------------------+--------------------------------------+
| Step 6 QA 测试                           |  未执行 / 单元测试 / 自测 / UI     |
+------------------------------------------+--------------------------------------+
| Step 7 Test Report（测试报告）            |  test-report.md                     |
+------------------------------------------+--------------------------------------+
| Step 8 Archive（归档）                    |  未执行（需显式指令）                |
+------------------------------------------+--------------------------------------+
```

状态符号约定：
-  已完成
-  进行中或部分完成
-  未执行

Archive 行永远不会因为前 7 步完成而自动变成  ，只在用户确认归档后才更新。

---

## 核心理念

**每个工单一个独立 session**。Agent 只看到一个工单，无法跳过/合并。
Blocking edges 声明工单依赖，开发者定义依赖，AI 不替人判断。
测试三阶段：单元测试 - 自测（全链路）- UI 测试（浏览器），通过后出报告再归档。

---

## Step 1: Grill（想法打磨）

启动 /grilling 或用对话方式明确需求。

**产出**：清晰的功能描述和范围界定。

**目录**：openspec/changes/{change-name}/

可选：如果已有明确需求，可直接进入 Step 2。

---

## Step 2: Spec（规范文档）

产出两个文档到 openspec/changes/{change-name}/：

- design.md - 技术设计方案（改动描述、涉及文件、不做范围）
- spec.md - 增量规范（验收标准、接口契约、交互规格）

**验收**：开发和技术评审人员能根据 spec 独立判断做完了没有。

---

## Step 3: Tickets（拆工单 + Blocking Edges）

将功能拆成可独立交付的工单。每个工单都是端到端可交付的最小切片，不是功能分解。

产出一个 tasks.md，包含每个工单的 blocking edges、涉及文件、描述。

**blocking edges 规则**：

| 规则 | 说明 |
|------|------|
| 无依赖 | 可最先执行，可与其他无依赖工单并行 |
| 依赖 A | A 完成后才能开始 |
| 同时依赖 A 和 B | A 和 B 都完成才能开始 |
| 修改同一文件 | 不能并行，除非改动完全不重叠 |

依赖是开发者定义的，AI 可以建议但最终由人判断。

---

## Step 4: Implement（执行）

### 4.0 实现前确认规格

Agent 在实现前必须：
- 读 design.md 和 handoff 中的设计决策
- 如果是前端 UI，确认交互细节（按钮样式、位置、状态变化、文字/图标）
- 如果是 Agent 指令，确认不需要注入完整数据集
- 一句话向开发者确认理解后再开始编码

### 4.1 编写 Workflow 脚本

创建一个 .claude/workflows/{change-name}.js 脚本，模板见：
.claude/skills/matt-pocock-flow/references/workflow-template.js

### 4.2 每个 Agent 的执行规则

每个 agent 在独立 session 中执行一个工单：

1. 读 design.md 和 spec.md 了解上下文
2. 实现工单描述的改动
3. 运行 dotnet build 确认编译通过
4. 运行 dotnet test 确认所有测试通过
5. git commit（feat/fix 前缀 + 工单 ID）
6. 生成 handoff 文件（openspec/changes/{change-name}/handoffs/handoff-{id}.md）
7. 在 tasks.md 中标记为 [x]

### 4.3 Agent 指令设计原则

- Agent 指令保持精简：只给推理规则，不给原始数据集
- 如果 Agent 需要查询数据（商品目录、用户列表、订单等），定义为工具（Tool）让 Agent 按需调用
- system prompt 超过 4K tokens 时检查是否可以精简或拆分为工具调用

### 4.4 运行时发现问题的处理

| 场景 | 处理 |
|------|------|
| 发现依赖工单有小 bug | 在当前 session 直接修，不做大重构 |
| 发现依赖工单需大重构 | Agent 评估影响（文件数、行数、调用链），上报给开发者 |
| 工单做到一半远超预期 | Agent 暂停，/handoff 留住调研结果，回 Step 3 重拆 |
| 规格不清楚 | Agent 先评估影响范围，再主动提问。先问，不要猜 |

---

## Step 5: Code Review

审查本次变更的所有 diff：

1. dotnet build 0 错误 0 警告
2. 项目引用是否丢失
3. nullable 警告
4. unused import
5. Agent 调用的 try/catch 是否被误删

---

## Step 6: QA 测试

三种测试都必须通过：

### 6.1 单元测试

dotnet test（失败 0）
测试通过后检查：无残留 dotnet 进程、无残留测试临时文件

### 6.2 自测（全链路 API 验证）

curl 测试 /api/login、/api/chat、/api/recommendations、/api/products

### 6.3 UI 测试

使用 /qa 或 /qa-only skill 在浏览器中验证界面。

---

## Step 7: Test Report

产出 test-report.md 到 openspec/changes/{change-name}/：
- 测试概要
- 每个功能的验收结果
- 修复记录
- 健康评分
- 截图
- 遗留建议

---

## Step 8: Archive（归档）

直接运行 openspec archive {change-name} --skip-specs --yes
openspec 会自动将 openspec/changes/{change-name}/ 归档到 openspec/archive/，handoffs/ 目录一并带入。

---

## 与 OpenSpec 共存

完全兼容。产出物直接放在 openspec/changes/{change-name}/ 目录下，结构只比 OpenSpec 多一个 handoffs/ 目录：

| 环节 | 使用方式 |
|------|---------|
| 变更创建 | openspec new change {change-name} |
| design.md | openspec/changes/{change-name}/design.md |
| spec.md | openspec/changes/{change-name}/specs/{domain}/spec.md |
| tasks.md | 保留 OpenSpec 格式，加 blocking edges |
| handoffs | openspec/changes/{change-name}/handoffs/（OpenSpec 没有，但不冲突）|
| test-report.md | openspec/changes/{change-name}/test-report.md |
| 归档 | openspec archive {change-name} --skip-specs --yes |

---

## 关键原则

1. 开发者定义依赖，AI 提建议
2. 每个工单独立 session，Agent 无法跳过。即使多个工单改同一文件，也各自独立 session + 各自 commit，不做"攒一批再提交"
3. 运行时 bug：Agent 评估影响，人决策
4. Agent 先问不要猜。规格不清楚时先评估影响范围再提问，不问就代表理解无误
5. 测试三阶段不可跳过。单元测试通过后检查进程和临时文件残留
6. Handoff 是必须的
7. 小 bug 直接修，大重构切 session，做太大暂停重拆
8. 状态同步：tasks.md checkbox、test-report.md 验收标准，修改即同步，不拖到归档才补
