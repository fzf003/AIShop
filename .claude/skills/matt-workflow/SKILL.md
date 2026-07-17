---
name: matt-workflow
description: >
  Matt Pocock AI 辅助开发工作流：grilling → spec → tickets → implement → code-review → QA测试 → test-report → archive。
  仅手工触发，不自动建议。用户输入 /matt-workflow 时启动。
disable-model-invocation: true
argument-hint: [功能描述]
---

# Matt Pocock AI 辅助开发工作流

收到请求后，先检查 openspec/.current-change 判断当前变更状态，然后从当前步骤继续或启动 Step 0。

## Step 0: 判定 flow-mode（新变更必做，续接已有变更跳过）

启动一个新变更时，先判断走哪条路径，并把结果写入 `openspec/changes/{change-name}/.flow-mode`
（这一步不受 hook 前置条件限制，可以直接写）：

| flow-mode | 适用场景 | 判定标准 |
|-----------|---------|---------|
| `quick` | 单文件或改动 < 20 行的 bugfix，不改变已有 spec.md 声明的验收标准，不涉及新接口 | 跳过 Step 1/2/3，直接进 Step 4；仍然强制 tasks.md（至少一条工单）+ implementer 委派 + commit 前 build/test 门禁 |
| `matt-pocock` | 常规新功能/变更，走完整 8 步 | 默认值，涉及文件数 > 2、或有接口/契约变化、或需要新增测试用例 |

判定原则：**能确定是"小改动"就走 quick，拿不准就走 matt-pocock 完整流程**——错判成本是"多做了规划"，比"该规划的没规划"便宜得多。

不确定时，直接问开发者一句，不要自己猜。

写入方式：`Write` 工具写 `openspec/changes/{change-name}/.flow-mode`，内容为 `quick` 或 `matt-pocock`（无换行、无多余字符）。这个文件决定了 `check_openspec_gate.py` 用哪一套前置条件校验，写错会导致后续 Step 2/4 的 Write 被 hook 拦截或误放行。

所有产出物放在 openspec/changes/{change-name}/ 下，结构如下（quick 模式下 design.md / spec.md 可省略，其余不变）：

```
openspec/changes/{change-name}/
├── .flow-mode
├── design.md
├── specs/{domain}/spec.md
├── tasks.md（含 blocking edges）
├── test-report.md
└── handoffs/
    ├── handoff-A.md
    ├── handoff-B.md
    └── ...
```

归档时直接运行 openspec archive，handoffs/ 会一并带入 archive 目录。归档命令本身会被 `check_archive_gate.py` 拦截校验（tasks.md 全部完成 + test-report.md 已存在），不满足条件时 archive 会被 BLOCKED，不是只靠"用户确认"这一道软约束。

## 流程总览

每次向用户汇报进度时，使用下面这张状态表，实时反映当前变更的真实进展。表格里的状态必须如实对应文件系统的实际情况。

```
+------------------------------------------+--------------------------------------+
|                  步骤                    |                状态                 |
+------------------------------------------+--------------------------------------+
| Step 0 Flow-mode（判定 quick/完整流程）  |  .flow-mode 已写入                  |
+------------------------------------------+--------------------------------------+
| Step 1 Grill（想法打磨）                 |  未开始 / 进行中 / 已完成           |
+------------------------------------------+--------------------------------------+
| Step 2 Spec（规范文档）                  |  design.md + spec.md                |
+------------------------------------------+--------------------------------------+
| Step 3 Tickets（拆工单+blocking edges）   |  tasks.md（全部 [x]）               |
+------------------------------------------+--------------------------------------+
| Step 4 Implement（执行）                  |  代码已实现，编译0错误 / ⚠️ 部分失败 |
+------------------------------------------+--------------------------------------+
| Step 5 Code Review（审查）                |  审查通过 / ⚠️ 需修复                |
+------------------------------------------+--------------------------------------+
| Step 6 QA 测试                           |  未执行 / 单元测试 / 自测 / UI / ❌ 未通过 |
+------------------------------------------+--------------------------------------+
| Step 7 Test Report（测试报告）            |  test-report.md                     |
+------------------------------------------+--------------------------------------+
| Step 8 Archive（归档）                    |  未执行（需显式指令）                |
+------------------------------------------+--------------------------------------+
```

