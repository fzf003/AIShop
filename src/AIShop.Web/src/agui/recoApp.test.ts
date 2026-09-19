/**
 * 推荐面板的数据源接线（agui-reco-realtime tasks.md F4「验收 / 测试」；spec R7 / R9）。
 *
 * 本文件是 App 级端到端的集中落点：用**真机形态**的 SSE（经真实 `@ag-ui/client`）驱动完整装配
 * （账户屏 → 模型屏 → 主界面 → 一轮），断言面板的数据来自推荐内容 store，且三个来源的
 * 到达顺序 = 覆盖顺序（后到者胜）。
 *
 * ⚠️ **本文件是 `.ts`（非 `.tsx`）**：vite/oxc 按扩展名判定，写 JSX 会 `[PARSE_ERROR]`。
 * 渲染组件一律用 `createElement(App)`（`tools.test.ts` 有同款先例）。
 *
 * ⚠️ **F5 / F6 会往本文件追加用例**：用例之间不得共享可变状态，`afterEach` 里统一复位
 * 模块级单例（`endSession` / `resetCart` / `dismissToast` / `resetRecoContent`）与 `localStorage`。
 */
import type { Message } from '@ag-ui/client'
import { createElement } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import App from '../App'
import { dismissToast } from '../components/Toast'
import { resetCart } from '../state/cart'
import { STORAGE_KEYS, writeModelId, writeUsername } from '../state/session'
import { installFetchStub } from '../test/fetch-stub'
import { render, screen, userEvent, waitFor } from '../test/render'
import {
  createSseResponse,
  customEvent,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallArgs,
  toolCallEnd,
  toolCallResultEncoded,
  toolCallStart,
  type SseEvent,
} from '../test/sse'
import { getRecoSnapshot, resetRecoContent } from './reco'
import { endSession } from './store'

const MODELS = [
  { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-0813', isDefault: false },
  { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
]

/** 工具结果那一路的负载（`recommend_products` 的单行 JSON，camelCase）。 */
const TOOL_RECO = {
  message: '根据您的对话，为您推荐：',
  hasRecommendation: true,
  categories: ['鞋类'],
  products: [
    {
      id: 1,
      name: '工具来源商品',
      category: '鞋类',
      price: 129.99,
      emoji: '👟',
      reason: '因为你提到「跑步」',
    },
  ],
}

/** `CUSTOM` 事件那一路的负载（`value` 是**对象**，与持久化 `agui.reco.{username}` 同形）。 */
const PUSH_RECO = {
  message: '根据您的偏好，为您推荐：',
  hasRecommendation: true,
  categories: ['配饰'],
  products: [
    {
      id: 3,
      name: '推送来源商品',
      category: '配饰',
      price: 249.99,
      emoji: '🎧',
      reason: '根据你的偏好「耳机」',
    },
  ],
}

let restoreFetch: (() => void) | null = null

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
  // 模块级单例复位：F5 / F6 追加的用例也依赖这三件套（各自 describe 内另有同名 afterEach）。
  globalThis.localStorage.clear()
  endSession()
  resetCart()
  dismissToast()
  resetRecoContent()
})

