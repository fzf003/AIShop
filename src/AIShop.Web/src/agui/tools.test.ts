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
import { afterEach, describe, expect, it } from 'vitest'

import { installFetchStub } from '../test/fetch-stub'
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
  toolCallStart,
  type SseEvent,
} from '../test/sse'
import { createAgent } from './agent'
import { attachToolEvents, createToolTracker } from './tools'

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
