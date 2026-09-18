/**
 * 工具胶囊视图模型用例（tasks.md C7「验收 / 测试」的数据面；spec R8）。
 *
 * - R8-2「耗时来自客户端实测」：`TOOL_CALL_START` → `TOOL_CALL_RESULT` 的墙钟差，时钟可注入；
 * - R8-3「无整轮用量时不显示 token」：`RUN_FINISHED` 缺 `usage` → `round.usage === null`
 *   （不补 0、不估算）；有 `usage` 时汇总为整轮口径并挂到**该轮全部**胶囊；
 * - 状态判据（design §9.6）：有 RESULT → 成功；错误文案（`❌` / `Error` 前缀）→ 失败；
 *   无 RESULT → 进行中；
 * - 与官方客户端的接缝（`attachToolEvents`）：走**真实 `@ag-ui/client`** + `src/test/sse.ts` 替身。
 */
import type { Message } from '@ag-ui/client'
import { createElement } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import App from '../App'
import ToolChip from '../components/ToolChip'
import { dismissToast } from '../components/Toast'
import { resetCart } from '../state/cart'
import { STORAGE_KEYS, writeModelId, writeUsername } from '../state/session'
import { installFetchStub } from '../test/fetch-stub'
import { render, screen, userEvent, waitFor } from '../test/render'
import {
  createSseResponse,
  runError,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResult,
  toolCallResultEncoded,
  toolCallStart,
  type SseEvent,
} from '../test/sse'
import { createAgent } from './agent'
import { endSession } from './store'
import {
  attachToolEvents,
  createToolTracker,
  decodeToolResultContent,
  type ToolRound,
} from './tools'

/** 可推进的假时钟：让「开始 → 结果」的墙钟差完全确定。 */
function fakeClock(start = 1_000) {
  const clock = { value: start }
  return {
    now: () => clock.value,
    advance(ms: number) {
      clock.value += ms
    },
  }
}

let restoreFetch: (() => void) | null = null

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
})

