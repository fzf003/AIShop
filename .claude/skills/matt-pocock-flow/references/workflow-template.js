// Workflow 脚本模板
// 使用方法：
// 1. 复制本文件到 .claude/workflows/{change-name}.js
// 2. 修改 TASKS 数组，填入当前变更的工单和 blocking edges
// 3. 修改 spec 字符串，填入对应规范
// 4. 修改 designDoc 和 tasksFile 路径
// 5. 运行 workflow

export const meta = {
  name: 'execute-tasks',
  description: '根据 tasks.md 的 blocking edges 自动调度任务执行',
  phases: [
    { title: '无依赖任务', detail: '并行执行无依赖工单' },
    { title: '依赖任务', detail: '依赖就绪后执行' },
  ],
}

const TASKS = [
  { id: 'A', desc: '工单描述', files: '涉及文件路径', blocking: [] },
  { id: 'B', desc: '工单描述', files: '涉及文件路径', blocking: [] },
  { id: 'C1', desc: '诊断任务', files: '涉及文件路径', blocking: [] },
  { id: 'C2', desc: '优化任务', files: '待C1确定', blocking: ['C1'] },
]

const spec = `规范说明，从 spec.md 提取验收标准`

const designDoc = 'openspec/changes/{change-name}/design.md'
const tasksFile = 'openspec/changes/{change-name}/tasks.md'
const handoffDir = 'openspec/changes/{change-name}/handoffs'

function buildPrompt(t) {
  return `项目：D:\\Hermes\\Projects\\AIShop

## 任务：${t.id}
${t.desc}

## 涉及文件
${t.files}

## 规范
${spec}

## 设计文档
${designDoc}

## 要求
- 完成后 git commit 所有改动
- 在 ${tasksFile} 中把对应工单标记为
- 生成 handoff 文件 ${handoffDir}/handoff-${t.id}.md`
}

// 第一阶段：无依赖任务并行执行
phase('无依赖任务')
const noDeps = TASKS.filter(t => t.blocking.length === 0)
const firstResults = await parallel(noDeps.map(t => () =>
  agent(buildPrompt(t), { label: t.id, phase: '无依赖任务' })
))

const completed = new Set(noDeps.map(t => t.id))

// 第二阶段：执行依赖任务
phase('依赖任务')
const pending = TASKS.filter(t => t.blocking.length > 0)

while (pending.length > 0) {
  const ready = pending.filter(t =>
    t.blocking.every(dep => completed.has(dep))
  )

  if (ready.length === 0) break

  await parallel(ready.map(t => () =>
    agent(buildPrompt(t), { label: t.id, phase: '依赖任务' })
  ))

  for (const t of ready) {
    completed.add(t.id)
    pending.splice(pending.indexOf(t), 1)
  }
}

return { completed: [...completed], total: TASKS.length }
