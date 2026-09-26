/**
 * AG-UI Agent 装配 + 会话 store 用例（tasks.md C4「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` 的：
 * - R1-1「连续两轮的全量重发」：第二轮请求体的 `messages` 含第一轮全部消息，而不是只有新消息；
 * - R3-1 / R3-2（硬契约 3）：每轮 `forwardedProps` 均为 `{username, model}`，且 `model` 取
 *   `GET /models` 响应项的 **`id`**（节键）而非 `model`（wire 名）；
 * - R3-4「两条通道写同一用户名」的 AG-UI 侧：`forwardedProps.username` 与 REST 的 `?username=`
 *   同源于 `currentUsername()`；
 * - R2-3 / R1-2「工具调用消息被完整保留」：持久化同时含 assistant 的 `toolCalls` 与配对的
 *   `role:"tool"` 结果消息（无孤儿），且新建 agent 能按持久化内容原样恢复；
 * - R12-1「threadId 存在但不承担隔离」：换 `threadId` 请求体随之变化，`forwardedProps.username` 不变。
 * - R1 追加条款「推理（reasoning）消息的排除」（D2 / C14）：入站 `REASONING_*` / `THINKING_*` 事件被丢、
 *   恢复种子里的 `role:"reasoning"` 被过滤，`agent.messages` 与本轮 `RunAgentInput.messages` 都不含该角色。
 *
 * 测试走**真实 `@ag-ui/client`**（`HttpAgent`）+ `src/test/sse.ts` 的 SSE 替身 ——
 * SSE 解析与事件应用全部由官方 SDK 完成（R18-2 的证据：本文件不含任何手写 SSE 解析）。
 */
import { BackwardCompatibility_0_0_45, HttpAgent, type Message } from '@ag-ui/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { withUsername } from '../api/http'
import { readMessages, writeUsername } from '../state/session'
import { installFetchStub, type FetchCall, type RouteHandler, type RouteSpec } from '../test/fetch-stub'
import {
  createHangingSseResponse,
  createSseResponse,
  customEvent,
  encodeSse,
  runFinished,
  runStarted,
  SSE_CONTENT_TYPE,
  type SseEvent,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResult,
  toolCallResultEncoded,
  toolCallStart,
} from '../test/sse'
import { AGUI_ENDPOINT, createAgent, FIRST_BYTE_TIMEOUT_MS, lastRecommendationContent } from './agent'
import { getRecoSnapshot, resetRecoContent } from './reco'
import { endSession, getSnapshot, runRound, setModel, startSession, subscribe } from './store'

/** `GET /models` 响应项的替身（R3-2 的数据契约：`id` 是节键、`model` 是 wire 名）。 */
const MODEL_ITEM = { id: 'gpt-4.1', name: 'Mimo', model: 'mimo-v2.5' } as const

/** 请求体形状（`RunAgentInput` 中本工单关心的字段）。 */
interface AguiRequestBody {
  threadId: string
  runId: string
  messages: Message[]
  forwardedProps: { username?: string; model?: string }
}

function bodyOf(call: FetchCall): AguiRequestBody {
  return call.body as AguiRequestBody
}

/** 一轮纯文本对话的 SSE 事件序列。 */
function textRound(threadId: string, runId: string, assistantId: string, reply: string) {
  return [
    runStarted(threadId, runId),
    textMessageStart(assistantId),
    textMessageContent(assistantId, reply),
    textMessageEnd(assistantId),
    runFinished(threadId, runId),
  ]
}

/** 一轮含工具调用（`search_product`）的 SSE 事件序列：assistant 带 `toolCalls` + 配对 tool 结果消息。 */
function toolRound(threadId: string, runId: string, assistantId: string) {
  return [
    runStarted(threadId, runId),
    textMessageStart(assistantId),
    toolCallStart('tc1', 'search_product', assistantId),
    toolCallArgs('tc1', '{"keyword":"跑步鞋"}'),
    toolCallEnd('tc1'),
    toolCallResult('tc1', 'tr1', '找到 1 个商品'),
    textMessageContent(assistantId, '已为您找到'),
    textMessageEnd(assistantId),
    runFinished(threadId, runId),
  ]
}

/**
 * 真机形态的一轮：助手文本结束后、`RUN_FINISHED` 之前推一条 `CUSTOM` 推荐事件
 * （宿主 `RecommendationPushAgent` 的次序；`name` 恒为 `recommendation`，见 `RecommendationPushContent.EventName`）。
 */
function recoRound(
  threadId: string,
  runId: string,
  assistantId: string,
  reply: string,
  value: unknown,
): SseEvent[] {
  return [
    runStarted(threadId, runId),
    textMessageStart(assistantId),
    textMessageContent(assistantId, reply),
    textMessageEnd(assistantId),
    customEvent('recommendation', value),
    runFinished(threadId, runId),
  ]
}

/** 按调用顺序依次回放各轮的 SSE（用尽后重复最后一轮，避免用例漏配时静默变成别的错误）。 */
function scriptRounds(rounds: readonly (readonly SseEvent[])[]): RouteSpec {
  let index = 0
  return () => {
    const events = rounds[Math.min(index, rounds.length - 1)] ?? []
    index += 1
    return createSseResponse(events)
  }
}

/**
 * 新协议推理事件（`REASONING_*`，字段名以 `@ag-ui/core` 的 schema 为准）。
 *
 * 就地构造而不加进 `src/test/sse.ts`：该文件属 C15 的改动范围（约束 A：同文件不并行）。
 */
function reasoningEvents(messageId: string, text: string): SseEvent[] {
  return [
    { type: 'REASONING_START', messageId },
    { type: 'REASONING_MESSAGE_START', messageId, role: 'reasoning' },
    { type: 'REASONING_MESSAGE_CONTENT', messageId, delta: text },
    { type: 'REASONING_MESSAGE_END', messageId },
    { type: 'REASONING_END', messageId },
  ]
}