describe('tools.ts：事件 → 视图模型（R8 数据面）', () => {
  it('START / ARGS / END / RESULT 累积出工具名、参数与结果', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    tracker.record({ type: 'TOOL_CALL_ARGS', toolCallId: 'tc1', delta: '{"keyword":' })
    tracker.record({ type: 'TOOL_CALL_ARGS', toolCallId: 'tc1', delta: '"跑鞋"}' })

    // RESULT 之前：参数已累积（END 缺省时回退解析原文），状态为进行中
    const beforeEnd = tracker.getRounds()[0]?.toolCalls[0]
    expect(beforeEnd?.argsText).toBe('{"keyword":"跑鞋"}')
    expect(beforeEnd?.args).toEqual({})
    expect(beforeEnd?.status).toBe('running')

    tracker.record({ type: 'TOOL_CALL_END', toolCallId: 'tc1' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '找到 3 个商品：\n#1 专业跑鞋' })

    const round = tracker.getRounds()[0]
    expect(round?.runId).toBeNull()
    expect(round?.toolCalls).toHaveLength(1)
    expect(round?.toolCalls[0]?.name).toBe('search_product')
    expect(round?.toolCalls[0]?.args).toEqual({ keyword: '跑鞋' })
    expect(round?.toolCalls[0]?.result).toBe('找到 3 个商品：\n#1 专业跑鞋')
    expect(round?.toolCalls[0]?.status).toBe('success')
    expect(round?.finished).toBe(false)
  })

  it('R8-2 耗时 = TOOL_CALL_START 与 TOOL_CALL_RESULT 的墙钟差（本例 300ms）', () => {
    const clock = fakeClock(10_000)
    const tracker = createToolTracker({ now: clock.now })

    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    clock.advance(300)
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '找到 3 个商品' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc2', toolCallName: 'add_to_cart' })
    clock.advance(120)
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc2', content: '已添加 x1' })

    const calls = tracker.getRounds()[0]?.toolCalls ?? []
    // 每个工具的耗时为各自 START → RESULT 的实测差，**不是**常量、不是整轮时长
    expect(calls.map((call) => call.durationMs)).toEqual([300, 120])
  })

  it('无 TOOL_CALL_RESULT → 显示「进行中」（durationMs 为 null，状态 running）', () => {
    const clock = fakeClock()
    const tracker = createToolTracker({ now: clock.now })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'get_cart_summary' })
    clock.advance(5_000)

    const call = tracker.getRounds()[0]?.toolCalls[0]
    expect(call?.durationMs).toBeNull()
    expect(call?.result).toBeNull()
    expect(call?.status).toBe('running')
  })

  it('结果文本为错误文案 → 状态为失败（服务端 ❌ 前缀 / FICC 回填的 Error 前缀）', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'error-mark', toolCallName: 'get_weather_forecast' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'error-mark', content: '❌ 无法查询 上海的天气: 超时' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'thrown', toolCallName: 'add_to_cart' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'thrown', content: 'Error: 工具执行失败' })

    const calls = tracker.getRounds()[0]?.toolCalls ?? []
    expect(calls.map((call) => call.status)).toEqual(['failure', 'failure'])
  })

  it('R8-3 无 usage → 该轮 usage 为 null（不显示 0、不估算）', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'RUN_STARTED', runId: 'r1' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '找到 1 个商品' })
    tracker.record({ type: 'RUN_FINISHED', runId: 'r1' })

    const round = tracker.getRounds()[0]
    expect(round?.finished).toBe(true)
    expect(round?.usage).toBeNull()
    expect(tracker.findToolCall('tc1')?.usage).toBeNull()

    // 空数组（协议允许 usage: []）同样视为「无用量」，而不是 0
    const empty = createToolTracker()
    empty.record({ type: 'RUN_STARTED', runId: 'r2' })
    empty.record({ type: 'TOOL_CALL_START', toolCallId: 'tc2', toolCallName: 'search_product' })
    empty.record({ type: 'RUN_FINISHED', runId: 'r2', usage: [] })
    expect(empty.getRounds()[0]?.usage).toBeNull()
  })

  it('有 usage → 汇总为该轮口径，并挂到该轮全部胶囊（含未结束的那次调用）', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'RUN_STARTED', runId: 'r1' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '找到 1 个商品' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc2', toolCallName: 'add_to_cart' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc2', content: '已添加 x1' })
    tracker.record({
      type: 'RUN_FINISHED',
      runId: 'r1',
      usage: [{ provider: 'deepseek', model: 'mimo-v2.5', inputTokens: 120, outputTokens: 34, totalTokens: 154 }],
    })

    const expected = { inputTokens: 120, outputTokens: 34, totalTokens: 154 }
    expect(tracker.getRounds()[0]?.usage).toEqual(expected)
    // 整轮口径：该轮每个胶囊看到的是**同一份**汇总，不是各自的分摊值
    expect(tracker.findToolCall('tc1')?.usage).toEqual(expected)
    expect(tracker.findToolCall('tc2')?.usage).toEqual(expected)
  })

  it('多条目 usage 逐项相加；缺 totalTokens 时用「输入 + 输出」补齐', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'RUN_STARTED', runId: 'r1' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    tracker.record({
      type: 'RUN_FINISHED',
      runId: 'r1',
      usage: [
        { inputTokens: 100, outputTokens: 20 },
        { inputTokens: 10, outputTokens: 5, totalTokens: 15 },
      ],
    })

    expect(tracker.getRounds()[0]?.usage).toEqual({
      inputTokens: 110,
      outputTokens: 25,
      totalTokens: 15,
    })
  })

  it('RUN_ERROR.usage 同样挂到该轮；新的一轮不继承上一轮用量（R8 的轮次边界）', () => {
    const clock = fakeClock()
    const tracker = createToolTracker({ now: clock.now })

    tracker.record({ type: 'RUN_STARTED', runId: 'r1' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'search_product' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: '找到 1 个商品' })
    tracker.record({ type: 'RUN_ERROR', usage: [{ totalTokens: 42 }] })

    expect(tracker.getRounds()[0]?.usage).toEqual({ inputTokens: null, outputTokens: null, totalTokens: 42 })

    // 第二轮：只有工具调用、没有结束事件 → 不显示 token
    tracker.record({ type: 'RUN_STARTED', runId: 'r2' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc2', toolCallName: 'add_to_cart' })

    const [first, second] = tracker.getRounds()
    expect(first?.runId).toBe('r1')
    expect(second?.runId).toBe('r2')
    expect(second?.usage).toBeNull()
    expect(tracker.findToolCall('tc1')?.usage?.totalTokens).toBe(42)
    expect(tracker.findToolCall('tc2')?.usage).toBeNull()
  })

  it('未识别的 toolCallId（无 START）事件被忽略，不产生空胶囊', () => {
    const tracker = createToolTracker()
    tracker.record({ type: 'TOOL_CALL_ARGS', toolCallId: 'ghost', delta: '{}' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'ghost', content: 'x' })

    expect(tracker.getRounds()).toEqual([])
    expect(tracker.findToolCall('ghost')).toBeNull()
  })
})

