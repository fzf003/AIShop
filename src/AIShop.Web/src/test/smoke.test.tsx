/**
 * C1 冒烟用例 —— 同时是前端测试基建的验收用例。
 *
 * 覆盖（对应 tasks.md C1「验收 / 测试」）：
 * 1. React 骨架构渲染 + 屏幕切屏（jsdom + Testing Library 链路可用）；
 * 2. `sse.ts` 的帧编码与 `fetch-stub.ts` 的路由 / 调用记录；
 * 3. **真实 `@ag-ui/client`** 经 SSE 替身跑完一轮（spec R18「AG-UI 经官方客户端接入」的
 *    正向证据：事件应用逻辑由官方 SDK 承担，测试基建能驱动它）。
 */
import { HttpAgent } from '@ag-ui/client'
import type { AssistantMessage, Message, ToolMessage } from '@ag-ui/client'
import { describe, expect, it } from 'vitest'

import App from '../App'
import { installFetchStub, jsonResponse } from './fetch-stub'
import { render, screen, userEvent } from './render'
import {
  createSseResponse,
  encodeSse,
  encodeSseBody,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResult,
  toolCallStart,
} from './sse'

describe('骨架渲染', () => {
  it('默认停在账户选择屏，选账户后进入模型选择屏', async () => {
    // C6 起 App 是真实的「账户 → 模型 → 主界面」切屏：选账户后模型屏会拉 `GET /models`，故此处给替身
    const stub = installFetchStub({
      '/models': [{ id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true }],
    })

    try {
      const { container } = render(<App />)

      expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('AIShop 购物助手')
      expect(container.querySelector('.screen')?.getAttribute('data-screen')).toBe('account')
      expect(screen.getByText('选择一个账户开始')).toBeDefined()

      await userEvent.click(screen.getByRole('button', { name: /Marla/ }))

      expect(container.querySelector('.screen')?.getAttribute('data-screen')).toBe('model')
      expect(screen.getByText('选择模型')).toBeDefined()
    } finally {
      stub.restore()
    }
  })
})

describe('测试基建：SSE 编码', () => {
  it('按 @ag-ui/encoder 的格式编帧（data: <json> + 空行）', () => {
    expect(encodeSse({ type: 'TEXT_MESSAGE_CONTENT', messageId: 'm1', delta: '嗨' })).toBe(
      'data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"嗨"}\n\n',
    )
  })

  it('多事件拼体后帧数正确，且 Response 的 Content-Type 为 text/event-stream', async () => {
    const body = encodeSseBody([runStarted('t1', 'r1'), runFinished('t1', 'r1')])
    expect(body.split('\n\n').filter((frame) => frame.length > 0)).toHaveLength(2)

    const response = createSseResponse([runStarted('t1', 'r1')])
    expect(response.headers.get('Content-Type')).toBe('text/event-stream')
    expect(await response.text()).toBe(encodeSse({ type: 'RUN_STARTED', threadId: 't1', runId: 'r1' }))
  })
})

describe('测试基建：fetch 替身', () => {
  it('按路径路由、记录调用，且支持前缀匹配与未匹配时显式失败', async () => {
    const models = [{ id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true }]
    const stub = installFetchStub({
      '/models': models,
      // 前缀路由：`/cart` 覆盖 `/cart/items/{id}` 之类的子路径
      '/cart': ({ body }) => jsonResponse({ received: body }),
    })

    try {
      const modelsResponse = await fetch('/models')
      expect(await modelsResponse.json()).toEqual(models)

      const cartResponse = await fetch('/cart/items/abc?username=marla', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ productId: 3, quantity: 1 }),
      })

      expect(await cartResponse.json()).toEqual({ received: { productId: 3, quantity: 1 } })

      const [modelsCall] = stub.callsTo('/models')
      expect(modelsCall?.method).toBe('GET')
      expect(modelsCall?.body).toBeUndefined()

      const [cartCall] = stub.callsTo('/cart/items/abc')
      expect(cartCall?.method).toBe('POST')
      expect(cartCall?.search).toBe('?username=marla')
      expect(cartCall?.body).toEqual({ productId: 3, quantity: 1 })

      // 未注册的路径必须**显式失败**（防"忘注册路由"被当成正常 404 静默通过）
      await expect(fetch('/not-registered')).rejects.toThrow('无匹配路由')
    } finally {
      stub.restore()
    }
  })
})