状态符号约定：
- ✅ 已完成
- 🔄 进行中或部分完成
- ⚪ 未执行
- ⚠️ 完成但有遗留问题，需要人工介入才能继续
- ❌ 未通过，需回退到更早的步骤

quick 模式下 Step 1/2/3 直接标"跳过（quick 模式）"，不算未完成。

Archive 行永远不会因为前 7 步完成而自动变成 ✅，只在用户确认归档后才更新；即使手动确认，实际能否归档仍由 `check_archive_gate.py` 校验决定。

---

## 核心理念

**每个工单一个独立 session**。Agent 只看到一个工单，无法跳过/合并。
Blocking edges 声明工单依赖，开发者定义依赖，AI 不替人判断。
测试三阶段：单元测试 - 自测（全链路）- UI 测试（浏览器），通过后出报告再归档。

---

## Step 1: Grill（想法打磨）

quick 模式跳过，直接进 Step 4。

启动 /grilling 或用对话方式明确需求。

**产出**：清晰的功能描述和范围界定，直接写进 Step 2 的 design.md 开头（"背景"章节），不单独产出文件——`check_openspec_gate.py` 在 matt-pocock 模式下不要求 proposal.md/exploration.md，design.md 就是第一个规划产出物。

**目录**：openspec/changes/{change-name}/

可选：如果已有明确需求，可直接进入 Step 2。

---

## Step 2: Spec（规范文档）

quick 模式跳过，直接进 Step 4。

产出两个文档到 openspec/changes/{change-name}/：

- design.md - 技术设计方案（改动描述、涉及文件、不做范围）
- spec.md - 增量规范（验收标准、接口契约、交互规格）

**必须委派给 @spec-writer 完成写入**（不要主对话自己动手写）——`check_openspec_gate.py` 会校验 Write 的 agent_type，不是 @spec-writer 写的会被 BLOCKED。

**验收**：开发和技术评审人员能根据 spec 独立判断做完了没有。

---

## Step 3: Tickets（拆工单 + Blocking Edges）

quick 模式简化：只需一条工单（对应本次改动本身），仍要写 tasks.md，跳过下面的多工单拆分逻辑。

将功能拆成可独立交付的工单。每个工单都是端到端可交付的最小切片，不是功能分解。

产出一个 tasks.md，包含每个工单的 blocking edges、涉及文件、描述。**必须委派给 @task-breaker 完成写入**。

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

模板会在执行任何工单前先校验 blocking edges 依赖图（缺失引用/循环依赖直接报错终止，不会静默丢工单），每个工单最多重试 3 次，超过仍失败会被记录到返回值的 `failed` 列表中，不计入 `completed`。

### 4.2 每个 Agent 的执行规则

每个 agent 在独立 session 中执行一个工单，workflow 脚本必须显式传 `subagent_type: 'implementer'`（模板已内置）——`check_openspec_gate.py` 会校验代码写入的 agent_type，不是 @implementer 写的会被拦截：

1. 读 design.md 和 spec.md 了解上下文
2. 实现工单描述的改动
3. 运行 dotnet build 确认编译通过
4. 运行 dotnet test 确认所有测试通过
5. git commit（feat/fix 前缀 + 工单 ID）——commit 会被 `check_commit_gate.py` 拦截重新跑一遍 build/test，本地没跑或跑漏了这里会兜底拦截，不是只靠 agent 自觉
6. 生成 handoff 文件（openspec/changes/{change-name}/handoffs/handoff-{id}.md，格式见文末"Handoff 格式"）
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
| 工单 build/test 反复失败（workflow 脚本已重试 3 次仍失败） | 不再自动重试，写入 handoff 说明失败原因和已尝试的修复方向，工单状态标 ⚠️，等待人工介入，不 commit |
| 依赖图校验失败（workflow 脚本启动时报错） | 不进入 Step 4 执行，直接回 Step 3 修 tasks.md 里的 blocking edges |

---

## Step 5: Code Review

quick 模式简化：仍必须过一遍下面 5 项检查，但可以由主对话直接做，不强制单独 session。

审查本次变更的所有 diff：

1. dotnet build 0 错误 0 警告
2. 项目引用是否丢失
3. nullable 警告
4. unused import
5. Agent 调用的 try/catch 是否被误删

