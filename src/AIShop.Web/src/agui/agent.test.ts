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
 *
 * 测试走**真实 `@ag-ui/client`**（`HttpAgent`）+ `src/test/sse.ts` 的 SSE 替身 ——
 * SSE 解析与事件应用全部由官方 SDK 完成（R18-2 的证据：本文件不含任何手写 SSE 解析）。
 */
import type { Message } from '@ag-ui/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { withUsername } from '../api/http'
import { readMessages, writeUsername } from '../state/session'
import { installFetchStub, type FetchCall, type RouteSpec } from '../test/fetch-stub'
import {
  createSseResponse,
  runFinished,
  runStarted,
  type SseEvent,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResult,
  toolCallStart,
} from '../test/sse'
import { createAgent } from './agent'
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

/** 按调用顺序依次回放各轮的 SSE（用尽后重复最后一轮，避免用例漏配时静默变成别的错误）。 */
function scriptRounds(rounds: readonly (readonly SseEvent[])[]): RouteSpec {
  let index = 0
  return () => {
    const events = rounds[Math.min(index, rounds.length - 1)] ?? []
    index += 1
    return createSseResponse(events)
  }
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