describe('attachToolEvents：接官方客户端订阅面（真实 @ag-ui/client）', () => {
  /** 一轮含 `search_product` 的 SSE：assistant 带 toolCalls + 配对 tool 结果消息。 */
  function toolRound(threadId: string, runId: string, assistantId: string): SseEvent[] {
    return [
      runStarted(threadId, runId),
      textMessageStart(assistantId),
      toolCallStart('tc1', 'search_product', assistantId),
      toolCallArgs('tc1', '{"keyword":"跑鞋"}'),
      toolCallEnd('tc1'),
      toolCallResult('tc1', 'tr1', '找到 3 个商品：\n#1 专业跑鞋'),
      textMessageContent(assistantId, '为您找到 3 款跑鞋'),
      textMessageEnd(assistantId),
      runFinished(threadId, runId, [{ inputTokens: 120, outputTokens: 34, totalTokens: 154 }]),
    ]
  }

  it('一轮含 search_product 的 SSE → 追踪器得到该次调用（参数 / 结果 / 耗时 / 整轮用量）', async () => {
    const stub = installFetchStub({ '/agui': () => createSseResponse(toolRound('t', 'r1', 'a1')) })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: 'gpt-4.1', threadId: 't' })
    const tracker = createToolTracker()
    const detach = attachToolEvents(session.agent, tracker)
    await session.runRound('推荐跑鞋')
    detach()

    const round = tracker.getRounds()[0]
    expect(round?.runId).toBe('r1')
    expect(round?.finished).toBe(true)
    expect(round?.toolCalls).toHaveLength(1)

    const call = round?.toolCalls[0]
    expect(call?.toolCallId).toBe('tc1')
    expect(call?.name).toBe('search_product')
    // 参数来自 SDK 的 onToolCallEndEvent（已解析成对象，不是字符串）
    expect(call?.args).toEqual({ keyword: '跑鞋' })
    expect(call?.result).toBe('找到 3 个商品：\n#1 专业跑鞋')
    expect(call?.status).toBe('success')
    // 真实时钟下的实测耗时（>= 0 的有限数；具体值不作断言，R8-2 的精确值由注入时钟的用例覆盖）
    expect(typeof call?.durationMs).toBe('number')
    expect(call?.durationMs ?? -1).toBeGreaterThanOrEqual(0)

    expect(round?.usage).toEqual({ inputTokens: 120, outputTokens: 34, totalTokens: 154 })
  })

  it('SSE 的 RUN_ERROR 帧（服务端流内报错）→ 该轮带用量并结束', async () => {
    const stub = installFetchStub({
      '/agui': () =>
        createSseResponse([
          runStarted('t', 'r1'),
          toolCallStart('tc1', 'recommend_products'),
          toolCallArgs('tc1', '{}'),
          toolCallEnd('tc1'),
          toolCallResult('tc1', 'tr1', '{"message":"无推荐","hasRecommendation":false,"products":[]}'),
          runError('模型超时', 'timeout', [{ totalTokens: 7 }]),
        ]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: 'gpt-4.1', threadId: 't' })
    const tracker = createToolTracker()
    const detach = attachToolEvents(session.agent, tracker)
    await session.runRound('推荐一下')
    detach()

    const round = tracker.getRounds()[0]
    expect(round?.finished).toBe(true)
    expect(round?.usage?.totalTokens).toBe(7)
    expect(round?.toolCalls[0]?.status).toBe('success')
  })
})

/**
 * D1（design §15.1）：宿主把工具结果字符串多编码一层，读取侧恰好剥一次。
 *
 * ⚠️ 本组**不**断言「幂等」——「剥一层」天然不幂等（见 `decodeToolResultContent` 注释里的反例），
 * 只断言「不误伤」（no-op）与「恰好剥一层」。
 */
