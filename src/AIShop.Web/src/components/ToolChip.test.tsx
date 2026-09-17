/**
 * 工具胶囊组件用例（tasks.md C7「验收 / 测试」的渲染面；spec R8 / R13-3 的胶囊形态）。
 *
 * - R8-1「默认折叠、点击展开」：一轮含 `search_product` 的 SSE（走**真实 `@ag-ui/client`**）
 *   → 聊天区出现**折叠**胶囊（工具名 + 耗时）；点击后展开显示参数 / 结果 / 耗时 / 状态；
 * - R8-2「耗时来自客户端实测」：注入时钟使 START → RESULT 相差 300ms → 展示的正是 `300ms`；
 * - R8-3「无整轮用量时不显示 token」：`RUN_FINISHED` 无 `usage` → 展开面板**不出现**任何 token 项；
 *   有 `usage` → token 项出现并**标注整轮口径**（不是逐工具）。
 */
import { afterEach, describe, expect, it } from 'vitest'

import { installFetchStub } from '../test/fetch-stub'
import { render, screen, userEvent } from '../test/render'
import {
  createSseResponse,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResult,
  toolCallStart,
  type SseEvent,
  type TokenUsage,
} from '../test/sse'
import { createAgent } from '../agui/agent'
import { attachToolEvents, createToolTracker, type ToolCallEntry } from '../agui/tools'
import ToolChip from './ToolChip'

let restoreFetch: (() => void) | null = null

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
})

/** 一轮含 `search_product` 的 SSE（`usage` 可选，用于「有 / 无整轮用量」两条分支）。 */
function searchProductRound(usage?: readonly TokenUsage[]): SseEvent[] {
  return [
    runStarted('t', 'r1'),
    textMessageStart('a1'),
    toolCallStart('tc1', 'search_product', 'a1'),
    toolCallArgs('tc1', '{"query":"跑鞋"}'),
    toolCallEnd('tc1'),
    toolCallResult('tc1', 'tr1', '找到 3 个商品：\n#1 专业跑鞋 — ¥129.99'),
    textMessageContent('a1', '为您找到 3 款跑鞋'),
    textMessageEnd('a1'),
    runFinished('t', 'r1', usage),
  ]
}

/** 用真实 SDK 跑完一段 SSE，取回追踪器（事件 → 视图模型全走生产路径）。 */
async function trackerAfterSse(events: readonly SseEvent[], now?: () => number) {
  const stub = installFetchStub({ '/agui': () => createSseResponse(events) })
  restoreFetch = stub.restore

  const session = createAgent({ username: 'marla', model: 'gpt-4.1', threadId: 't' })
  const tracker = now === undefined ? createToolTracker() : createToolTracker({ now })
  const detach = attachToolEvents(session.agent, tracker)
  await session.runRound('推荐跑鞋')
  detach()
  return tracker
}

/** 直接构造一次工具调用的视图模型（参数 / 结果的形态由 SSE 路径的用例覆盖）。 */
function entryOf(overrides: Partial<ToolCallEntry['tool']> = {}, usage: ToolCallEntry['usage'] = null): ToolCallEntry {
  return {
    tool: {
      toolCallId: 'tc1',
      name: 'search_product',
      argsText: '{"query":"跑鞋"}',
      args: { query: '跑鞋' },
      result: '找到 3 个商品',
      status: 'success',
      durationMs: 320,
      ...overrides,
    },
    usage,
  }
}

function renderChip(entry: ToolCallEntry) {
  return render(<ToolChip tool={entry.tool} usage={entry.usage} />)
}

describe('工具胶囊（R8-1 默认折叠 + 点击展开 / R8-2 实测耗时）', () => {
  it('R8-1：一轮含 search_product 的 SSE → 渲染出折叠胶囊；点击后展开参数 / 结果 / 耗时 / 状态', async () => {
    const tracker = await trackerAfterSse(searchProductRound())
    const entry = tracker.findToolCall('tc1')
    expect(entry).not.toBeNull()

    const { container } = renderChip(entry as ToolCallEntry)

    // 折叠态：只有药丸（工具名 + 耗时 + 箭头），展开体不在 DOM 里
    const pill = screen.getByRole('button')
    expect(pill.textContent).toContain('search_product')
    expect(pill.textContent).toMatch(/\d+ms/)
    expect(container.querySelector('.tool-body')).toBeNull()
    expect(container.querySelector('.tool')?.className).not.toContain('open')
    expect(container.textContent).not.toContain('参数')
    expect(container.textContent).not.toContain('找到 3 个商品')

    await userEvent.click(pill)

    // 展开态：元信息行（耗时 / 状态）+ 参数段 + 结果段
    expect(container.querySelector('.tool-body')).not.toBeNull()
    const metaText = container.querySelector('.tool-meta')?.textContent ?? ''
    expect(metaText).toContain('耗时')
    expect(metaText).toMatch(/\d+ms/)
    expect(metaText).toContain('状态')
    expect(container.querySelector('.tool-meta .st')?.textContent).toBe('成功')

    expect(container.querySelector('.tool-sec')?.textContent).toContain('参数')
    expect(container.textContent).toContain('query: "跑鞋"')
    expect(container.textContent).toContain('找到 3 个商品')
  })

  it('R8-2：START 与 RESULT 相差 300ms 的实测事件 → 折叠态与展开态都显示 300ms', async () => {
    const clock = { value: 1_000 }
    const tracker = createToolTracker({ now: () => clock.value })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'add_to_cart' })
    tracker.record({ type: 'TOOL_CALL_ARGS', toolCallId: 'tc1', delta: '{"product_id":3}' })
    tracker.record({ type: 'TOOL_CALL_END', toolCallId: 'tc1' })
    clock.value += 300
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '已添加 专业跑鞋 x1 到购物车' })

    const { container } = renderChip(tracker.findToolCall('tc1') as ToolCallEntry)

    // 展示的是两个事件的时间差本身（若实现改成常量 / 估算，本断言必红）
    expect(container.querySelector('.tool-chip .t')?.textContent).toBe('300ms')

    await userEvent.click(screen.getByRole('button'))
    expect(container.querySelector('.tool-meta')?.textContent).toContain('300ms')
  })
})

