/**
 * 主界面装配用例（tasks.md C11 的集成级验收 —— 复用 C1 的前端测试基建驱动**真实** `@ag-ui/client`）。
 *
 * 覆盖 spec `agui-client`：
 * - R7-1 流式增量渲染（首个增量到达即出气泡，而非流末一次性出现）；
 * - R7-2 运行中发送入口禁用、本轮结束恢复；
 * - R17-1 空历史账户进入 → 消息区顶部欢迎语胶囊；
 * - R17-2 首条消息发出瞬间欢迎语消失；
 * - R17-3 / R17-5 欢迎语可见性由「当前账户消息列表是否为空」决定（刷新恢复 / 切换账户各自判定）；
 * - R17-4 欢迎语不进入消息序列与持久化；
 * - R12-2 主界面无「新建对话 / 会话列表」类入口；
 * - R2 第 3 段 + R5 退出登录只清当前账户、回账户选择页。
 *
 * 本文件也是「装配接线」的回归：会话生命周期、Toast 出口、会话失效订阅、错误分派四处接线
 * 任一失效都会在这里（或下游用例）变红。
 */
import type { Message } from '@ag-ui/client'
import { afterEach, describe, expect, it } from 'vitest'

import App from './App'
import { endSession } from './agui/store'
import { dismissToast } from './components/Toast'
import { resetCart } from './state/cart'
import { STORAGE_KEYS, writeMessages, writeModelId, writeUsername } from './state/session'
import {
  installFetchStub,
  type FetchStub,
  type RouteHandler,
  type RouteSpec,
} from './test/fetch-stub'
import { act, render, screen, userEvent, waitFor } from './test/render'
import {
  type SseEvent,
  createSseResponse,
  encodeSseBody,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
} from './test/sse'

/** `/models` 替身（顺序即响应顺序；`isDefault` 在 `gpt-4.1` 上）。 */
const MODELS = [
  { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-0813', isDefault: false },
  { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
  { id: 'qwen', name: 'Qwen', model: 'qwen3.8-flash', isDefault: false },
]

let stub: FetchStub | null = null

function stubFetch(routes: Record<string, RouteSpec>): FetchStub {
  stub = installFetchStub(routes)
  return stub
}

/** 一轮完整（含最终文本）的 SSE 响应**工厂**：`Response` 的 body 只能读一次，故每次 fetch 新建。 */
function completeRound(text: string): RouteHandler {
  return () =>
    createSseResponse([
      runStarted('thread-1', 'run-1'),
      textMessageStart('assistant-1'),
      textMessageContent('assistant-1', text),
      textMessageEnd('assistant-1'),
      runFinished('thread-1', 'run-1'),
    ])
}

/**
 * 可手动放行增量与收尾的 SSE 流（handoff-C10 经验 3 的写法）。
 *
 * 用非空初始值的函数变量接住 `controller` 的回调 —— 可空类型会被 TS 的 CFA 收窄成 `never`。
 */
function openSseStream(): {
  response: Response
  send: (events: readonly SseEvent[]) => void
  close: () => void
} {
  const encoder = new TextEncoder()
  let push: (events: readonly SseEvent[]) => void = () => undefined
  let finish: () => void = () => undefined

  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      push = (events) => {
        controller.enqueue(encoder.encode(encodeSseBody(events)))
      }
      finish = () => {
        controller.close()
      }
    },
  })

  return {
    response: new Response(body, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    }),
    send: (events) => {
      push(events)
    },
    close: () => {
      finish()
    },
  }
}

/** 账户屏 → 模型屏 → 主界面（点账户卡 + 点模型卡）。 */
async function enterMain(account: RegExp, model: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('button', { name: account }))
  await userEvent.click(await screen.findByRole('button', { name: model }))
}

function sendButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: '发送' }) as HTMLButtonElement
}

function welcomeOf(container: HTMLElement): Element | null {
  return container.querySelector('.welcome')
}