**发现问题的处理**：reviewer 不直接改代码（职责分离，且当前 `.claude/agents/` 下没有专属 code-review subagent，无法被 hook 校验身份）。在 tasks.md 里新增一条修复工单，回 Step 4 走正常单工单流程处理，状态表 Step 5 标 ⚠️ 直到修复工单完成。

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

### 6.4 任一测试未通过

不允许 QA 环节里的 agent 直接改代码修复。做法：

1. 状态表 Step 6 标 ❌，记录失败的具体测试项和现象
2. 在 tasks.md 新增一条修复工单，写清楚失败现象和已知线索
3. 回 Step 4，走正常单工单流程修复（新工单会重新经过 build/test/commit 的 hook 校验）
4. 修复工单完成后，Step 6 从头重新跑三阶段测试，不能只重跑失败的那一项——避免修复引入新的回归

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

这条命令会被 `check_archive_gate.py` 拦截校验：tasks.md 是否全部 [x]、test-report.md 是否存在。不满足条件时命令直接 BLOCKED，不会出现"手滑归档了一个没测完的变更"。

---

## Handoff 格式

每个工单完成后写 `openspec/changes/{change-name}/handoffs/handoff-{id}.md`，固定字段：

```markdown
## 工单 ID / 状态
{id} / completed | failed

## 改动文件清单
- path/to/file.cs（新增/修改/删除）

## 关键设计决策
简述做了什么取舍，为什么这么做（下游工单需要知道的部分）

## 遗留问题（给下游工单）
下一个工单接手时要注意什么，没做完的部分是什么

## 测试情况
跑了哪些测试，结果如何

## → 值得沉淀的经验（可选）
如果这次踩了坑或发现了值得记住的模式，写一两句话；
非空时由 orchestrator 追加到 .claude/agent-memory/implementer/learnings.md，
避免同样的坑在下一个 change 里重复踩。
```

最后一项是关键：handoff 默认只在本次 change 内有效，归档后就"过期"了；把值得记住的经验补充到 `.claude/agent-memory/{role}/learnings.md`（项目里已有这套跨 session 记忆机制），才能让下一次 /matt-pocock-flow 受益。

---

## 与 OpenSpec 共存

完全兼容。产出物直接放在 openspec/changes/{change-name}/ 目录下，结构只比 OpenSpec 多一个 handoffs/ 目录和一个 .flow-mode 标记文件：

| 环节 | 使用方式 |
|------|---------|
| 变更创建 | openspec new change {change-name} |
| flow-mode | openspec/changes/{change-name}/.flow-mode（quick / matt-pocock） |
| design.md | openspec/changes/{change-name}/design.md |
| spec.md | openspec/changes/{change-name}/specs/{domain}/spec.md |
| tasks.md | 保留 OpenSpec 格式，加 blocking edges |
| handoffs | openspec/changes/{change-name}/handoffs/（OpenSpec 没有，但不冲突）|
| test-report.md | openspec/changes/{change-name}/test-report.md |
| 归档 | openspec archive {change-name} --skip-specs --yes（受 check_archive_gate.py 校验） |

---

## 关键原则

1. 开发者定义依赖，AI 提建议
2. 每个工单独立 session，Agent 无法跳过。即使多个工单改同一文件，也各自独立 session + 各自 commit，不做"攒一批再提交"
3. 运行时 bug：Agent 评估影响，人决策
4. Agent 先问不要猜。规格不清楚时先评估影响范围再提问，不问就代表理解无误
5. 测试三阶段不可跳过。单元测试通过后检查进程和临时文件残留
6. Handoff 是必须的，值得沉淀的经验要追加进 agent-memory，不能只留在 change 目录里等归档
7. 小 bug 直接修，大重构切 session，做太大暂停重拆
8. 状态同步：tasks.md checkbox、test-report.md 验收标准，修改即同步，不拖到归档才补
9. 工单重试有上限（3 次），超限即升级为 ⚠️ 状态等待人工介入，不允许同一 session 无限重试硬撑
10. Code Review / QA 发现问题不允许原地直接改代码，一律开新工单回 Step 4，保持"谁审查、谁修复"职责分离
11. quick 模式跳过规划三步，但不跳过 build/test/commit 门禁和工单委派——省的是流程仪式，不是质量门槛