/** 旧协议推理事件（`THINKING_*`）：SDK 的 `BackwardCompatibility_0_0_45` 会把它映射成 `REASONING_*`。 */
function thinkingEvents(messageId: string, text: string): SseEvent[] {
  return [
    { type: 'THINKING_START', messageId },
    { type: 'THINKING_TEXT_MESSAGE_START', messageId },
    { type: 'THINKING_TEXT_MESSAGE_CONTENT', messageId, delta: text },
    { type: 'THINKING_TEXT_MESSAGE_END', messageId },
    { type: 'THINKING_END', messageId },
  ]
}

/** 真机形态的一轮：助手文本**之前**先来一段推理（D2 就是这样把 `reasoning` 消息带进 messages 的）。 */
function roundWith(prefix: readonly SseEvent[], threadId: string, runId: string, assistantId: string, reply: string) {
  return [
    runStarted(threadId, runId),
    ...prefix,
    textMessageStart(assistantId),
    textMessageContent(assistantId, reply),
    textMessageEnd(assistantId),
    runFinished(threadId, runId),
  ]
}

/** 含新协议推理事件的一轮。 */
function reasoningRound(threadId: string, runId: string, assistantId: string, reply: string): SseEvent[] {
  return roundWith(reasoningEvents('reason-1', '先想想'), threadId, runId, assistantId, reply)
}

/** 含旧协议 `THINKING_*` 事件的一轮。 */
function thinkingRound(threadId: string, runId: string, assistantId: string, reply: string): SseEvent[] {
  return roundWith(thinkingEvents('think-1', '先想想'), threadId, runId, assistantId, reply)
}

/** 含遗留 `role:"reasoning"` 的恢复种子（模拟升级前写进 `localStorage` 的旧数据）。 */
const SEED_WITH_REASONING: readonly Message[] = [
  { id: 'u1', role: 'user', content: '上一轮提问' },
  { id: 'm2', role: 'reasoning', content: '先想想' },
  { id: 'a1', role: 'assistant', content: '上一轮回复' },
]

/**
 * 把 SDK 的前向兼容中间件塞到链首（**最外层**）——复现 design §15.2 描述的真实层级：
 * 兼容层在外、`dropReasoningEvents`（经 `use()` 追加）在内。
 *
 * `middlewares` 在类型声明里标了 `private`，但运行时就是一个普通数组；测试要在**不改产品代码**的
 * 前提下摆出这一层级，只能这样访问（本仓安装的 0.0.59 因版本门控默认不挂载它，故必须手工加）。
 */
function prependCompatMiddleware(agent: HttpAgent): void {
  const internals = agent as unknown as { middlewares: unknown[] }
  internals.middlewares.unshift(new BackwardCompatibility_0_0_45())
}

let restoreFetch: (() => void) | null = null
let warnSpy: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  localStorage.clear()
  warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
})

afterEach(() => {
  endSession()
  restoreFetch?.()
  restoreFetch = null
  warnSpy.mockRestore()
})