afterEach(() => {
  stub?.restore()
  stub = null
  globalThis.localStorage.clear()
  endSession()
  resetCart()
  dismissToast()
})

describe('流式渲染与单轮运行约束（R7）', () => {
  it('SSE 分多个文本增量：首个增量到达时气泡已出现并随后续增量增长（R7-1）', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())

    // 首个增量 → 气泡已经出现（不是等流结束）
    await act(async () => {
      stream.send([
        runStarted('thread-1', 'run-1'),
        textMessageStart('assistant-1'),
        textMessageContent('assistant-1', '第一段'),
      ])
    })
    await waitFor(() => {
      expect(container.querySelector('.row.a .bub')?.textContent).toBe('第一段')
    })

    // 后续增量 → 同一气泡继续增长
    await act(async () => {
      stream.send([textMessageContent('assistant-1', '第二段')])
    })
    await waitFor(() => {
      expect(container.querySelector('.row.a .bub')?.textContent).toBe('第一段第二段')
    })

    await act(async () => {
      stream.send([textMessageEnd('assistant-1'), runFinished('thread-1', 'run-1')])
      stream.close()
    })
  })

  it('运行中发送入口禁用，本轮结束后恢复（R7-2）', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    render(<App />)
    await enterMain(/Marla/, /MiMo/)
    expect(sendButton().disabled).toBe(false)

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())

    await waitFor(() => {
      expect(sendButton().disabled).toBe(true)
    })

    await act(async () => {
      stream.send([
        runStarted('thread-1', 'run-1'),
        textMessageStart('assistant-1'),
        textMessageContent('assistant-1', '好的'),
        textMessageEnd('assistant-1'),
        runFinished('thread-1', 'run-1'),
      ])
      stream.close()
    })

    await waitFor(() => {
      expect(sendButton().disabled).toBe(false)
    })
  })
})

describe('空对话欢迎语（R17）', () => {
  it('空历史账户进入主界面 → 消息区顶部显示欢迎语胶囊（R17-1）', async () => {
    stubFetch({ '/models': MODELS, '/agui': completeRound('好的') })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)

    const msgs = container.querySelector('.msgs')
    const welcome = welcomeOf(container)
    expect(welcome?.textContent).toBe('👋 开始聊天，告诉我您的喜好')
    expect(msgs?.firstElementChild).toBe(welcome)
  })

  it('助手未回复时发出首条消息 → 欢迎语立即消失（R17-2）', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)
    expect(welcomeOf(container)).not.toBeNull()

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())

    // 本轮尚未收到任何助手内容（`.row.a` 还不存在），欢迎语必须已经消失
    expect(container.querySelector('.row.a')).toBeNull()
    expect(welcomeOf(container)).toBeNull()

    await act(async () => {
      stream.close()
    })
  })

  it('持久化有历史 → 不显示；无历史账户 → 显示；切到有历史账户 → 又不显示（R17-3 / R17-5）', async () => {
    stubFetch({ '/models': MODELS, '/agui': completeRound('好的') })

    const history: Message[] = [
      { id: 'u-1', role: 'user', content: '老问题' },
      { id: 'a-1', role: 'assistant', content: '老回答' },
    ]
    writeMessages('marla', history)
    writeUsername('marla')
    writeModelId('gpt-4.1')

    const { container } = render(<App />)
    // 刷新恢复：直接落在主界面并恢复历史（R17-3 的「有历史」一侧）
    await screen.findByText('老问题')
    expect(welcomeOf(container)).toBeNull()

    // marla 退出 → steve 无历史 → 显示（R17-5 前半）
    await userEvent.click(screen.getByRole('button', { name: '退出' }))
    await enterMain(/Steve/, /MiMo/)
    expect(welcomeOf(container)).not.toBeNull()

    // 离开 steve 后切到「有历史」的 marla → 不显示（R17-5 后半的规则）。
    //
    // ⚠️ 这里重新写入 marla 的历史，而不是依赖第一次登录时留下的那份：spec R12 第 3 段明确
    // 「退出登录清空的是**本地**会话与历史」，因此 R17-5 字面的「从 marla 退出 → 再切回 marla
    // 列表非空」与 R2 第 3 段 / R12 第 3 段互相矛盾（详见 handoff-C11 的 ⚠️ 记录）。
    // 本用例断言的是该场景真正要保护的规则：**可见性由「该账户自己的消息列表」派生**。
    writeMessages('marla', history)
    await userEvent.click(screen.getByRole('button', { name: '退出' }))
    await enterMain(/Marla/, /MiMo/)
    await screen.findByText('老问题')
    expect(welcomeOf(container)).toBeNull()
  })

  it('首条消息的请求体与持久化都不含欢迎语，messages 首项是该用户消息（R17-4）', async () => {
    const fetchStub = stubFetch({ '/models': MODELS, '/agui': completeRound('好的') })

    render(<App />)
    await enterMain(/Marla/, /MiMo/)

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())

    await waitFor(() => {
      expect(fetchStub.callsTo('/agui')).toHaveLength(1)
    })

    const [call] = fetchStub.callsTo('/agui')
    const body = call?.body as { messages: Message[] }
    expect(body.messages).toHaveLength(1)
    expect(body.messages[0]?.role).toBe('user')
    expect(body.messages[0]?.content).toBe('你好')
    expect(JSON.stringify(body)).not.toContain('开始聊天')

    const stored = globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla'))
    expect(stored).not.toBeNull()
    expect(stored ?? '').not.toContain('开始聊天')
    const storedMessages = JSON.parse(stored ?? '[]') as Message[]
    expect(storedMessages[0]?.role).toBe('user')
    expect(storedMessages[0]?.content).toBe('你好')
  })
})

