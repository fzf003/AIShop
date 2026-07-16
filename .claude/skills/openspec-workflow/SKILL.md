---
name: openspec-workflow
description: 启动一个新的 OpenSpec 变更流程。当用户输入 /openspec-workflow 或明确要求"开始一个新功能/变更"时手动触发。
disable-model-invocation: true
argument-hint: [change-id] [简要描述]
---

# OpenSpec 变更流程

你现在要为变更 `$0`（描述：$1）执行严格的六阶段流程。**禁止合并步骤，禁止跳过，禁止在任何前置文件缺失时凭空生成占位内容替代继续执行。**

每个阶段都必须委派给对应的 subagent 完成，不要在主对话里自己代劳（主对话只负责编排和汇报进度）。

本流程适配官方 OpenSpec CLI（`openspec init` 生成的目录结构），并在此基础上补充了两个官方 schema 没有的产出物：`exploration.md`（STEP 0 调研）和 `test-report.md`（STEP 5 验证报告）。归档动作直接复用官方的 `openspec archive <change-id>` 命令，不额外发明。

---

### 流程总览

每次向用户汇报进度时，使用下面这张状态表，实时反映当前 change 的真实进展。表格里的状态**必须如实对应文件系统的实际情况**，不能凭印象或"应该做完了"去打勾。

```
┌──────────────────────────────────────┬────────────────────────────┐
│                 步骤                 │            状态            │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 0 Explore                       │ ✅ exploration.md          │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 1 Proposal                      │ ✅ proposal.md             │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 2 Design + Spec                 │ ✅ design.md + specs/{d}/spec.md │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 3 Tasks                         │ ✅ tasks.md（全部 [x]）    │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 4 Implementation                │ ✅ 代码已实现，编译 0 错误 │
├──────────────────────────────────────┼────────────────────────────┤
│ STEP 5 Verification / test-report.md │ ⚠️ 未执行                  │
├──────────────────────────────────────┼────────────────────────────┤
│ 归档（openspec archive）             │ ❌ 未执行（需显式指令）    │
└──────────────────────────────────────┴────────────────────────────┘
```

状态符号约定：
- ✅ 已完成，对应文件/条件真实存在且满足要求
- ⚠️ 尚未执行，或已执行但未通过（比如 `VERIFICATION_FAILED`）
- ❌ 未执行，且属于需要显式触发才能进行的动作（目前只有"归档"属于这一类）

"归档"这一行永远不会因为 STEP 0-5 全部完成而自动变成 ✅——它只在用户明确下达归档指令、且 `openspec archive $0` 真正执行之后才更新，其余时候恒为 ❌。

---

### 确认检查点（STEP 1-5 适用）

**STEP 1 到 STEP 5，每一步完成后，必须做两件事，缺一不可：**

1. **展示一次流程总览状态表**（用上面的模板，如实反映刚完成这一步之后的最新状态）
2. **停下来，向用户明确报告本步产出（附文件路径），并等待用户明确确认后才能进入下一步**——比如问一句"STEP 2 已完成，design.md 和 spec.md 都在这里，要继续 STEP 3 吗？"

**不允许**在一条消息里连续跑完多个 STEP 后才一次性汇报；**不允许**把用户没有回应、或只是笼统的"好的"之外的沉默当作确认；**不允许**自己判断"用户应该同意"就直接往下走。只有用户明确说了"继续"、"可以"、"下一步"或等效的肯定表述之后，才能调用下一个 STEP 对应的 subagent。

STEP 0（Explore）不受此规则约束——调研属于低风险的只读动作，做完直接进入 STEP 1 汇报，不需要在 STEP 0 后单独停下确认。

---

## STEP 0: Explore（只读调研）

**在开始调研前，先把 `openspec/.current-change` 写入内容 `$0`**（这是 hook 用来识别"当前活跃变更"的状态文件，实现代码阶段的门禁依赖它）。

调用 `@Explore` 调研现有代码库，回答：
- 这个变更涉及哪些现有文件/模块？
- 是否已有类似模式可复用？
- 有没有潜在冲突的现有实现？

- 结果保存为 `openspec/changes/$0/exploration.md`
- Explore 是只读 subagent，不允许触碰任何文件写入，调研结果由主对话代为写入 exploration.md
- 完成后打印 `EXPLORE_DONE: $0`

未完成此步骤，不允许进入 STEP 1。

---

## STEP 1: Proposal

调用 `@proposal-writer` 处理 change-id `$0`。

- 前置条件：`openspec/changes/$0/exploration.md` 必须存在
- 产出：`openspec/changes/$0/proposal.md`（变更动机、影响范围、备选方案）
- 完成后打印 `PROPOSAL_DONE: $0`

未完成此步骤，不允许进入 STEP 2。

**完成后：展示流程总览状态表，向用户报告 proposal.md 已产出，等待用户明确确认后再进入 STEP 2。**

---

## STEP 2: Design + Spec

调用 `@spec-writer` 处理 change-id `$0`。

- 前置条件：`openspec/changes/$0/proposal.md` 必须存在
- 若文件不存在，**立即终止并报告**，不要自行编造 proposal 内容代替
- 产出两个文件：
  - `openspec/changes/$0/design.md`（技术设计：接口、数据结构、兼容性）
  - `openspec/changes/$0/specs/{domain}/spec.md`（增量规范，Given/When/Then 格式，domain 名默认等于 `$0`，如项目已有对应业务域目录则复用既有名字）
- 完成后打印 `SPEC_DONE: $0`

未完成此步骤，不允许进入 STEP 3。

**完成后：展示流程总览状态表，向用户报告 design.md 和 spec.md 已产出，等待用户明确确认后再进入 STEP 3。**

---

## STEP 3: Tasks