describe('agent.ts：每轮全量重发 + 身份/模型注入（R1-1、R3-1、R3-2、R3-4）', () => {
  it('第一轮完成后发第二轮：第二轮请求体的 messages 含第一轮全部消息（R1-1）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '第一轮回复'),
        textRound('t', 'r2', 'a2', '第二轮回复'),
      ]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: MODEL_ITEM.id })

    await runRound('第一轮')
    // 第一轮只带本轮新消息（基线，证明增量确实发生了）
    expect(bodyOf(stub.callsTo('/agui')[0]!).messages.map((m) => m.content)).toEqual(['第一轮'])

    await runRound('第二轮')

    const calls = stub.callsTo('/agui')
    expect(calls).toHaveLength(2)
    const second = bodyOf(calls[1]!)
    // 全量快照：第一轮的用户消息与助手回复**都在**，不是只有本轮新消息
    expect(second.messages.map((m) => m.role)).toEqual(['user', 'assistant', 'user'])
    expect(second.messages.map((m) => m.content)).toEqual(['第一轮', '第一轮回复', '第二轮'])
  })

  it('连续两轮请求体的 forwardedProps 均为 {username, model}，model 取 id 而非 wire 名（R3-1、R3-2）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '一'),
        textRound('t', 'r2', 'a2', '二'),
      ]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    // 选中 /models 响应项 → 传的是它的 `id`（节键）
    startSession({ model: MODEL_ITEM.id })

    await runRound('第一轮')
    await runRound('第二轮')

    const bodies = stub.callsTo('/agui').map(bodyOf)
    expect(bodies).toHaveLength(2)
    // 「每轮都显式传」：两轮一字不差
    for (const body of bodies) {
      expect(body.forwardedProps).toEqual({ username: 'marla', model: MODEL_ITEM.id })
    }
    // 绝不能是响应项的 `model`（真实 wire 名）
    expect(bodies.map((body) => body.forwardedProps.model)).not.toContain(MODEL_ITEM.model)
  })

  it('切模型：下一轮生效，在途本轮不受影响（R6 追加条款「对话中切换于下一轮生效」）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '一'),
        textRound('t', 'r2', 'a2', '二'),
      ]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: 'gpt-4.1' })
    await runRound('第一轮')

    setModel('deepseek')
    await runRound('第二轮')

    const bodies = stub.callsTo('/agui').map(bodyOf)
    expect(bodies[0]!.forwardedProps.model).toBe('gpt-4.1')
    expect(bodies[1]!.forwardedProps.model).toBe('deepseek')
  })

  it('切模型（真并发）：在途本轮用发起时的旧 model，下一轮才取新值（R6 追加条款「对话中切换于下一轮生效」）', async () => {
    // 第一轮请求到达后**挂住不返回**，制造「本轮仍在途」的窗口 —— 用测试可控的 deferred gate，
    // 不用 `createHangingSseResponse`（它永不 close，会挂死后面的 `await first`）。
    let releaseFirst!: () => void
    const firstGate = new Promise<void>((resolve) => {
      releaseFirst = resolve
    })

    const rounds = scriptRounds([
      textRound('t', 'r1', 'a1', '一'),
      textRound('t', 'r2', 'a2', '二'),
    ]) as RouteHandler

    let served = 0
    const stub = installFetchStub({
      '/agui': async (ctx) => {
        // 只让第一轮挂起（served++ 后为 1）；第二轮直接放行。
        if (served++ === 0) await firstGate
        return rounds(ctx)
      },
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: 'gpt-4.1' })

    // 关键：**不 await** 地发起第一轮 —— 本轮进入在途状态。
    const first = runRound('第一轮')

    // 等第一轮请求确实发出（此时其 forwardedProps 已按发起时的 model 构造并送出）。
    await vi.waitFor(() => expect(stub.callsTo('/agui')).toHaveLength(1))

    // 在途轮尚未返回时切换模型：不得回溯改写已送出的第一轮请求体。
    setModel('deepseek')

    // 放行第一轮，让它正常收尾。
    releaseFirst()
    await first

    await runRound('第二轮')

    const bodies = stub.callsTo('/agui').map(bodyOf)
    expect(bodies).toHaveLength(2)
    // 在途本轮（第一轮）用的是**发起时**的旧 model（本用例的核心）。
    expect(bodies[0]!.forwardedProps.model).toBe('gpt-4.1')
    // 下一轮才取新值。
    expect(bodies[1]!.forwardedProps.model).toBe('deepseek')
  })

  it('身份单一来源：forwardedProps.username 与 REST 的 ?username= 同源（R3-4 的 AG-UI 侧）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '一'),
        textRound('t', 'r2', 'a2', '二'),
      ]),
    })
    restoreFetch = stub.restore

    writeUsername('steve')
    startSession({ model: MODEL_ITEM.id })
    await runRound('你好')

    // AG-UI 面与 REST 面必须写同一个用户名（steve，且不是硬编码的 marla）
    expect(bodyOf(stub.callsTo('/agui')[0]!).forwardedProps.username).toBe('steve')
    expect(withUsername('/cart')).toBe('/cart?username=steve')

    // 换一个账户重来 → 请求体随之改变（实现里若写死字面量，本断言必红）
    endSession()
    writeUsername('fzf003')
    startSession({ model: MODEL_ITEM.id })
    await runRound('你好')

    expect(bodyOf(stub.callsTo('/agui')[1]!).forwardedProps.username).toBe('fzf003')
  })

  it('同一 username 换 threadId 重发：threadId 为新值、forwardedProps.username 不变（R12-1）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r1', 'a1', '一'), textRound('t', 'r2', 'a2', '二')]),
    })
    restoreFetch = stub.restore

    const oldSession = createAgent({
      username: 'marla',
      model: MODEL_ITEM.id,
      threadId: 'thread-old',
    })
    await oldSession.runRound('第一轮')

    const newSession = createAgent({
      username: 'marla',
      model: MODEL_ITEM.id,
      threadId: 'thread-new',
    })
    await newSession.runRound('第二轮')

    const bodies = stub.callsTo('/agui').map(bodyOf)
    expect(bodies[0]!.threadId).toBe('thread-old')
    expect(bodies[1]!.threadId).toBe('thread-new')
    // threadId 换了，身份没有换（服务端按用户名归属会话，客户端不依赖 threadId 隔离）
    expect(bodies[1]!.forwardedProps.username).toBe('marla')
  })
})

describe('store.ts：持久化完整性 + 恢复 + 重建（R2-3、R1-2、R12）', () => {
  it('工具调用轮次：持久化同时保留 assistant.toolCalls 与配对的 role:"tool" 结果消息（R2-3、R1-2）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([toolRound('t', 'r1', 'a1')]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: MODEL_ITEM.id })
    await runRound('推荐跑步鞋')

    const stored = readMessages('marla')
    // 落库的就是渲染用的那一份（无裁剪、无投影）
    expect(stored).toEqual(getSnapshot().messages)

    const assistant = stored.find((m) => m.role === 'assistant')
    const tool = stored.find((m) => m.role === 'tool')

    expect(assistant?.toolCalls?.[0]?.id).toBe('tc1')
    expect(assistant?.toolCalls?.[0]?.function.name).toBe('search_product')
    // 配对无孤儿：有 toolCalls 就有结果消息，且 toolCallId 对得上
    expect(tool?.toolCallId).toBe('tc1')
    expect(tool?.content).toBe('找到 1 个商品')
    expect(stored.indexOf(tool!)).toBeGreaterThan(stored.indexOf(assistant!))
  })

  it('刷新（新建 agent + 持久化历史作 initialMessages）恢复的消息条数与内容一致（R1-2 数据面）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r1', 'a1', '第一轮回复')]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: MODEL_ITEM.id })
    await runRound('第一轮')

    const persisted = readMessages('marla')
    expect(persisted.length).toBeGreaterThan(0)

    // 模拟刷新：新建 agent 并以持久化内容为初始消息
    const restored = createAgent({
      username: 'marla',
      model: MODEL_ITEM.id,
      threadId: 'thread-after-refresh',
      initialMessages: persisted,
    })

    expect(restored.getMessages()).toHaveLength(persisted.length)
    expect(restored.getMessages()).toEqual(persisted)
  })

  it('切账户：重建 agent 后新账户为空历史，原账户持久化历史不受影响（R2-1 的 store 侧）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r1', 'a1', 'marla 的回复')]),
    })
    restoreFetch = stub.restore

    writeUsername('marla')
    startSession({ model: MODEL_ITEM.id })
    await runRound('marla 的消息')
    const marlaHistory = readMessages('marla')
    expect(marlaHistory).toHaveLength(2)

    // 切到 steve → 重建 agent（threadId 随之重置），历史以 steve 自己的键为准（空）
    writeUsername('steve')
    startSession({ model: MODEL_ITEM.id })
    expect(getSnapshot().messages).toEqual([])

    // marla 的历史原样保留
    expect(readMessages('marla')).toEqual(marlaHistory)
  })

  it('运行态：运行中出现 isRunning=true，结束后为 false，且 store 会通知订阅者（C4 步骤 3）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r1', 'a1', '回复')]),
    })
    restoreFetch = stub.restore

    const seenRunning: boolean[] = []
    const unsubscribe = subscribe(() => {
      seenRunning.push(getSnapshot().isRunning)
    })

    writeUsername('marla')
    startSession({ model: MODEL_ITEM.id })
    await runRound('你好')

    expect(seenRunning).toContain(true)
    expect(getSnapshot().isRunning).toBe(false)
    expect(getSnapshot().messages).toHaveLength(2)

    unsubscribe()
  })
})