describe('整轮 token 口径（R8 第 3 段：逐工具 token 不可得）', () => {
  it('R8-3：RUN_FINISHED 无 usage → 展开面板中不出现任何 token 项（无 0、无占位、无估算）', async () => {
    const tracker = await trackerAfterSse(searchProductRound())
    const entry = tracker.findToolCall('tc1') as ToolCallEntry
    expect(entry.usage).toBeNull()

    const { container } = renderChip(entry)
    await userEvent.click(screen.getByRole('button'))

    // 展开面板确实渲染了（否则「不出现 token」是空断言）
    expect(container.querySelector('.tool-meta')?.textContent).toContain('状态')
    expect(container.querySelector('.tool-meta .usage')).toBeNull()
    expect(container.textContent).not.toContain('token')
    expect(container.textContent).not.toContain('整轮')
    expect(container.textContent).not.toContain('142')
  })

  it('有 usage → token 项出现并标注整轮口径（不标注为逐工具）', async () => {
    const tracker = await trackerAfterSse(
      searchProductRound([{ provider: 'deepseek', inputTokens: 120, outputTokens: 34, totalTokens: 154 }]),
    )
    const entry = tracker.findToolCall('tc1') as ToolCallEntry
    expect(entry.usage).toEqual({ inputTokens: 120, outputTokens: 34, totalTokens: 154 })

    const { container } = renderChip(entry)
    await userEvent.click(screen.getByRole('button'))

    const usage = container.querySelector('.tool-meta .usage')
    expect(usage).not.toBeNull()
    expect(usage?.textContent).toContain('整轮口径')
    expect(usage?.textContent).toContain('120')
    expect(usage?.textContent).toContain('34')
    expect(usage?.textContent).toContain('154')
  })

  it('协议里只有整轮用量时，胶囊对同一轮流内的每个工具都挂同一份汇总', async () => {
    const entry = entryOf({}, { inputTokens: 10, outputTokens: 2, totalTokens: 12 })
    const { container } = renderChip(entry)
    await userEvent.click(screen.getByRole('button'))

    expect(container.querySelector('.tool-meta .usage')?.textContent).toContain('合计 12')
  })
})

describe('状态与进行中（design §9.6）', () => {
  it('无 RESULT 的调用 → 胶囊显示「进行中」，结果段无内容、无 token 项', async () => {
    const entry = entryOf({ status: 'running', result: null, durationMs: null })
    const { container } = renderChip(entry)

    expect(container.querySelector('.tool-chip .t')?.textContent).toBe('进行中')

    await userEvent.click(screen.getByRole('button'))
    expect(container.querySelector('.tool-meta .st')?.textContent).toBe('进行中')
    expect(container.querySelector('.tool-meta .usage')).toBeNull()
    expect(container.querySelector('.tool-result')).toBeNull()
  })

  it('错误文案的结果 → 状态显示「失败」', async () => {
    const entry = entryOf({ status: 'failure', result: '❌ 无法查询 上海的天气: 超时' })
    const { container } = renderChip(entry)
    await userEvent.click(screen.getByRole('button'))

    expect(container.querySelector('.tool-meta .st')?.textContent).toBe('失败')
    expect(container.textContent).toContain('❌ 无法查询')
  })

  it('参数缺失 → 参数段显示占位「无参数」而不是空白段', async () => {
    const entry = entryOf({ args: {}, argsText: '' })
    const { container } = renderChip(entry)
    await userEvent.click(screen.getByRole('button'))

    expect(container.querySelector('.tool-sec code')).toBeNull()
    expect(container.querySelector('.empty')?.textContent).toBe('无参数')
  })

  it('JSON 结果结构化展示（缩进），纯文本结果原样保留分行', async () => {
    const structured = entryOf({
      name: 'recommend_products',
      args: {},
      result: '{"message":"无推荐","hasRecommendation":false,"products":[]}',
    })
    const first = renderChip(structured)
    await userEvent.click(screen.getByRole('button'))
    expect(first.container.querySelector('.tool-result')?.textContent).toContain('"hasRecommendation": false')
    first.unmount()

    const plain = entryOf({ result: '您的购物车共 2 件商品，总计 ¥329.98\n· 专业跑鞋 × 1' })
    const second = renderChip(plain)
    await userEvent.click(screen.getByRole('button'))
    expect(second.container.querySelector('.tool-result')?.textContent).toBe(
      '您的购物车共 2 件商品，总计 ¥329.98\n· 专业跑鞋 × 1',
    )
  })
})