describe('主界面入口与退出登录（R12 / R2 第 3 段 / R5）', () => {
  it('主界面不存在「新建对话 / 会话列表」类入口（R12-2）', async () => {
    stubFetch({ '/models': MODELS, '/agui': completeRound('好的') })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)

    expect(screen.queryByText(/新建对话|会话列表/)).toBeNull()
    const labels = [...container.querySelectorAll('button')].map((button) => button.textContent ?? '')
    expect(labels.some((label) => /新建对话|新对话|会话列表/.test(label))).toBe(false)
  })

  it('点退出 → 只删当前账户的持久化键、其它账户保留、回到账户选择页（R2 第 3 段 + R5）', async () => {
    stubFetch({ '/models': MODELS, '/agui': completeRound('好的') })

    writeMessages('marla', [{ id: 'u-1', role: 'user', content: '老问题' }])
    writeMessages('steve', [{ id: 'u-2', role: 'user', content: '别人的问题' }])
    writeUsername('marla')
    writeModelId('gpt-4.1')

    const { container } = render(<App />)
    await screen.findByText('老问题')

    await userEvent.click(screen.getByRole('button', { name: '退出' }))

    // 当前账户（marla）的会话数据被清：消息与 threadId 双双消失
    expect(globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla'))).toBeNull()
    expect(globalThis.localStorage.getItem(STORAGE_KEYS.threadId('marla'))).toBeNull()
    // 其它账户原样保留
    expect(globalThis.localStorage.getItem(STORAGE_KEYS.messages('steve'))).not.toBeNull()

    expect(container.querySelector('[data-screen]')?.getAttribute('data-screen')).toBe('account')
    expect(screen.getByText('选择一个账户开始')).toBeDefined()

    // 退出登录清空的确实是「本地会话与历史」（spec R12 第 3 段）：立刻重选 marla，
    // 列表已空 → 欢迎语出现。这条断言把 R2 第 3 段 / R12 第 3 段的语义钉住
    // （也是 R17-5 字面写法与二者矛盾之处，见 handoff-C11 的 ⚠️ 记录）。
    await enterMain(/Marla/, /MiMo/)
    expect(welcomeOf(container)).not.toBeNull()
    expect(container.querySelector('.row.u')).toBeNull()
  })
})