describe('agent.ts：推理消息不得进入 agent.messages（R1 追加条款 / D2 / C14）', () => {
  it('基线复现：裸 HttpAgent（未挂过滤器）回放推理事件流 → messages 出现 role:"reasoning" 且第二轮请求体带上它（D2 根因）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        reasoningRound('t', 'r1', 'a1', '第一轮回复'),
        textRound('t', 'r2', 'a2', '第二轮回复'),
      ]),
    })
    restoreFetch = stub.restore

    // 不经 createAgent：这就是修复前线上跑的那条路径（SDK 的 defaultApplyEvents 原样应用推理事件）
    const bare = new HttpAgent({ url: AGUI_ENDPOINT, threadId: 't' })
    await bare.runAgent()

    // 推理事件被落成一条 role:"reasoning" 的消息，混在助手文本之前
    expect(bare.messages.map((message) => message.role)).toEqual(['reasoning', 'assistant'])
    expect(bare.messages[0]?.content).toBe('先想想')

    // 第 2 轮的请求体是**跑之前**的 messages 快照 → 它把遗留的推理消息整条发了出去
    // （.NET 宿主 MapChatRole 不识别 reasoning → 线上即 HTTP 500）
    await bare.runAgent()
    expect(bodyOf(stub.callsTo('/agui')[1]!).messages.map((message) => message.role)).toEqual([
      'reasoning',
      'assistant',
    ])
  })

  it('经 createAgent 回放同一事件流 → 无 reasoning 消息，助手文本与运行完成不受影响（R1「入站事件过滤」）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([reasoningRound('t', 'r1', 'a1', '第一轮回复')]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't' })
    let runFinishedCount = 0
    session.agent.subscribe({
      onRunFinishedEvent: () => {
        runFinishedCount += 1
      },
    })

    await session.runRound('第一轮')

    // 不变量：消息序列里只有对话角色（推理消息**根本不产生**）
    expect(session.getMessages().map((message) => message.role)).toEqual(['user', 'assistant'])
    // 过滤没有误伤：助手文本完整、RUN_FINISHED 照常到达、运行态正常收尾
    expect(session.getMessages().map((message) => message.content)).toEqual(['第一轮', '第一轮回复'])
    expect(runFinishedCount).toBe(1)
    expect(session.isRunning()).toBe(false)
  })

  it('宿主发旧协议 THINKING_* 事件时同样被丢（design §15.2「为什么必须同时丢 THINKING_*」）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([thinkingRound('t', 'r1', 'a1', '第一轮回复')]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't' })
    // 兼容层（最外层）+ 本过滤器（最内层）：这是 design §15.2 描述的真实形态。
    // **这一行是本用例的判别力来源** —— 裸 `THINKING_*` 在当前 SDK 版本下本来就不会产生消息，
    // 没有兼容中间件时本用例会退化成「恒绿」，见下一条反证。
    prependCompatMiddleware(session.agent)

    await session.runRound('第一轮')

    expect(session.getMessages().map((message) => message.role)).toEqual(['user', 'assistant'])
    expect(session.getMessages().map((message) => message.content)).toEqual(['第一轮', '第一轮回复'])
  })

  it('反证（上一条）：只有兼容中间件、没有本过滤器时，THINKING_* 会被映射成 reasoning 消息并混进 messages', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([thinkingRound('t', 'r1', 'a1', '第一轮回复')]),
    })
    restoreFetch = stub.restore

    // 裸 agent + 兼容中间件 = 「只丢 REASONING_*、不丢 THINKING_*」的等价形态
    const bare = new HttpAgent({ url: AGUI_ENDPOINT, threadId: 't' })
    prependCompatMiddleware(bare)
    await bare.runAgent()

    expect(bare.messages.map((message) => message.role)).toEqual(['reasoning', 'assistant'])
  })

  it('恢复种子过滤：带 role:"reasoning" 的 initialMessages 既不进 messages，也不出现在本轮 RunAgentInput.messages（R1「恢复种子过滤」）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r2', 'a2', '本轮回复')]),
    })
    restoreFetch = stub.restore

    const session = createAgent({
      username: 'marla',
      model: MODEL_ITEM.id,
      threadId: 't',
      initialMessages: SEED_WITH_REASONING,
    })

    // 恢复进来的序列里遗留推理消息已被剔除（只留 user + assistant）
    expect(session.getMessages().map((message) => message.role)).toEqual(['user', 'assistant'])

    await session.runRound('本轮提问')

    // 本轮真正发出去的角色序列：没有 reasoning（有的话 .NET 宿主 MapChatRole 会抛 → HTTP 500）
    const body = bodyOf(stub.callsTo('/agui')[0]!)
    expect(body.messages.map((message) => message.role)).toEqual(['user', 'assistant', 'user'])
    // 反证「不存在」：其余内容原样保留（不是把种子整体丢掉换来的绿）
    expect(body.messages.map((message) => message.content)).toEqual(['上一轮提问', '上一轮回复', '本轮提问'])
  })

  it('只修入站不够：不加种子过滤时，遗留 reasoning 消息确实会被发出去（D2 裁决的依据）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r2', 'a2', '本轮回复')]),
    })
    restoreFetch = stub.restore

    // 绕过 createAgent 的种子过滤（等价于「只做入站事件过滤」的半修状态）
    const bare = new HttpAgent({
      url: AGUI_ENDPOINT,
      threadId: 't',
      initialMessages: [...SEED_WITH_REASONING],
    })
    await bare.runAgent()

    expect(bodyOf(stub.callsTo('/agui')[0]!).messages.map((message) => message.role)).toEqual([
      'user',
      'reasoning',
      'assistant',
    ])
  })
})