describe('decodeToolResultContent：剥掉宿主多编码层（D1）', () => {
  it('双编码的 JSON 对象文本 → 解出原 JSON 文本（再次 JSON.parse 得到对象）', () => {
    const resultText = JSON.stringify({
      message: '根据您的对话，为您推荐：',
      hasRecommendation: true,
      products: [],
    })
    const wire = JSON.stringify(resultText) // 宿主又序列化了一次

    const decoded = decodeToolResultContent(wire)
    expect(decoded).toBe(resultText)
    // 修复的直接目的：面板那次 JSON.parse 拿到的是**对象**而不是 string
    expect(typeof JSON.parse(decoded)).toBe('object')
  })

  it('双编码的纯文本（❌ 失败文案）→ 解出原文（外层引号被剥掉，前缀可匹配）', () => {
    const wire = JSON.stringify('❌ 无法查询 上海的天气: 超时')
    expect(decodeToolResultContent(wire)).toBe('❌ 无法查询 上海的天气: 超时')
  })

  it('不含外层引号的普通文本 → 原样返回（no-op，不误伤）', () => {
    const plain = '找到 3 个商品：\n#1 专业跑鞋'
    expect(decodeToolResultContent(plain)).toBe(plain)
    expect(decodeToolResultContent('Error: 工具执行失败')).toBe('Error: 工具执行失败')
  })

  it('工具真的返回带引号字符串 `"hi"` → 只剥宿主那一层，得到 `"hi"`（引号是工具原意）', () => {
    const wire = JSON.stringify('"hi"') // = '"\"hi\""'
    expect(decodeToolResultContent(wire)).toBe('"hi"')
  })

  it('非法 JSON / 空串 / 未闭合引号 → 原样返回且不抛异常', () => {
    expect(decodeToolResultContent('{ 这不是合法 JSON')).toBe('{ 这不是合法 JSON')
    expect(decodeToolResultContent('')).toBe('')
    expect(decodeToolResultContent('"未闭合')).toBe('"未闭合')
  })

  it('已解码的 JSON 对象文本再调一次 → 原样返回（该类输入 no-op，非全局「幂等」承诺）', () => {
    const decoded = JSON.stringify({ products: [] })
    expect(decodeToolResultContent(decoded)).toBe(decoded)

    // 反例：已解码的**带引号**文本再解一次会掉引号（把工具本意的引号吃掉）——
    // 这正是「只在 wire 边界恰好应用一次、MUST NOT 链式调用」的理由。
    expect(decodeToolResultContent('"hi"')).toBe('hi')
  })
})

/** 双编码帧经真实 SDK 落到追踪器（`attachToolEvents` 是唯一的 wire 边界解码点）。 */
describe('双编码帧回放：顺带修好的两处（D1）', () => {
  /** 一轮工具调用的 SSE；结果以**真机形态**（宿主多编码一层）发出。 */
  function encodedToolRound(result: string, toolName = 'search_product'): SseEvent[] {
    return [
      runStarted('t', 'r1'),
      textMessageStart('a1'),
      toolCallStart('tc1', toolName, 'a1'),
      toolCallArgs('tc1', '{"keyword":"跑鞋"}'),
      toolCallEnd('tc1'),
      toolCallResultEncoded('tc1', 'tr1', result),
      textMessageContent('a1', '好的'),
      textMessageEnd('a1'),
      runFinished('t', 'r1'),
    ]
  }

  async function runEncoded(result: string, toolName?: string) {
    const stub = installFetchStub({ '/agui': () => createSseResponse(encodedToolRound(result, toolName)) })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: 'gpt-4.1', threadId: 't' })
    const tracker = createToolTracker()
    const detach = attachToolEvents(session.agent, tracker)
    await session.runRound('推荐跑鞋')
    detach()
    return tracker.getRounds()[0]?.toolCalls[0]
  }

  it('复现基线：未解码时错误文案被判为**成功**（前缀匹配不到外层的 `"`）', () => {
    // 直喂 tracker（等价修复前的路径）：多编码值以 `"` 开头 → `❌` 前缀匹配失败。
    const tracker = createToolTracker()
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'tc1', toolCallName: 'get_weather_forecast' })
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'tc1', content: JSON.stringify('❌ 超时') })

    expect(tracker.getRounds()[0]?.toolCalls[0]?.status).toBe('success')
  })

  it('isFailureResult 恢复：双编码的 `❌ …` 被判为 failure', async () => {
    const call = await runEncoded('❌ 无法查询 上海的天气: 超时', 'get_weather_forecast')

    expect(call?.result).toBe('❌ 无法查询 上海的天气: 超时')
    expect(call?.status).toBe('failure')
  })

  it('FICC 回填的 `Error` 前缀在双编码下同样判为 failure', async () => {
    const call = await runEncoded('Error: 工具执行失败', 'add_to_cart')

    expect(call?.status).toBe('failure')
  })

  it('ToolChip 的 JSON 美化分支命中：结果段被缩进美化（而非原样展示双编码串）', async () => {
    const payload = JSON.stringify({
      message: '为您精选商品',
      hasRecommendation: false,
      products: [],
    })
    const call = await runEncoded(payload)

    // 解码后 = 原 JSON 文本，`resultText` 的 `{` 分支才会命中
    expect(call?.result).toBe(payload)
    if (call === undefined) throw new Error('未捕获到工具调用')

    // 本文件是 `.ts`（非 `.tsx`），故用 `createElement` 而非 JSX。
    const { container } = render(createElement(ToolChip, { tool: call, usage: null }))
    await userEvent.click(screen.getByRole('button', { name: /search_product/ }))

    const shown = container.querySelector('.tool-result')?.textContent ?? ''
    expect(shown).toContain('\n  "message"') // 缩进美化（未美化时无双空格缩进）
    expect(shown).not.toBe(JSON.stringify(payload)) // 不是那层双编码原文
  })
})