调用 `@task-breaker` 处理 change-id `$0`。

- 前置条件：`openspec/changes/$0/design.md` 和至少一个 `openspec/changes/$0/specs/{domain}/spec.md` 必须存在
- 产出：`openspec/changes/$0/tasks.md`（拆解为可独立验收的任务清单，**每项任务预计耗时不超过 10 分钟**，超出则继续拆分，格式为 `- [ ] (预计 Nmin) 任务描述`）
- 完成后打印 `TASKS_DONE: $0`

未完成此步骤，不允许进入 STEP 4。

**完成后：展示流程总览状态表，向用户报告 tasks.md 已产出（附任务条数），等待用户明确确认后再进入 STEP 4。**

---

## STEP 4: Implementation

调用 `@implementer` 处理 change-id `$0`。

- 前置条件：`openspec/changes/$0/tasks.md` 必须存在
- 此时才允许调用 Write/Edit 实现代码
- 每完成一项 task，在 tasks.md 中勾选对应项
- 严禁实现 tasks.md 中未列出的范围

未完成此步骤（tasks.md 未全部勾选），不允许进入 STEP 5。

**完成后：展示流程总览状态表，向用户报告实现已完成、编译结果，等待用户明确确认后再进入 STEP 5。**

---

## STEP 5: Verification（运行测试 + 出测试报告）

调用 `@tester` 处理 change-id `$0`。

- 前置条件：`openspec/changes/$0/tasks.md` 全部任务已勾选完成
- 产出：`openspec/changes/$0/test-report.md`（测试执行结果 + design.md/specs 覆盖度检查）
- 若返回 `VERIFICATION_FAILED`：把问题交还给 `@implementer` 修复，修复后重新回到 STEP 5，不允许跳过直接结束
- 若返回 `VERIFICATION_PASSED`：清空（或删除）`openspec/.current-change`，结束本次变更的门禁管控状态

**注意：验证通过 ≠ 自动归档。** `openspec/changes/$0/` 目录在验证通过后原样保留，不要自动移动、删除或归档。归档动作只能由用户明确发出指令后执行（比如用户说"归档这个变更"），此时主对话运行官方命令 `openspec archive $0`——这个命令本身就是官方设计的显式人工触发点，会把 delta spec 合并进 `openspec/specs/{domain}/spec.md` 主规范目录，并把 change 移入 archive。未收到这类显式指令前，视为流程到 `VERIFICATION_PASSED` 即结束，change 目录保持原状。

这是本流程的终点（不含归档）。完整链路为：
`Explore → Proposal → Design+Spec → Tasks → Implementation → Verification`

**完成后：展示流程总览状态表，向用户报告 test-report.md 的结论（PASS/FAIL）。若 PASS，明确询问用户是否现在要归档（`openspec archive $0`），不要自己主动执行归档；若用户暂不归档或没有回应，流程到此结束，不追问、不重复提醒。**

---

## STEP 5 之后发现问题：正式重入流程（不允许绕过门禁临时修补）

如果 `test-report.md` 已经是 `PASS`、`.current-change` 已清空之后，又发现了新的 bug 或遗漏，**禁止直接用 Write/Edit 改代码"快速修一下"**。此时代码没有任何门禁保护，且 `design.md` / `specs/{domain}/spec.md` / `test-report.md` 会立刻变成跟实际代码不符的过期文档。

正确做法（重入，不是重启）：

1. **重新打开门禁**：把 `$0` 重新写入 `openspec/.current-change`
2. **增量更新 tasks.md**：调用 `@task-breaker`，以"追加模式"在 `openspec/changes/$0/tasks.md` 末尾新增本次修复对应的任务项，**不删除、不重写已有的历史任务**
3. **不需要重新走 STEP 0-2**（除非这次修复本身改变了原有设计/规范，才需要回头找 `@spec-writer` 补充 design.md 或 delta spec，并在 proposal.md 里追加一句"修订原因"）
4. **直接回到 STEP 4**：调用 `@implementer` 完成新增的任务项
5. **重新执行 STEP 5**：调用 `@tester`，重新生成 `test-report.md`（覆盖旧报告，旧报告的 PASS 结论已经过期）
6. 验证通过后，再次清空 `.current-change`

---

## 通用规则

如果任何前置文件缺失，直接停止并告知用户在哪一步缺失了什么，不要用推测内容"补全"继续往下走。

真正的强制门禁由 `.claude/hooks/check_openspec_gate.py`（PreToolUse hook）保证，此 skill 只负责规定顺序和调度 subagent。该 hook 现在做三层检查：
1. 前置文件是否存在（比如没有 proposal.md 就不能写 design.md）
2. **调用方身份是否匹配**：proposal.md 只能由 `@proposal-writer` 写，design.md 和 delta spec（`specs/{domain}/spec.md`）只能由 `@spec-writer` 写，tasks.md 只能由 `@task-breaker` 写，实现代码只能由 `@implementer` 写，test-report.md 只能由 `@tester` 写——主对话如果自己动手写这些文件，会被 hook 硬性拦截，不是靠这份 skill 文档口头约束。
3. **任务完成度**：写 test-report.md 前，hook 会解析 tasks.md，如果还有 `- [ ]` 未勾选的任务，直接拦截。

所以主对话在执行本流程时，**必须真的用 Task 工具调用对应 subagent**，而不能自己读完 SKILL.md 描述后直接代劳——否则 Write/Edit 会在第一步就被 hook 拒绝，报错信息里会写明"必须由 @xxx 完成"。

**归档规则（适用于本流程任何阶段）**：无论流程走到哪一步、验证是否通过，都不允许自动把 `openspec/changes/{id}/` 移动、删除或归档。归档统一走官方 `openspec archive {id}` 命令，且只能由用户显式指令触发。