/**
 * spec R6「CUSTOM 推荐事件不产生消息与工具调用栏条目」+ R7 的**协议层搬运**（tasks.md F3）。
 *
 * 本层只验证「`CUSTOM` 事件 → 推荐 store」的搬运：`value` 的归一化、非法形状不崩、不套多编码解码
 * 已在 `reco.test.ts`（F2）钉死，此处不重复。
 *
 * 依旧回放**真实 `@ag-ui/client`**（R18-2）：事件应用逻辑不在这里重写。
 */
describe('agent.ts：CUSTOM 事件 → 推荐 store（R6-1、R7）', () => {
  // 推荐 store 是模块级单例：每个用例后清空，避免与用例内预置的原值串味到后续文件级用例。
  afterEach(() => {
    resetRecoContent()
  })

  /** 宿主推的 `value` 形状：推荐结果**对象**（与持久化 `agui.reco.{username}` 同形）。 */
  const RECO = {
    message: '为你推荐',
    products: [
      { id: 1, name: '跑步鞋 A', reason: '适合日常跑步' },
      { id: 2, name: '跑步鞋 B', reason: '缓震更好' },
    ],
  }

  it('回放含 CUSTOM 的一轮 → store 快照为该 value 的归一化文本（正向锚点：无 CUSTOM 的一轮一字不写）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '第一轮回复'),
        recoRound('t', 'r2', 'a2', '第二轮回复', RECO),
      ]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't' })
    resetRecoContent()

    // 正向锚点：本轮**没有** CUSTOM 帧 → 一个字都不写（主断言因此不可能靠空转变绿）
    await session.runRound('第一轮')
    expect(getRecoSnapshot()).toBeNull()

    await session.runRound('第二轮')

    // 主断言：`value` 经 `setRecoFromCustomEvent` 归一化后落在 store 上（对象 → JSON 文本）
    const snapshot = getRecoSnapshot()
    expect(snapshot).toBe(JSON.stringify(RECO))
    expect(JSON.parse(snapshot as string)).toEqual(RECO)
  })

  it('CUSTOM 不产生消息：本轮增量只有自身的 user + assistant，消息内容不含推荐负载（R6-1）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([
        textRound('t', 'r1', 'a1', '第一轮回复'),
        recoRound('t', 'r2', 'a2', '第二轮回复', RECO),
      ]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't' })

    await session.runRound('第一轮')
    // `getMessages()` 返回的就是 SDK 持有的那个数组（会被后续轮次原地追加）——先取**数值**快照。
    const beforeCount = session.getMessages().length
    expect(session.getMessages().map((message) => message.role)).toEqual(['user', 'assistant'])

    await session.runRound('第二轮')

    // 长度与角色序列：第二轮只多了本轮自己的 user + assistant —— CUSTOM 一帧都没进来
    const after = session.getMessages()
    expect(after.map((message) => message.role)).toEqual(['user', 'assistant', 'user', 'assistant'])
    expect(after).toHaveLength(beforeCount + 2)
    expect(after.map((message) => message.content)).toEqual([
      '第一轮',
      '第一轮回复',
      '第二轮',
      '第二轮回复',
    ])
    // 反证「不存在」（不只靠长度，长度可能被等量替换骗过）：推荐负载的任何片段都不在消息里
    const serialized = JSON.stringify(after)
    expect(serialized).not.toContain('为你推荐')
    expect(serialized).not.toContain('跑步鞋 A')
    expect(serialized).not.toContain('recommendation')
  })

  it('本轮无 CUSTOM 帧 → store 保持原值（不被清零，沿用「不在轮次之间清空」口径）', async () => {
    const stub = installFetchStub({
      '/agui': scriptRounds([textRound('t', 'r1', 'a1', '闲聊回复')]),
    })
    restoreFetch = stub.restore

    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't' })
    resetRecoContent('上一次推送的推荐')

    await session.runRound('你好')

    expect(getRecoSnapshot()).toBe('上一次推送的推荐')
  })
})