/**
 * D1 的端到端落点：推荐面板（`App.tsx` 的 `lastRecommendationContent` 读取侧解码）。
 *
 * 用**真实双编码帧**驱动完整装配（账户屏 → 模型屏 → 主界面 → 一轮），断言面板渲染卡片
 * 而非停在占位；同时钉住「解码只在读取侧」——持久化内容未被改写。
 */
describe('双编码帧驱动推荐面板（D1：App 读取侧解码）', () => {
  const MODELS = [
    { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-0813', isDefault: false },
    { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
  ]

  const RECO = {
    message: '根据您的对话，为您推荐：',
    hasRecommendation: true,
    categories: ['鞋类', '配饰'],
    products: [
      {
        id: 1,
        name: '专业跑鞋',
        category: '鞋类',
        price: 129.99,
        emoji: '👟',
        reason: '因为你提到「跑步」',
      },
      {
        id: 3,
        name: '无线降噪耳机',
        category: '配饰',
        price: 249.99,
        emoji: '🎧',
        reason: '根据你的偏好「耳机」',
      },
    ],
  }
  /** 工具的**真实返回文本**（单行 JSON，camelCase）。 */
  const RECO_TEXT = JSON.stringify(RECO)

  function recommendRound(): SseEvent[] {
    return [
      runStarted('thread-1', 'run-1'),
      textMessageStart('a1'),
      toolCallStart('tc1', 'recommend_products', 'a1'),
      toolCallArgs('tc1', '{"query":"跑步"}'),
      toolCallEnd('tc1'),
      toolCallResultEncoded('tc1', 'tr1', RECO_TEXT),
      textMessageContent('a1', '为您推荐'),
      textMessageEnd('a1'),
      runFinished('thread-1', 'run-1'),
    ]
  }

  afterEach(() => {
    globalThis.localStorage.clear()
    endSession()
    resetCart()
    dismissToast()
  })

  it('面板渲染推荐卡片（不再是占位），且持久化仍是宿主原样送达的 content', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(recommendRound()),
    })
    restoreFetch = stub.restore

    const { container } = render(createElement(App))
    await userEvent.click(screen.getByRole('button', { name: /Marla/ }))
    await userEvent.click(await screen.findByRole('button', { name: /MiMo/ }))

    // 收到结果前：占位文案
    expect(container.querySelector('.rhint')).not.toBeNull()

    await userEvent.type(screen.getByLabelText('消息'), '推荐一下跑鞋')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))

    await waitFor(() => {
      expect(container.querySelectorAll('.rcard')).toHaveLength(2)
    })
    expect(
      [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent),
    ).toEqual(['专业跑鞋', '无线降噪耳机'])
    // 占位文案已被替换（面板不是恒停在 `.rhint`）
    expect(container.querySelector('.rhint')).toBeNull()

    // 解码**只在读取侧**：持久化的 tool 消息 content 仍是宿主原样送达的双编码值（未被改写）。
    const stored = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla')) ?? '[]',
    ) as Message[]
    const toolMessage = stored.find((message) => message.role === 'tool')
    expect(toolMessage?.toolCallId).toBe('tc1')
    expect(toolMessage?.content).toBe(JSON.stringify(RECO_TEXT))
  })
})

/**
 * D3（design §15.3 + §15.4）：刷新后工具调用栏消失 —— 数据面。
 *
 * 恢复的轮次是**已完成**轮，只有 `durationMs`、没有原始 `startedAt` / `endedAt`，故单独存放、
 * `getRounds()` 按「恢复轮在前、实时轮在后」拼接；`result` 已是**解码后**的值，
 * 恢复路径 MUST NOT 再解码一次（design §15.1「只在边界恰好应用一次」）。
 */