describe('推荐面板数据源 = store（F4：App 装配）', () => {
  /**
   * 一轮「先 `recommend_products` 工具结果、后 `CUSTOM`」的 SSE（真机形态）。
   *
   * 顺序即真实时序：工具结果随 `TOOL_CALL_RESULT` 先到（`agent.messages` 随之变化），
   * 服务端的推荐推送（`RecommendationPushAgent` 在内层流结束后合成）随后以 `CUSTOM` 到达。
   */
  function twoSourceRound(threadId = 'thread-1'): SseEvent[] {
    return [
      runStarted(threadId, 'run-1'),
      textMessageStart('a1'),
      toolCallStart('tc1', 'recommend_products', 'a1'),
      toolCallArgs('tc1', '{"query":"跑步"}'),
      toolCallEnd('tc1'),
      toolCallResultEncoded('tc1', 'tr1', JSON.stringify(TOOL_RECO)),
      textMessageContent('a1', '为您推荐'),
      textMessageEnd('a1'),
      customEvent('recommendation', PUSH_RECO),
      runFinished(threadId, 'run-1'),
    ]
  }

  /** 走完「账户屏 → 模型屏 → 主界面」（首次进入路径）。 */
  async function pickAccountAndModel(): Promise<HTMLElement> {
    const { container } = render(createElement(App))
    await userEvent.click(screen.getByRole('button', { name: /Marla/ }))
    await userEvent.click(await screen.findByRole('button', { name: /MiMo/ }))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    return container
  }

  it('同轮先工具结果、后 CUSTOM → 面板渲染 CUSTOM 的内容（后到者胜），且消息与 store 都已落定', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(twoSourceRound()),
    })
    restoreFetch = stub.restore

    const container = await pickAccountAndModel()
    // 收到任何来源之前：占位（面板确实是由 store 驱动的，而不是恒有内容）
    expect(container.querySelector('.rhint')).not.toBeNull()

    await userEvent.type(screen.getByLabelText('消息'), '推荐一下跑鞋')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))

    await waitFor(() => {
      expect(container.querySelectorAll('.rcard')).toHaveLength(1)
    })
    // 后到者（`CUSTOM`）胜：面板不是工具结果那一路的内容
    expect(
      [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent),
    ).toEqual(['推送来源商品'])
    expect(container.querySelector('.rhint')).toBeNull()

    // 同轮两条通道承载的是同一个事实（最近一次推荐），store 的最终值 = `CUSTOM` 的负载
    // ——这正是 F5 将要回写进 `agui.reco.{username}` 的那个值。
    // （`agui.reco.marla` 键本身的写入/清除属 F5，本工单不接线；此处钉住值的来源与形态。）
    expect(getRecoSnapshot()).toBe(JSON.stringify(PUSH_RECO))

    // F5 补齐（handoff-F4 遗留 1）：该键现在确实被回写，且内容与 store 快照逐字一致 ——
    // 「写入与 `agui.messages.{username}` 同一处、同一时刻」（spec R8 第 2 段）在真实一轮上的直接证据。
    expect(globalThis.localStorage.getItem(STORAGE_KEYS.reco('marla'))).toBe(getRecoSnapshot())

    // 另一半：消息历史照常落库（推荐负载**不进**历史，`CUSTOM` 不产生消息 —— spec R6-1）
    const stored = JSON.parse(
      globalThis.localStorage.getItem(STORAGE_KEYS.messages('marla')) ?? 'null',
    ) as Message[] | null
    expect(stored).not.toBeNull()
    expect(stored?.some((message) => message.role === 'tool' && message.toolCallId === 'tc1')).toBe(
      true,
    )
    expect(JSON.stringify(stored)).not.toContain('推送来源商品')
  })
})

describe('切账户 / 退出后的推荐 store 归零（F4）', () => {
  const RECO_MARLA = {
    message: 'Marla 的推荐',
    hasRecommendation: true,
    categories: ['鞋类'],
    products: [
      { id: 1, name: 'Marla推荐商品', category: '鞋类', price: 10, emoji: '👟', reason: 'marla' },
    ],
  }
  const RECO_STEVE = {
    message: 'Steve 的推荐',
    hasRecommendation: true,
    categories: ['配饰'],
    products: [
      { id: 3, name: 'Steve推荐商品', category: '配饰', price: 20, emoji: '🎧', reason: 'steve' },
    ],
  }

  /** 预置「上次会话留下的」推荐负载 + 身份（App 直接落到主界面）。 */
  function seedAccount(username: string, reco: unknown): void {
    writeUsername(username)
    writeModelId('gpt-4.1')
    globalThis.localStorage.setItem(STORAGE_KEYS.reco(username), JSON.stringify(reco))
  }

  it('退出后 store 归零；再进入另一账户时以该账户的恢复值为初值（不残留上一账户的推荐）', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse([runStarted('t', 'r'), runFinished('t', 'r')]),
    })
    restoreFetch = stub.restore

    seedAccount('marla', RECO_MARLA)
    globalThis.localStorage.setItem(STORAGE_KEYS.reco('steve'), JSON.stringify(RECO_STEVE))

    const { container } = render(createElement(App))
    // 先证明恢复路径真的生效：marla 的持久化负载原样进了 store 与面板
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    expect(getRecoSnapshot()).toBe(JSON.stringify(RECO_MARLA))
    await waitFor(() => {
      expect(
        [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent),
      ).toEqual(['Marla推荐商品'])
    })

    // 退出登录（closeToAccount）：与 `resetCart()` 同款时机丢弃推荐投影
    await userEvent.click(screen.getByRole('button', { name: '退出' }))
    expect(container.querySelector('.chat')).toBeNull()
    expect(getRecoSnapshot()).toBeNull()

    // 换账户进入：以**新账户**的持久化值为初值，上一账户的推荐不得残留
    await userEvent.click(screen.getByRole('button', { name: /Steve/ }))
    await userEvent.click(await screen.findByRole('button', { name: /MiMo/ }))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())

    await waitFor(() => {
      expect(
        [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent),
      ).toEqual(['Steve推荐商品'])
    })
    expect(getRecoSnapshot()).toBe(JSON.stringify(RECO_STEVE))
    expect(container.textContent).not.toContain('Marla推荐商品')
  })
})