/**
 * C2：挂起兜底（首字节超时）。
 *
 * **要解的形态**：服务端建立 SSE 连接后**既不产出事件、也不结束、也不报错**。此时
 * `@ag-ui/client` 的读流会一直挂着 → `agent.isRunning` 恒为 `true` → `ChatPanel` 的发送按钮
 * （`disabled={isRunning}`）永久禁用，用户只能刷新页面（真机 Mimo 事故的表现之一）。
 *
 * **⚠️ 这里与工单原文有一处偏离，理由是真机实测**：C2 工单要求的验证构造是「只发部分帧、
 * 不发 RUN_FINISHED、也不 close」。但实测表明「发出若干帧后长时间静默」**正是正常行为** ——
 * 一轮对话服务端约 28 秒，其中 19.5 秒是记忆提取，那段期间一个事件都不发。若把这种形态判为
 * 挂起，就会误杀正常请求（误杀的表现与真实故障一模一样，只会更难排查）。
 * 故兜底改为只对**首字节**计时，本用例也相应构造「**一个帧都不发**」的替身 —— 那才是真正的挂起。
 *
 * 本用例是**反证导向**的：撤销 `agent.ts` 的首字节兜底后，本用例必须变红
 * （`isRunning()` 恒 `true`）。
 */
describe('agent.ts：C2 挂起兜底（服务端连首字节都不发时 isRunning 必须复位）', () => {
  it('首字节超时 → 自动中止本轮、isRunning 复位且通知订阅者', async () => {
    vi.useFakeTimers()
    try {
      const stub = installFetchStub({
        // 空事件数组 + 不关闭流 = 连接建立了但一个字节都不发（真正的挂起）
        [AGUI_ENDPOINT]: () => createHangingSseResponse([]),
      })
      restoreFetch = stub.restore

      const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't-hang' })

      let notified = 0
      session.subscribe(() => {
        notified += 1
      })

      // 先前置一个 catch：中止会让本轮以 AbortError 收尾，避免被 vitest 记为 unhandled rejection
      const settled = session.runRound('你好').catch((error: unknown) => error)

      await vi.advanceTimersByTimeAsync(FIRST_BYTE_TIMEOUT_MS + 1_000)
      await settled

      // 停止兜底后才可能置 false —— 这正是「按钮恢复可用」的数据面
      expect(session.isRunning()).toBe(false)
      expect(notified).toBeGreaterThan(0)
    } finally {
      vi.useRealTimers()
    }
  })

  it('首字节及时到达 → 兜底撤表，后续长时间静默也不中止本轮（不误杀后处理慢的正常请求）', async () => {
    vi.useFakeTimers()
    try {
      // 发出首帧后一直静默、也不关闭 —— 这正是真机上「记忆提取 19.5 秒」的形态
      const stub = installFetchStub({
        [AGUI_ENDPOINT]: () =>
          createHangingSseResponse([
            runStarted('t-slow', 'r-slow'),
            textMessageStart('a-slow'),
            textMessageContent('a-slow', '部分回复'),
          ]),
      })
      restoreFetch = stub.restore

      const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't-slow' })

      // 刻意不等本轮收尾（替身本就不收尾），只挂一个 catch 防 unhandled rejection
      session.runRound('你好').catch(() => undefined)

      // 首字节已到 → 撤表；此后推进远超阈值的时长也不应中止
      await vi.advanceTimersByTimeAsync(FIRST_BYTE_TIMEOUT_MS * 3)

      // 关键断言：本轮**没有**被兜底误杀 —— 流未收尾，故仍处于运行中
      expect(session.isRunning()).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})

/**
 * L5：会话销毁 —— 在途轮的副作用必须随会话一起消失。
 *
 * **要解的形态**：一轮在途（真机实测单轮 6–11 秒）时退出登录 / 切账户。`createAgent` 里
 * `agent.subscribe({...})` 的返回值原先**被丢弃**，`endSession` 只退订了 store 侧 —— 旧 agent 的
 * 订阅仍活着，它晚到的 `CUSTOM` 经 `setRecoFromCustomEvent` 写进**模块级全局**推荐 store，于是
 * ① 新账户面板被上一账户的负载顶掉；② 随后一次 store 通知又把它写进新账户的 `agui.reco.{username}`
 * （违反 spec「退出只清当前账户」）。
 *
 * `dispose()` 的三步各挡一段，但**只有前两步**能由本文件的单测锁定：
 * - **中止在途轮**（`abortRun`）→ 用例 a：旧轮的事件不再送达；
 * - **断开订阅**（`unsubscribe`）→ 用例 b：销毁后 SDK 的后续派发不再触达本会话（走活引用）；
 * - **`disposed` 守卫** → **单测锁定不了**（原因见文末说明），靠真机验证。
 *
 * ⚠️ **2026-09-21 记一次误判与两次更正（本组用例最值得回头看的地方）**：
 * ① 先据 SDK 源码推断「`abortRun()` 会派发 `onRunFailed`，故用户切账户会看到假的『网络异常』」；
 * ② 随后写用例验证，却用 fake timers 只推进 0ms —— **异步链没走完**，断言「未弹提示」通过，
 *    于是**推翻了①**，还写下「该现象未能复现」；
 * ③ **真机复现把①又推了回来** —— 在途轮中点退出，账户页**确实**弹出「网络异常，请稍后重试」。
 *
 * 教训：**单测替身在 abort 路径上与真实网络行为不同**（替身下本轮正常 resolve、`onRunFailed`
 * 不派发），所以单测「绿」既不能证明没问题、也不能证明问题不存在 —— **产品行为以真机为准**。
 *
 * 用例 a、b 都是**反证导向**的：整块去掉 `dispose()` → 用例 a 红；去掉 `unsubscribe()` → 用例 b 红。
 */