describe('createToolTracker({ initialRounds })：以持久化轮次为初值（D3）', () => {
  /** 一份「上次会话已落库」的轮次快照（形状 = `JSON.stringify(tracker.getRounds())`）。 */
  function restoredRounds(): ToolRound[] {
    return [
      {
        runId: 'run-old',
        toolCalls: [
          {
            toolCallId: 'old-1',
            name: 'search_product',
            argsText: '{"keyword":"跑鞋"}',
            args: { keyword: '跑鞋' },
            result: '找到 3 个商品：\n#1 专业跑鞋',
            status: 'success',
            durationMs: 312,
          },
          {
            toolCallId: 'old-2',
            name: 'recommend_products',
            argsText: '{"query":"跑步"}',
            args: { query: '跑步' },
            // 工具真的返回带引号文本（引号是工具原意）。若恢复路径再解码一次，本值会掉引号。
            result: '"hi"',
            status: 'success',
            durationMs: 87,
          },
        ],
        usage: { inputTokens: 120, outputTokens: 34, totalTokens: 154 },
        finished: true,
      },
    ]
  }

  it('未收到任何事件时恢复轮已可见；实时轮追加在后，findToolCall 两处都查得到', () => {
    const clock = fakeClock(1_000)
    const tracker = createToolTracker({ initialRounds: restoredRounds(), now: clock.now })

    // 这正是刷新后「整块不消失」的机制：不依赖任何新事件
    expect(tracker.getRounds()).toEqual(restoredRounds())
    expect(tracker.findToolCall('old-1')?.tool.durationMs).toBe(312)

    tracker.record({ type: 'RUN_STARTED', runId: 'run-new' })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'new-1', toolCallName: 'add_to_cart' })
    clock.advance(45)
    tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: 'new-1', content: '已加入购物车' })

    const rounds = tracker.getRounds()
    expect(rounds.map((round) => round.runId)).toEqual(['run-old', 'run-new'])
    // 实时轮照常参与「两处都查」
    expect(tracker.findToolCall('new-1')?.tool.durationMs).toBe(45)
    // 实时事件不触碰恢复轮（已完成轮不被新的 RUN_STARTED 改写）
    expect(rounds[0]).toEqual(restoredRounds()[0])
  })

  it('恢复轮的 durationMs 逐字来自快照（不是重建后重新掐表）', () => {
    // 时钟从无关的起点起步并推进：若实现是「重建后重新计时」，绝无可能恰好得到 312 / 87。
    const clock = fakeClock(500_000)
    const tracker = createToolTracker({ initialRounds: restoredRounds(), now: clock.now })
    clock.advance(9_999)

    expect(tracker.getRounds()[0]?.toolCalls.map((call) => call.durationMs)).toEqual([312, 87])
  })

  it('恢复路径不重复解码：result 与快照逐字相同（含「本就带引号」的结果）', () => {
    const tracker = createToolTracker({ initialRounds: restoredRounds() })
    const calls = tracker.getRounds()[0]?.toolCalls ?? []

    expect(calls[0]?.result).toBe('找到 3 个商品：\n#1 专业跑鞋')
    // 若这里再调一次 `decodeToolResultContent`，`"hi"` 会变成 `hi`（把工具本意的引号吃掉）
    expect(calls[1]?.result).toBe('"hi"')
    expect(tracker.findToolCall('old-2')?.tool.result).toBe('"hi"')
  })

  it('再次持久化时两批轮次都在（getRounds 的结果可直接 JSON 往返）', () => {
    const tracker = createToolTracker({ initialRounds: restoredRounds() })
    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'new-1', toolCallName: 'add_to_cart' })

    const roundTripped = JSON.parse(JSON.stringify(tracker.getRounds())) as ToolRound[]
    expect(roundTripped).toHaveLength(2)
    expect(roundTripped[0]).toEqual(restoredRounds()[0])
    expect(roundTripped[1]?.toolCalls[0]?.toolCallId).toBe('new-1')
  })

  it('未传 initialRounds 时行为不变（既有调用点零影响）', () => {
    const tracker = createToolTracker()
    expect(tracker.getRounds()).toEqual([])

    tracker.record({ type: 'TOOL_CALL_START', toolCallId: 'x', toolCallName: 'search_product' })
    expect(tracker.getRounds()).toHaveLength(1)
  })
})

/**
 * D3 的端到端落点（App 装配）：一轮工具调用后落库、刷新（卸载重挂）后原样恢复、
 * 持久化损坏时降级为空而不白屏。
 */
