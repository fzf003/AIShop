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
import { afterEach, describe, expect, it, vi } from 'vitest'

import App from './App'
import { FIRST_BYTE_TIMEOUT_MS } from './agui/agent'
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
import { act, fireEvent, render, screen, userEvent, waitFor } from './test/render'
import {
  type SseEvent,
  createSseResponse,
  customEvent,
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

  /**
   * C2：**挂起**形态下的发送入口恢复（与上一条 R7-2 互补）。
   *
   * R7-2 覆盖的是「服务端正常收尾 → 入口恢复」；本用例覆盖 SDK 唯一不能自愈的那条出口 ——
   * 服务端**既不产出事件、也不结束、也不报错**（`openSseStream` 只 send 部分帧、不 close）。
   * 此时若没有静默兜底，`isRunning` 恒真、发送入口永久禁用，用户只能刷新页面。
   *
   * 反证导向：撤销 `agent.ts` 的 watchdog 后本用例会**挂死超时**（流程永不收尾）。
   */
  /**
   * C2：**挂起**形态下的发送入口恢复（与上一条 R7-2 互补）。
   *
   * R7-2 覆盖的是「服务端正常收尾 → 入口恢复」；本用例覆盖 SDK 唯一不能自愈的那条出口 ——
   * 服务端**连首字节都不发**（连接建立、但一个事件都不产出，也不结束、也不报错）。
   * 此时若没有兜底，`isRunning` 恒真、发送入口永久禁用，用户只能刷新页面。
   *
   * 注：不用「发若干帧后静默」构造 —— 真机实测那是正常行为（记忆提取期间服务端静默约 20 秒），
   * 详见 `agent.ts` 里 `FIRST_BYTE_TIMEOUT_MS` 的说明与 `agent.test.ts` 中不误杀用例。
   *
   * 反证导向：撤销 `agent.ts` 的首字节兜底后本用例会**挂死超时**（流程永不收尾）。
   */
  it('C2：服务端连首字节都不发 → 首字节超时中止本轮，发送入口恢复可用', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    render(<App />)
    // 进主界面在**真实计时器**下完成：`findBy*` / `waitFor` 在 vitest 的 fake timers 下不会
    // 自动推进定时器（那套自动检测只认 jest），提前切换会挂到超时。
    await enterMain(/Marla/, /MiMo/)

    // 这一段**只用同步的 `fireEvent`**：`userEvent` 内部走计时器，在 fake timers 下会卡住。
    // 只 fake 兜底用到的两个计时器，避免 React 调度依赖的 microtask / performance 被替换。
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      fireEvent.change(screen.getByLabelText('消息'), { target: { value: '你好' } })
      fireEvent.click(sendButton())

      // 点击后的「进入运行态」是异步的，先 flush 一轮再断言（否则会误判为未禁用）
      await act(async () => undefined)

      // 刻意一个事件都不发 —— 真正的挂起；本轮应处于运行中，入口禁用
      expect(sendButton().disabled).toBe(true)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(FIRST_BYTE_TIMEOUT_MS + 1_000)
      })

      expect(sendButton().disabled).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('等待态占位气泡（design §16）', () => {
  /**
   * 三条用例覆盖交互决定的全部时机：
   * 1. 点发送即出现（无延迟）；
   * 2. 首个文本增量到达 → 占位消失、由真实流式气泡接替；
   * 3. 轮次结束（`RUN_FINISHED`）→ 占位不残留（含「本轮无任何文本输出」的边界）。
   *
   * 占位是**纯 UI 占位、不是消息**：第 4 条另证它不进入 `messages`（不持久化、不进请求体）。
   */
  function placeholderOf(container: HTMLElement): Element | null {
    return container.querySelector('.row.a .bub.thinking')
  }

  it('点发送后立即出现「正在思考中…」占位，且位于对话区末尾（不进入消息序列）', async () => {
    // 响应体一直挂着、一个事件都不发：把「首字未到」的窗口稳定住，便于断言。
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)
    expect(placeholderOf(container)).toBeNull()

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())

    // 点发送即出现（不需要任何服务端事件）
    await waitFor(() => {
      expect(placeholderOf(container)?.textContent).toBe('正在思考中…')
    })

    // 位置 = 对话区末尾：`.msgs` 的**最后**一个子元素（那一行）里就是这条占位气泡
    const msgs = container.querySelector('.msgs')
    expect(msgs?.lastElementChild?.lastElementChild).toBe(placeholderOf(container))

    // 纯 UI 占位、不是消息：用户消息之后没有新增消息，history 仍只有那一条用户消息。
    await waitFor(() => {
      expect(sendButton().disabled).toBe(true)
    })
    const stored = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla')) ?? '[]',
    ) as Message[]
    expect(stored.map((m) => m.role)).toEqual(['user'])
    expect(JSON.stringify(stored)).not.toContain('正在思考中')

    // 收尾：关闭挂起的流（本轮没有真实回复内容，仅用于结束运行态）。
    await act(async () => {
      stream.close()
    })
  })

  it('首个文本增量到达 → 占位消失、由真实内容接替', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)

    await userEvent.type(screen.getByLabelText('消息'), '你好')
    await userEvent.click(sendButton())
    await waitFor(() => {
      expect(placeholderOf(container)).not.toBeNull()
    })

    // 只发「文本消息开始」（正文仍为空）→ 仍算未开字，占位不动
    await act(async () => {
      stream.send([runStarted('thread-1', 'run-1'), textMessageStart('assistant-1')])
    })
    await waitFor(() => {
      expect(sendButton().disabled).toBe(true)
    })
    expect(placeholderOf(container)).not.toBeNull()

    // 首个文本增量到达 → 占位消失、真实气泡接替（一个真气泡，且不是占位类）
    await act(async () => {
      stream.send([textMessageContent('assistant-1', '你好呀')])
    })
    await waitFor(() => {
      expect(placeholderOf(container)).toBeNull()
    })
    expect(container.querySelector('.row.a .bub')?.textContent).toBe('你好呀')
    expect(container.querySelectorAll('.row.a .bub:not(.thinking)')).toHaveLength(1)

    await act(async () => {
      stream.send([textMessageEnd('assistant-1'), runFinished('thread-1', 'run-1')])
      stream.close()
    })
  })

  it('本轮无任何文本输出（只推 CUSTOM、只调工具）→ 收尾时占位不残留', async () => {
    const stream = openSseStream()
    stubFetch({ '/models': MODELS, '/agui': () => stream.response })

    const { container } = render(<App />)
    await enterMain(/Marla/, /MiMo/)

    await userEvent.type(screen.getByLabelText('消息'), '推荐点东西')
    await userEvent.click(sendButton())

    // 全程只推 CUSTOM 事件：没有任何文本 → 占位应一直显示（判据「还没开字」成立）
    await act(async () => {
      stream.send([
        runStarted('thread-1', 'run-1'),
        customEvent('recommendation', { message: '为你精选', products: [] }),
      ])
    })
    await waitFor(() => {
      expect(placeholderOf(container)).not.toBeNull()
    })

    // `RUN_FINISHED` 到达 → `isRunning` 变假 → 占位无条件消失，不残留
    await act(async () => {
      stream.send([runFinished('thread-1', 'run-1')])
      stream.close()
    })

    await waitFor(() => {
      expect(sendButton().disabled).toBe(false)
    })
    expect(placeholderOf(container)).toBeNull()
    expect(container.querySelector('.row.a')).toBeNull()

    // 该轮同样没有把占位写进历史
    const stored = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla')) ?? '[]',
    ) as Message[]
    expect(stored.map((m) => m.role)).toEqual(['user'])
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

    // 本轮尚未收到任何助手内容，欢迎语必须已经消失。
    // 注意：此时对话区末尾会有一条**等待态占位气泡**（`.row.a .bub.thinking`，本变更新增），
    // 它不是助手内容 —— 故这里按「排除占位」的选择器断言「还没有真实的助手气泡」。
    expect(container.querySelector('.row.a .bub:not(.thinking)')).toBeNull()
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