describe('agent.ts：L5 会话销毁（在途轮不得污染已切换的账户）', () => {
  /**
   * 自建 SSE 流：测试持有 controller，可精确控制「事件何时到达」—— 本组用例的核心。
   * 现成的 `createHangingSseResponse` 只能一次性入队，构造不出「销毁之后才到」这个窗口。
   *
   * 流被中止后再 `push` 会**同步抛错**（`Controller is already closed`）—— 这不是缺陷，正是
   * 「连接已断」的可观测证据，用例 a 据此断言。
   */
  function installManualSse(): { push: (chunk: string) => void } {
    const handle = { push: (_chunk: string): void => undefined }
    const stub = installFetchStub({
      [AGUI_ENDPOINT]: () =>
        new Response(
          new ReadableStream<Uint8Array>({
            start(controller) {
              handle.push = (chunk) => controller.enqueue(new TextEncoder().encode(chunk))
            },
          }),
          { status: 200, headers: { 'Content-Type': SSE_CONTENT_TYPE } },
        ),
    })
    restoreFetch = stub.restore
    return handle
  }

  /** 起一轮并推进到「首字节已到、本轮在途」的状态。 */
  async function startInFlightRound(sse: { push: (chunk: string) => void }, threadId: string): Promise<void> {
    startSession({ model: MODEL_ITEM.id })
    // 前置 catch：销毁会中止本轮，避免被 vitest 记为 unhandled rejection
    runRound('你好').catch(() => undefined)
    await vi.advanceTimersByTimeAsync(0)
    sse.push(encodeSse(runStarted(threadId, `${threadId}-r1`)))
    await vi.advanceTimersByTimeAsync(0)
  }

  it('a. endSession 中止在途轮：旧轮的事件不再送达，推荐 store 不被污染', async () => {
    vi.useFakeTimers()
    try {
      resetRecoContent()
      writeUsername('marla')
      const sse = installManualSse()
      await startInFlightRound(sse, 't-l5a')

      // 退出登录 / 切账户（`closeToAccount` 走的就是这一句）
      endSession()

      // 断言 1「连接已断」：旧轮的事件**不可能**再送达 —— 这是「不污染」的机制本身
      expect(() =>
        sse.push(encodeSse(customEvent('recommendation', { message: '为你精选', products: [] }))),
      ).toThrow()
      await vi.advanceTimersByTimeAsync(0)

      // 断言 2：面板数据源没有被上一账户的负载写入
      expect(getRecoSnapshot()).toBeNull()
    } finally {
      vi.useRealTimers()
    }
  })

  it('b. dispose 断开 SDK 订阅：旧 agent 后续的派发不再触达本会话', async () => {
    const session = createAgent({ username: 'marla', model: MODEL_ITEM.id, threadId: 't-l5b' })

    let notified = 0
    session.subscribe(() => {
      notified += 1
    })

    session.dispose()

    // `setMessages` 遍历的是 SDK 的 `this.subscribers` **活引用**（不是 `runAgent` 开头那份快照），
    // 因此退订在这条路径上必须生效 —— 这正是 `dispose()` 里 `unsubscribe()` 的作用面。
    session.agent.setMessages([{ id: 'm-l5b', role: 'user', content: '销毁之后' }])
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(notified).toBe(0)
  })

  /**
   * 关于「`disposed` 守卫」为什么**没有**对应用例（2026-09-21 实测后的决定）。
   *
   * **现象与修复**：在途轮中点「退出」→ 账户选择页弹出「网络异常，请稍后重试」。根因是
   * `dispose()` 里的 `abortRun()` **会**派发本轮的 `onRunFailed`，而 `unsubscribe()` 拦不住它
   * —— 本轮派发走的是 `runAgent` 开头拍下的订阅者**快照**。修法是 `disposed` 标记，四个回调自查。
   * **真机复验已通过**：加守卫前 `hasNetworkError: true`，加守卫后 `false`（「在途」由等待态的
   * 「正在思考中…」占位气泡确认）。
   *
   * **为什么写不出单测**：本文件的 SSE 替身构造下，中止后本轮 `runAgent()` **正常 resolve**
   * （实测 `outcome: 'RESOLVED'`、`toastCalls: 0`），`onRunFailed` **不被派发** —— 于是任何
   * 「断言未弹提示」的用例都**恒为真**，属于空转断言，留着只会制造「已覆盖」的假象，故删除。
   * 替身与真实网络在 abort 路径上的这一差异本身就值得记住：
   * **这条不变量靠真机验证守，不靠本文件的单测。**
   */
})

/**
 * L6：推荐三源的**覆盖时序**。
 *
 * spec R9-1 要求「后到者胜」（按**到达顺序**覆盖）。而三源原先**不在同一时序上**：
 * - `CUSTOM` 在协议层 `onCustomEvent` 里**同步**写 store；
 * - 工具结果要等 React 重渲染后由 `App.tsx` 的 `useEffect([messages])` 才写。
 *
 * 于是「模型先调 `recommend_products`、宿主随后推 CUSTOM」这一真机次序下，**更早的工具结果可能
 * 顶掉更晚的 CUSTOM**（`App.tsx` 的 `toolRecoRef` 注释已承认该风险，当时只用「值比较」缓解 ——
 * 两源内容相同时没事，**不同**时仍会顶掉）。真实触发条件是 React 18 的批次合并 / 并发渲染让那个
 * effect 晚于 CUSTOM 派发；**jsdom + `act()` 会在 await 点及时 flush，所以复现不出来** ——
 * 这正是盘点所说「`waitFor` 冲洗掩盖了时序」。
 *
 * 修法因此落在**结构**上：把工具结果的派生也搬进协议层（`onMessagesChanged` 与 `onCustomEvent`
 * 同为**同步**派发），两源因此落在同一条时序上，**到达顺序即写入顺序**。
 *
 * 本用例锁定该结构：**只发工具结果（不含 CUSTOM）的一轮，store 也必须被写入**。
 * 反证：把工具结果的派生退回 `App.tsx` 的 effect，本用例必须变红。
 */