describe('测试基建：驱动真实 @ag-ui/client', () => {
  it('一轮对话跑通：全量历史重发 + forwardedProps 携带 + 文本与工具消息落地', async () => {
    const threadId = 'thread-smoke'
    const history: Message[] = [
      { id: 'u-1', role: 'user', content: '你好' },
      { id: 'a-1', role: 'assistant', content: '你好，我是 AIShop 购物助手' },
    ]

    const events = [
      runStarted(threadId, 'run-smoke'),
      textMessageStart('assistant-2'),
      toolCallStart('call-1', 'recommend_products', 'assistant-2'),
      toolCallArgs('call-1', '{"query":"跑步鞋"}'),
      toolCallEnd('call-1'),
      toolCallResult('call-1', 'tool-msg-1', '{"message":"为你推荐"}'),
      textMessageContent('assistant-2', '为你找到一双跑鞋'),
      textMessageEnd('assistant-2'),
      runFinished(threadId, 'run-smoke', [{ model: 'mimo-v2.5', totalTokens: 42 }]),
    ]

    const stub = installFetchStub({ '/agui': () => createSseResponse(events) })

    try {
      const agent = new HttpAgent({ url: 'http://localhost/agui', threadId, initialMessages: history })
      let finishedUsage: unknown
      await agent.runAgent(
        { forwardedProps: { username: 'marla', model: 'gpt-4.1' } },
        {
          onRunFinishedEvent: ({ event }) => {
            finishedUsage = event.usage
          },
        },
      )

      // ① 每轮显式携带身份与模型（硬契约 3）
      const [request] = stub.callsTo('/agui')
      expect(request?.method).toBe('POST')
      const requestBody = request?.body as {
        threadId: string
        messages: Message[]
        forwardedProps: unknown
      }
      expect(requestBody.threadId).toBe(threadId)
      expect(requestBody.forwardedProps).toEqual({ username: 'marla', model: 'gpt-4.1' })

      // ② 客户端持有的**全量**历史被整体重发（硬契约 1；服务端 ProvideChatHistoryAsync 恒空）
      expect(requestBody.messages.map((message) => message.id)).toEqual(['u-1', 'a-1'])

      // ③ 事件流被官方 SDK 应用：assistant 文本 + 工具调用 + 配对的 tool 结果消息
      const assistant = agent.messages.find(
        (message): message is AssistantMessage =>
          message.role === 'assistant' && message.id === 'assistant-2',
      )
      expect(assistant?.content).toBe('为你找到一双跑鞋')
      expect(assistant?.toolCalls).toHaveLength(1)
      expect(assistant?.toolCalls?.[0]?.function.name).toBe('recommend_products')
      expect(assistant?.toolCalls?.[0]?.function.arguments).toBe('{"query":"跑步鞋"}')

      const toolMessage = agent.messages.find(
        (message): message is ToolMessage =>
          message.role === 'tool' && message.id === 'tool-msg-1',
      )
      expect(toolMessage?.content).toBe('{"message":"为你推荐"}')
      expect(toolMessage?.toolCallId).toBe('call-1')

      // ④ 工具结果消息与 toolCalls 成对（硬契约 1「只存纯文本会造孤儿消息」）
      const toolCallIds = agent.messages.flatMap((message) =>
        message.role === 'assistant' ? (message.toolCalls?.map((toolCall) => toolCall.id) ?? []) : [],
      )
      const toolResultIds = agent.messages
        .filter((message): message is ToolMessage => message.role === 'tool')
        .map((message) => message.toolCallId)
      expect(toolResultIds).toEqual(toolCallIds)

      // ⑤ 消息序列完整落进 agent.messages（持久化与下一轮全量重发的数据来源）
      expect(agent.messages.map((message) => message.id)).toEqual([
        'u-1',
        'a-1',
        'assistant-2',
        'tool-msg-1',
      ])

      // ⑥ 整轮 usage 经官方订阅钩子暴露（spec R8-3 的数据来源；无该字段时后续工单隐藏 token 项）
      expect(finishedUsage).toEqual([{ model: 'mimo-v2.5', totalTokens: 42 }])
    } finally {
      stub.restore()
    }
  })
})