describe('工具调用栏数据持久化（D3：App 写入与刷新恢复）', () => {
  const MODELS = [
    { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-0813', isDefault: false },
    { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
  ]
  const RECO_TEXT = JSON.stringify({ message: '为您推荐', hasRecommendation: true, products: [] })
  const SEARCH_RESULT = '找到 3 个商品：\n#1 专业跑鞋'

  /** 一轮含 `search_product` 与 `recommend_products` 的 SSE（结果以真机形态发出）。 */
  function toolRound(threadId = 'thread-1'): SseEvent[] {
    return [
      runStarted(threadId, 'run-1'),
      textMessageStart('a1'),
      toolCallStart('tc1', 'search_product', 'a1'),
      toolCallArgs('tc1', '{"keyword":"跑鞋"}'),
      toolCallEnd('tc1'),
      toolCallResultEncoded('tc1', 'tr1', SEARCH_RESULT),
      toolCallStart('tc2', 'recommend_products', 'a1'),
      toolCallArgs('tc2', '{"query":"跑步"}'),
      toolCallEnd('tc2'),
      toolCallResultEncoded('tc2', 'tr2', RECO_TEXT),
      textMessageContent('a1', '为您推荐'),
      textMessageEnd('a1'),
      runFinished(threadId, 'run-1', [{ inputTokens: 120, outputTokens: 34, totalTokens: 154 }]),
    ]
  }

  afterEach(() => {
    globalThis.localStorage.clear()
    endSession()
    resetCart()
    dismissToast()
  })

  /** 渲染并等主界面就绪（用于「身份已持久化」的刷新 / 预置场景：App 直接落到主界面）。 */
  async function mountMain(): Promise<HTMLElement> {
    const { container } = render(createElement(App))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    return container
  }

  /** 走完「账户屏 → 模型屏 → 主界面」（用于首次进入场景）。 */
  async function pickAccountAndModel(): Promise<HTMLElement> {
    const { container } = render(createElement(App))
    await userEvent.click(screen.getByRole('button', { name: /Marla/ }))
    await userEvent.click(await screen.findByRole('button', { name: /MiMo/ }))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    return container
  }

  it('一轮工具调用后：工具数据与消息**同批次**落库（无漂移），含工具名 / 参数 / 结果 / 实测耗时', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(toolRound()),
    })
    restoreFetch = stub.restore

    // 观测落库批次：记录每次 `setItem` 的键（仍调用原实现，不改变行为）。
    const writtenKeys: string[] = []
    const originalSetItem = Storage.prototype.setItem
    const setItemSpy = vi
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(function (this: Storage, key: string, value: string): void {
        writtenKeys.push(key)
        originalSetItem.call(this, key, value)
      })

    try {
      const container = await pickAccountAndModel()
      await userEvent.type(screen.getByLabelText('消息'), '推荐跑鞋')
      await userEvent.click(screen.getByRole('button', { name: '发送' }))
      await waitFor(() => expect(container.querySelectorAll('.tool')).toHaveLength(2))

      // 同批次：每条通知里 messages 与 tools 各写一次（计数相同 → 不存在「只落了一个」的漂移）
      const messagesWrites = writtenKeys.filter((key) => key === STORAGE_KEYS.messages('marla')).length
      const toolsWrites = writtenKeys.filter((key) => key === STORAGE_KEYS.tools('marla')).length
      expect(messagesWrites).toBeGreaterThan(0)
      expect(toolsWrites).toBe(messagesWrites)

      const rounds = JSON.parse(
        globalThis.localStorage.getItem(STORAGE_KEYS.tools('marla')) ?? '[]',
      ) as ToolRound[]
      expect(rounds).toHaveLength(1)
      expect(rounds[0]?.runId).toBe('run-1')
      expect(rounds[0]?.toolCalls.map((call) => call.name)).toEqual([
        'search_product',
        'recommend_products',
      ])
      expect(rounds[0]?.toolCalls[0]?.args).toEqual({ keyword: '跑鞋' })
      // 落库的是**解码后**的结果（wire 边界剥过一层），不是宿主发送的双编码原文
      expect(rounds[0]?.toolCalls[0]?.result).toBe(SEARCH_RESULT)
      expect(rounds[0]?.toolCalls[0]?.result).not.toBe(JSON.stringify(SEARCH_RESULT))
      // 实测耗时：真实时钟 → 断言是有限数（精确值由「恢复」用例覆盖）
      expect(typeof rounds[0]?.toolCalls[0]?.durationMs).toBe('number')
    } finally {
      setItemSpy.mockRestore()
    }
  })

  it('刷新（卸载重挂）后工具调用栏原样恢复：工具名 / 耗时一致，数据未被改写', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(toolRound()),
    })
    restoreFetch = stub.restore

    const first = render(createElement(App))
    await userEvent.click(screen.getByRole('button', { name: /Marla/ }))
    await userEvent.click(await screen.findByRole('button', { name: /MiMo/ }))
    await userEvent.type(screen.getByLabelText('消息'), '推荐跑鞋')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))
    await waitFor(() => expect(first.container.querySelectorAll('.tool')).toHaveLength(2))

    const beforeRounds = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.tools('marla')) ?? '[]',
    ) as ToolRound[]
    const beforeDurations = [...first.container.querySelectorAll('.tool .t')].map(
      (element) => element.textContent,
    )

    // 模拟刷新：卸载（订阅随 effect 清理）→ 重新挂载（tracker 以持久化值为初值）
    first.unmount()
    endSession()

    const container = await mountMain()

    await waitFor(() => expect(container.querySelectorAll('.tool')).toHaveLength(2))
    expect(
      [...container.querySelectorAll('.tool .nm')].map((element) => element.textContent),
    ).toEqual(['search_product', 'recommend_products'])
    // 耗时与刷新前**逐字一致**：来自持久化，而不是重建后重新掐表
    expect([...container.querySelectorAll('.tool .t')].map((element) => element.textContent)).toEqual(
      beforeDurations,
    )
    // 重挂后落库的仍是同一份数据（恢复路径没有解码 / 重算）
    const afterRounds = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.tools('marla')) ?? '[]',
    ) as ToolRound[]
    expect(afterRounds).toEqual(beforeRounds)

    // 展开后参数与结果仍在（结果不被再次解码）
    await userEvent.click(screen.getByRole('button', { name: /search_product/ }))
    expect(container.querySelector('.tool-result')?.textContent).toBe(SEARCH_RESULT)
    expect(
      [...container.querySelectorAll('.tool-sec code')].map((element) => element.textContent),
    ).toContain('keyword: "跑鞋"')
  })

  it('恢复的 durationMs 与 usage 逐字来自持久化（固定 777ms / 合计 13），且结果不再解码', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(toolRound()),
    })
    restoreFetch = stub.restore

    // 预置「上次会话留下的」两键：消息里带 assistant.toolCalls + 配对的 tool 结果消息，
    // 工具数据里是固定耗时（任何「重新掐表」都不可能得到 777）。
    writeUsername('marla')
    writeModelId('gpt-4.1')
    globalThis.localStorage.setItem(
      STORAGE_KEYS.messages('marla'),
      JSON.stringify([
        { id: 'u-1', role: 'user', content: '找跑鞋' },
        {
          id: 'a-1',
          role: 'assistant',
          content: '为您找到',
          toolCalls: [
            {
              id: 'seed-1',
              type: 'function',
              function: { name: 'search_product', arguments: '{"keyword":"跑鞋"}' },
            },
          ],
        },
        { id: 't-1', role: 'tool', content: '"找到 3 个商品"', toolCallId: 'seed-1' },
      ] satisfies Message[]),
    )
    globalThis.localStorage.setItem(
      STORAGE_KEYS.tools('marla'),
      JSON.stringify([
        {
          runId: 'run-seed',
          toolCalls: [
            {
              toolCallId: 'seed-1',
              name: 'search_product',
              argsText: '{"keyword":"跑鞋"}',
              args: { keyword: '跑鞋' },
              // 已解码的值，且「本就带引号」——再解码一次会掉引号
              result: '"hi"',
              status: 'success',
              durationMs: 777,
            },
          ],
          usage: { inputTokens: 9, outputTokens: 4, totalTokens: 13 },
          finished: true,
        },
      ] satisfies ToolRound[]),
    )

    const container = await mountMain()

    await waitFor(() => expect(container.querySelectorAll('.tool')).toHaveLength(1))
    expect(container.querySelector('.tool .nm')?.textContent).toBe('search_product')
    expect(container.querySelector('.tool .t')?.textContent).toBe('777ms')

    await userEvent.click(screen.getByRole('button', { name: /search_product/ }))
    expect(container.querySelector('.tool-result')?.textContent).toBe('"hi"')
    expect(container.querySelector('.tool-meta')?.textContent).toContain('合计 13')
  })

  it('损坏的工具数据（非法 JSON）→ 以空开始 + 告警；不抛出、不白屏', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(toolRound()),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    writeModelId('gpt-4.1')
    globalThis.localStorage.setItem(
      STORAGE_KEYS.messages('marla'),
      JSON.stringify([{ id: 'u-1', role: 'user', content: '找跑鞋' }] satisfies Message[]),
    )
    globalThis.localStorage.setItem(STORAGE_KEYS.tools('marla'), '{这不是 JSON')

    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
    try {
      const container = await mountMain()

      // 应用照常渲染到主界面（气泡在），只是工具调用栏降级为「不渲染」
      await waitFor(() =>
        expect([...container.querySelectorAll('.bub')].map((el) => el.textContent)).toContain(
          '找跑鞋',
        ),
      )
      expect(container.querySelectorAll('.tool')).toHaveLength(0)
      expect(
        warnSpy.mock.calls.some((call) => String(call[0]).includes('agui.tools.marla')),
      ).toBe(true)
    } finally {
      warnSpy.mockRestore()
    }
  })
})