describe('agent.ts：L6 工具结果来源在协议层同步写入（与 CUSTOM 同一时序）', () => {
  it('轮次内的 recommend_products 工具结果 → 写入推荐 store', async () => {
    resetRecoContent()
    writeUsername('marla')

    const payload = JSON.stringify({
      message: '工具结果',
      hasRecommendation: true,
      products: [
        {
          id: 11,
          name: '工具结果里的商品',
          category: '测试',
          price: 1,
          emoji: '🎁',
          reason: '因为你提到「测试」',
        },
      ],
    })

    const stub = installFetchStub({
      [AGUI_ENDPOINT]: scriptRounds([
        [
          runStarted('t-l6', 'r-l6'),
          textMessageStart('a-l6'),
          toolCallStart('tc1', 'recommend_products', 'a-l6'),
          toolCallArgs('tc1', '{}'),
          toolCallEnd('tc1'),
          toolCallResultEncoded('tc1', 'tr1', payload),
          textMessageContent('a-l6', '好的'),
          textMessageEnd('a-l6'),
          runFinished('t-l6', 'r-l6'),
        ],
      ]),
    })
    restoreFetch = stub.restore

    startSession({ model: MODEL_ITEM.id })
    await runRound('推荐点东西')

    expect(getRecoSnapshot()).not.toBeNull()
  })

  /**
   * T12：**两源内容逐字节不同**时，store 最终必须是【后到】的那一份。
   *
   * 盘点 T12 原文：「『同轮两源』只在**语义**层断言，**逐字节不同**时面板最终显示哪一份未测」——
   * 既有用例（`AguiRecommendationPushTests`）只证明了两个负载**语义等价**，而真机上它们可能
   * **逐字节不同**（转义形态差异、模型多次调工具等）。L6 之前更糟：两源不同时序，谁胜取决于
   * React 何时 flush。
   *
   * L6 把工具结果搬进协议层后，两源同为 SDK **同步**派发 —— 于是可以在这里直接断言
   * **「到达顺序 = 写入顺序」**：本用例让工具结果先到、`CUSTOM` 后到，store 必须是 `CUSTOM` 的文本。
   *
   * 反证：把用例里 `CUSTOM` 与工具结果的**顺序对调**（CUSTOM 先到），断言必须变红 —— 那时
   * 后到的是工具结果，store 应是它的文本。
   */
  it('T12：两源内容不同时，store 是【后到】的 CUSTOM（到达顺序即写入顺序）', async () => {
    resetRecoContent()
    writeUsername('marla')

    const toolPayload = JSON.stringify({
      message: '工具结果的文案',
      hasRecommendation: true,
      products: [
        {
          id: 11,
          name: '工具结果里的商品',
          category: '测试',
          price: 1,
          emoji: '🎁',
          reason: '因为你提到「测试」',
        },
      ],
    })
    const customPayload = {
      message: 'CUSTOM 的文案',
      hasRecommendation: true,
      products: [
        {
          id: 22,
          name: 'CUSTOM 里的商品',
          category: '测试',
          price: 2,
          emoji: '🎁',
          reason: '因为你提到「测试」',
        },
      ],
    }

    const stub = installFetchStub({
      [AGUI_ENDPOINT]: scriptRounds([
        [
          runStarted('t-t12', 'r-t12'),
          textMessageStart('a-t12'),
          toolCallStart('tc1', 'recommend_products', 'a-t12'),
          toolCallArgs('tc1', '{}'),
          toolCallEnd('tc1'),
          toolCallResultEncoded('tc1', 'tr1', toolPayload),
          // 后到者：CUSTOM 在工具结果之后
          customEvent('recommendation', customPayload),
          textMessageContent('a-t12', '好的'),
          textMessageEnd('a-t12'),
          runFinished('t-t12', 'r-t12'),
        ],
      ]),
    })
    restoreFetch = stub.restore

    startSession({ model: MODEL_ITEM.id })
    await runRound('推荐点东西')

    expect(getRecoSnapshot()).toBe(JSON.stringify(customPayload))
  })

  /**
   * T20：**多条 `recommend_products` 结果**时取哪一条。
   *
   * 盘点 T20 原文：「『两条 `recommend_products` 结果、后者非法时保留前者』无用例」。
   * 面板侧「非法 → 保留上一次」已有覆盖（`RecoPanel.test.tsx:186/198`），缺的是**协议层这一跳**：
   * `lastRecommendationContent` 取的是**消息序最后一条**（不是「最近一条合法的」）——
   * 它把**非法的**那份原样交给 store，由面板的 `parseRecommendation` 判为不可渲染并保留上一次。
   *
   * 这个分工必须钉住：若将来有人把它改成「跳过非法、回退到上一条合法的」，面板反而会显示一条
   * **比实际更陈旧**的结果（用户看到的是两次之前的推荐）。
   */
  it('T20：多条推荐工具结果 → 取消息序最后一条（即便它非法，也交给面板去判）', () => {
    const legalPayload = JSON.stringify('{"message":"合法的","products":[]}')

    const messages = [
      {
        id: 'm-t20-a',
        role: 'assistant',
        toolCalls: [{ id: 'tc-a', function: { name: 'recommend_products', arguments: '{}' } }],
      },
      { id: 'tr-a', role: 'tool', toolCallId: 'tc-a', content: JSON.stringify(legalPayload) },
      {
        id: 'm-t20-b',
        role: 'assistant',
        toolCalls: [{ id: 'tc-b', function: { name: 'recommend_products', arguments: '{}' } }],
      },
      // 后者非法（不是合法 JSON）
      { id: 'tr-b', role: 'tool', toolCallId: 'tc-b', content: JSON.stringify('这不是 JSON') },
    ] as Message[]

    expect(lastRecommendationContent(messages)).toBe('这不是 JSON')
  })
})