/**
 * F5：推荐负载的**持久化回写**（`agui.reco.{username}`）与刷新恢复（spec R8 / tasks.md F5）。
 *
 * 回写落点 = App 会话 effect 的既有 `subscribe(...)` 通知链，与 `writeToolRounds` 处于**同一次通知、
 * 同一批次** —— store 的 `handleSessionChange` 正在同一次通知里写 `agui.messages.{username}`。
 *
 * ⚠️ 「写入次数相等」的用例**预置了该账户的推荐负载**：store 快照为 `null`（该账户从未收到过推荐）时
 * 本实现**不建键** —— 写空串会让刷新时的 `readReco` 把「从未写过」误报成「数据损坏」告警。
 * 预置使快照自挂载起即非空，此后每一次 `agui.messages.marla` 写入都必须与一次 `agui.reco.marla`
 * 写入成对，这正是「同一处、同一时刻」可断言的事实形态（与 C16 对 `agui.tools` 的判据同源）。
 */
describe('推荐负载持久化回写与刷新恢复（F5）', () => {
  /** 2 条推荐：既有「内容与顺序一致」的可断言面，也能证明确实不是占位。 */
  const RECO_TWO = {
    message: '根据您的偏好，为您推荐：',
    hasRecommendation: true,
    categories: ['鞋类', '配饰'],
    products: [
      { id: 1, name: '恢复商品甲', category: '鞋类', price: 101, emoji: '👟', reason: '理由甲' },
      { id: 3, name: '恢复商品乙', category: '配饰', price: 202, emoji: '🎧', reason: '理由乙' },
    ],
  }

  /** 预置用：与 `RECO_TWO` 不同的一条，使「回写反映的是最新值」可观测。 */
  const RECO_SEED = {
    message: '上一次会话留下的推荐',
    hasRecommendation: true,
    categories: ['旧类'],
    products: [
      { id: 2, name: '旧会话商品', category: '旧类', price: 9, emoji: '🧦', reason: '旧' },
    ],
  }

  const recoKey = (): string => STORAGE_KEYS.reco('marla')

  /** 一轮「仅 `CUSTOM`、无工具」的对话：面板与持久化值都应 = `RECO_TWO`。 */
  function pushTwoRound(threadId: string): SseEvent[] {
    return [
      runStarted(threadId, 'run-1'),
      textMessageStart('a1'),
      textMessageContent('a1', '为您推荐'),
      textMessageEnd('a1'),
      customEvent('recommendation', RECO_TWO),
      runFinished(threadId, 'run-1'),
    ]
  }

  /** 预置身份 → App 启动即落主界面（`agui.reco` 的预置由各用例按需自行决定）。 */
  function seedIdentity(): void {
    writeUsername('marla')
    writeModelId('gpt-4.1')
  }

  async function enterMain(): Promise<HTMLElement> {
    const { container } = render(createElement(App))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    return container
  }

  function cardNames(container: HTMLElement): (string | null)[] {
    return [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent)
  }

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('同处同时刻：同一次通知里 agui.messages.marla 与 agui.reco.marla 的写入次数相等且 > 0', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(pushTwoRound('t-f5-write')),
    })
    restoreFetch = stub.restore

    seedIdentity()
    globalThis.localStorage.setItem(recoKey(), JSON.stringify(RECO_SEED))

    // 必须在预置之后安装：本用例要数的是「挂载后由通知链产生的写入」。
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem')

    const container = await enterMain()
    await userEvent.type(screen.getByLabelText('消息'), '推荐一下跑鞋')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))
    await waitFor(() => expect(cardNames(container)).toEqual(['恢复商品甲', '恢复商品乙']))

    const writtenKeys = setItemSpy.mock.calls.map((call) => String(call[0]))
    const messageWrites = writtenKeys.filter((key) => key === STORAGE_KEYS.messages('marla')).length
    const recoWrites = writtenKeys.filter((key) => key === recoKey()).length

    // 反向锚点：该轮确实发生了消息写入（否则「相等」可能只是两个 0）。
    expect(messageWrites).toBeGreaterThan(0)
    expect(recoWrites).toBe(messageWrites)

    // 回写的是**面板所见的那一份**（不是某个中间量），且确实换成了本轮的新值。
    const recoValues = setItemSpy.mock.calls
      .filter((call) => String(call[0]) === recoKey())
      .map((call) => String(call[1]))
    expect(recoValues.at(-1)).toBe(getRecoSnapshot())
    expect(getRecoSnapshot()).toBe(JSON.stringify(RECO_TWO))
    expect(globalThis.localStorage.getItem(recoKey())).toBe(JSON.stringify(RECO_TWO))
  })

  it('刷新恢复：重新挂载后 .rcard 恰 2 条且内容与顺序一致（非占位）', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(pushTwoRound('t-f5-refresh')),
    })
    restoreFetch = stub.restore

    seedIdentity()

    // 第一段：经真实一轮（`CUSTOM`）把面板填成 2 条，并由本工单的回写把该值落进 `agui.reco.marla`。
    const first = render(createElement(App))
    await waitFor(() => expect(first.container.querySelector('.chat')).not.toBeNull())
    await userEvent.type(screen.getByLabelText('消息'), '推荐一下跑鞋')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))
    await waitFor(() => expect(cardNames(first.container)).toEqual(['恢复商品甲', '恢复商品乙']))
    expect(globalThis.localStorage.getItem(recoKey())).toBe(JSON.stringify(RECO_TWO))

    // 第二段：卸载 + 重挂 = 刷新（会话 store 是模块级单例，两次之间必须 `endSession`）。
    first.unmount()
    endSession()

    const refreshed = await enterMain()
    await waitFor(() => expect(cardNames(refreshed)).toEqual(['恢复商品甲', '恢复商品乙']))
    expect(refreshed.querySelectorAll('.rcard')).toHaveLength(2)
    expect(refreshed.querySelector('.rhint')).toBeNull()
    // 「原样」= 连 reason 与价格文本都逐字相同（不是重新算出来的另一份内容）。
    expect(refreshed.textContent).toContain('理由甲')
    expect(refreshed.textContent).toContain('理由乙')
    expect(globalThis.localStorage.getItem(recoKey())).toBe(JSON.stringify(RECO_TWO))
  })

  it('从未收到过推荐时刷新：面板显示占位，且不伪造内容（不建键）', async () => {
    const stub = installFetchStub({ '/models': MODELS })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
    expect(container.querySelector('.rhint')).not.toBeNull()
    // MUST NOT 伪造内容：既然从未有过推荐，「该键」就不该存在（而不是被写成一个空壳值）。
    expect(globalThis.localStorage.getItem(recoKey())).toBeNull()
  })

  it('持久化数据损坏时安全降级：照常渲染 + 面板占位 + 告警含键名，不抛不白屏', async () => {
    const stub = installFetchStub({ '/models': MODELS })
    restoreFetch = stub.restore
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined)

    seedIdentity()
    globalThis.localStorage.setItem(recoKey(), '{这不是 JSON')

    const container = await enterMain()

    // 应用照常渲染（主界面 + 欢迎气泡都在），没有抛错、没有白屏。
    expect(container.querySelector('.welcome')).not.toBeNull()
    expect(screen.getByLabelText('消息')).not.toBeNull()
    // 面板走占位（无历史工具结果可回退），不显示伪造的推荐。
    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
    expect(container.querySelector('.rhint')).not.toBeNull()

    const warnings = warnSpy.mock.calls.map((call) => String(call[0]))
    expect(warnings.some((message) => message.includes(recoKey()))).toBe(true)
    // 坏值没有被「顺手回写」成合法值（读侧只降级，不修复）。
    expect(globalThis.localStorage.getItem(recoKey())).toBe('{这不是 JSON')
  })

  it('退出未重建：clearSession 之后 agui.reco.marla 仍为 null（通知链不回写重建）', async () => {
    const stub = installFetchStub({ '/models': MODELS })
    restoreFetch = stub.restore

    seedIdentity()
    globalThis.localStorage.setItem(recoKey(), JSON.stringify(RECO_TWO))

    const container = await enterMain()
    // 前提：键本来就在（否则「退出后为 null」是空转断言）。
    expect(globalThis.localStorage.getItem(recoKey())).toBe(JSON.stringify(RECO_TWO))

    await userEvent.click(screen.getByRole('button', { name: '退出' }))
    expect(container.querySelector('.chat')).toBeNull()

    // `closeToAccount` 的 `endSession()` 会再发一次通知 —— 守卫必须挡住那次回写（否则刚被
    // `clearSession` 删掉的键会被重建，正是 C16 记录的同款事故）。
    expect(globalThis.localStorage.getItem(recoKey())).toBeNull()
    expect(getRecoSnapshot()).toBeNull()
  })
})
