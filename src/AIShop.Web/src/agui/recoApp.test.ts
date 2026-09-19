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
import { act, render, screen, userEvent, waitFor } from '../test/render'
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
import { endSession, getAgent } from './store'

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

/**
 * F6：把 R2 / R5 / R6 / R7 / R9 里**断言「关系」而非单个行为**的条款集中钉死（tasks.md F6）。
 *
 * 三条写法约束（本仓硬要求）：
 * 1. **否定性断言必须配正向锚点或反证** —— 否则「什么都没发生」与「断言写错了」无法区分。
 *    每条「无请求 / 不轮询 / 不变 / 不回退」都先钉住对应通道**确实动过**：请求发生过（`GET /models`
 *    或 `POST /agui`）、工具条目出现过、面板此前有过内容、本轮工具结果确实落进了历史。
 *    各条反证（临时改产品代码 → 用例必红 → 还原）的实测记录见 handoff-F6；
 * 2. 只经 `installFetchStub` + **真实 `@ag-ui/client`** 驱动，不改写协议层行为；
 * 3. 本文件是 `.ts`：渲染一律 `createElement(App)`（JSX 会 `[PARSE_ERROR]`）。
 */
describe('端到端契约（F6）：无额外请求 / 不轮询 / 消息与工具栏不变 / 两源覆盖', () => {
  /** 更早一轮的 2 条推荐：既提供「数量 + 文案 + 顺序」三面，也能证明确实不是占位。 */
  const OLD_ROUND = {
    message: '旧一轮的推荐',
    hasRecommendation: true,
    categories: ['鞋类', '配饰'],
    products: [
      { id: 1, name: '旧轮商品甲', category: '鞋类', price: 101, emoji: '👟', reason: '旧甲' },
      { id: 3, name: '旧轮商品乙', category: '配饰', price: 202, emoji: '🎧', reason: '旧乙' },
    ],
  }

  /** 工具结果那一路（`recommend_products` 的返回文本形状）。 */
  const TOOL_ROUND = {
    message: '工具结果那一路',
    hasRecommendation: true,
    categories: ['鞋类'],
    products: [
      {
        id: 5,
        name: '工具来源商品',
        category: '鞋类',
        price: 129.99,
        emoji: '👟',
        reason: '工具理由',
      },
    ],
  }

  /** `CUSTOM` 推送那一路（后到者，应按到达顺序胜出）。 */
  const PUSH_ROUND = {
    message: '推送那一路',
    hasRecommendation: true,
    categories: ['配饰'],
    products: [
      {
        id: 7,
        name: '推送来源商品',
        category: '配饰',
        price: 249.99,
        emoji: '🎧',
        reason: '推送理由',
      },
    ],
  }

  /** 预置身份 → App 启动即落主界面（走恢复路径，不经账户屏 / 模型屏）。 */
  function seedIdentity(): void {
    writeUsername('marla')
    writeModelId('gpt-4.1')
  }

  async function enterMain(): Promise<HTMLElement> {
    const { container } = render(createElement(App))
    await waitFor(() => expect(container.querySelector('.chat')).not.toBeNull())
    return container
  }

  async function send(text: string): Promise<void> {
    await userEvent.type(screen.getByLabelText('消息'), text)
    await userEvent.click(screen.getByRole('button', { name: '发送' }))
  }

  function cardNames(container: HTMLElement): (string | null)[] {
    return [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent)
  }

  function cardTexts(container: HTMLElement): (string | null)[] {
    return [...container.querySelectorAll('.rcard')].map((element) => element.textContent)
  }

  /** 工具调用栏条目数（`ToolChip` 的根节点 `.tool`，与 `tools.test.ts` 同款选择器）。 */
  function toolCount(container: HTMLElement): number {
    return container.querySelectorAll('.tool').length
  }

  /** 持久化的消息历史（`agui.messages.{username}`）。 */
  function storedMessages(username = 'marla'): Message[] {
    const raw = globalThis.localStorage.getItem(STORAGE_KEYS.messages(username))
    expect(raw).not.toBeNull()
    return JSON.parse(raw ?? 'null') as Message[]
  }

  /** 一轮「文本回复（+ 可选 `CUSTOM`）」；`reco` 缺省时即「闲聊轮」。 */
  function chatRound(
    threadId: string,
    messageId: string,
    text: string,
    reco?: unknown,
  ): SseEvent[] {
    const events: SseEvent[] = [
      runStarted(threadId, `run-${messageId}`),
      textMessageStart(messageId),
      textMessageContent(messageId, text),
      textMessageEnd(messageId),
    ]
    if (reco !== undefined) events.push(customEvent('recommendation', reco))
    events.push(runFinished(threadId, `run-${messageId}`))
    return events
  }

  /**
   * 一轮**真机形态**的 `recommend_products` 调用：宿主多编码一层的 `TOOL_CALL_RESULT`，
   * 之后可选追加一个 `CUSTOM`（两条通道的到达顺序就是数组顺序）。
   */
  function toolRound(
    threadId: string,
    callId: string,
    messageId: string,
    resultText: string,
    reco?: unknown,
  ): SseEvent[] {
    const events: SseEvent[] = [
      runStarted(threadId, `run-${messageId}`),
      textMessageStart(messageId),
      toolCallStart(callId, 'recommend_products', messageId),
      toolCallArgs(callId, '{"query":"跑步"}'),
      toolCallEnd(callId),
      toolCallResultEncoded(callId, `tr-${callId}`, resultText),
      textMessageContent(messageId, '为您推荐'),
      textMessageEnd(messageId),
    ]
    if (reco !== undefined) events.push(customEvent('recommendation', reco))
    events.push(runFinished(threadId, `run-${messageId}`))
    return events
  }

  it('R5-1 一轮对话至面板更新：fetch 轨迹恰为「既有 REST + POST /agui」，无任何推荐请求', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(chatRound('t-r51', 'a1', '为您推荐', PUSH_ROUND)),
      // 反证路由：客户端若真的去调推荐接口，这次调用会被**记录**下来（而不是让替身因「无匹配
      // 路由」抛错，把失败信号从断言上引开）—— 于是本用例能对「多了一次请求」给出断言级红灯。
      '/recommendations': { products: [] },
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()
    // 正向锚点：挂载期确实发生过既有 REST —— 否则下面的「精确轨迹」可能只是「一条都没记」的巧合。
    expect(stub.callsTo('/models').length).toBeGreaterThan(0)

    await send('推荐一下跑鞋')
    await waitFor(() => expect(cardNames(container)).toEqual(['推送来源商品']))

    // 精确轨迹（非「不含 recommendation 字样」这类弱断言）：多出**任何**一条都是红。
    expect(stub.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'GET /models',
      'POST /agui',
    ])
    expect(stub.callsTo('/recommendations')).toHaveLength(0)
  })

  it('R5-2 空闲 ≥60s：无任何请求、无新建定时器（假定时器自挂载起生效）', async () => {
    // 假定时器必须在**挂载之前**装上：否则挂载期创建的 `setInterval` 是真实定时器，
    // 后面的 `advanceTimersByTime` 推不动它，「不轮询」会退化成一个永远为真的空断言。
    vi.useFakeTimers()
    try {
      const stub = installFetchStub({
        '/models': MODELS,
        '/recommendations': { products: [] },
      })
      restoreFetch = stub.restore

      seedIdentity()
      const { container } = render(createElement(App))

      // 假定时器下 RTL 的 `waitFor` 不会自行推进时间，手工 `act` + 推进 0ms 冲洗微任务链。
      for (let i = 0; i < 50 && container.querySelector('.chat') === null; i += 1) {
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0)
        })
      }

      // 正向锚点：确实已进主界面，且既有 REST 真的发生过。
      expect(container.querySelector('.chat')).not.toBeNull()
      expect(stub.callsTo('/models').length).toBeGreaterThan(0)

      // 「无新建定时器」的判据 = 挂载 + 空转后**一个待触发定时器都没有**（有的话基线本身就 ≥1）。
      expect(vi.getTimerCount()).toBe(0)

      const callsBefore = stub.calls.length
      await act(async () => {
        await vi.advanceTimersByTimeAsync(60_000)
      })

      expect(stub.calls.length).toBe(callsBefore)
      expect(vi.getTimerCount()).toBe(0)
    } finally {
      vi.useRealTimers()
    }
  })

  it('R6-1 收到 CUSTOM：消息与工具调用栏都不变，面板更新（对照「同形状无 CUSTOM」的轮次）', async () => {
    let round = 0
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => {
        round += 1
        if (round === 1) {
          // 正向锚点轮：这一轮**真实**调用了工具 → 工具调用栏条目数必须正常 +1。
          return createSseResponse(
            toolRound('t-r61', 'tc1', 'a1', JSON.stringify(TOOL_ROUND)),
          )
        }
        if (round === 2) {
          // 对照组的另一半：与第 3 轮**同形状**，只多一个 `CUSTOM`。
          return createSseResponse(chatRound('t-r61', 'a2', '第二轮回复', PUSH_ROUND))
        }
        return createSseResponse(chatRound('t-r61', 'a3', '第三轮回复'))
      },
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()
    expect(toolCount(container)).toBe(0)

    await send('推荐一下跑鞋')
    await waitFor(() => expect(toolCount(container)).toBe(1))
    expect(cardNames(container)).toEqual(['工具来源商品'])
    const afterToolRound = getAgent()?.messages.length ?? -1
    expect(afterToolRound).toBeGreaterThan(0)

    // 第 2 轮（含 `CUSTOM`）：面板被换成推送那一路。
    await send('再推荐一次')
    await waitFor(() => expect(cardNames(container)).toEqual(['推送来源商品']))
    const afterCustomRound = getAgent()?.messages.length ?? -1

    // 第 3 轮（同形状、无 `CUSTOM`）：作为对照组。
    await send('随便聊聊')
    await waitFor(() => expect(container.textContent).toContain('第三轮回复'))
    const afterPlainRound = getAgent()?.messages.length ?? -1

    // 关系性断言：`CUSTOM` 轮与「同形状无 CUSTOM」轮对 `agent.messages` 的增量**逐字相等**
    // = `CUSTOM` 事件本身贡献 0 条消息（两轮各自只多了「用户消息 + 助手文本」）。
    expect(afterCustomRound - afterToolRound).toBe(afterPlainRound - afterCustomRound)
    expect(afterCustomRound - afterToolRound).toBeGreaterThan(0)
    // 工具调用栏也不因 `CUSTOM` 增加条目（第 3 轮同样没有新条目）。
    expect(toolCount(container)).toBe(1)
    expect(afterPlainRound).toBeGreaterThan(afterCustomRound)
  })

  it('R2-1 闲聊轮（无 CUSTOM）：面板数量、文案与顺序均不变，不显示空列表也不显示错误', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(chatRound('t-r21', 'a1', '今天天气不错')),
    })
    restoreFetch = stub.restore

    seedIdentity()
    globalThis.localStorage.setItem(STORAGE_KEYS.reco('marla'), JSON.stringify(OLD_ROUND))

    const container = await enterMain()
    // 正向锚点：面板此前**确实**有 2 条内容（否则「不变」是拿空比空）。
    await waitFor(() => expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙']))
    const before = cardTexts(container)

    await send('你好呀')
    // 本轮回复已落地（证明这一轮真的跑完了，且**没有** `CUSTOM`）。
    await waitFor(() => expect(container.textContent).toContain('今天天气不错'))

    expect(container.querySelectorAll('.rcard')).toHaveLength(2)
    expect(cardTexts(container)).toEqual(before)
    expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙'])
    // 既不退化成占位（`.rhint`），也不显示空列表 —— 且没有错误提示。
    expect(container.querySelector('.rhint')).toBeNull()
    expect(container.querySelector('.toast')?.textContent).toBe('')
  })

  it('R7-1 对象型 value：直接序列化后正常渲染（未套工具结果的多编码解码）', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(chatRound('t-r71', 'a1', '给你找了两件', OLD_ROUND)),
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    await send('推荐一下')
    await waitFor(() => expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙']))

    expect(container.querySelectorAll('.rcard')).toHaveLength(2)
    // `reason` 标签与价格文本都在 —— 证明拿到的是**完整的对象内容**（而非被多剥一层的残片）。
    expect(container.textContent).toContain('旧甲')
    expect(container.textContent).toContain('旧乙')
    expect(container.querySelector('.rhint')).toBeNull()
    expect(getRecoSnapshot()).toBe(JSON.stringify(OLD_ROUND))
  })

  it('R7-2 字符串型 value：原样交给同一个解析器，渲染结果与对象型一致', async () => {
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () =>
        createSseResponse(chatRound('t-r72', 'a1', '给你找了两件', JSON.stringify(OLD_ROUND))),
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    await send('推荐一下')
    await waitFor(() => expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙']))

    expect(container.querySelectorAll('.rcard')).toHaveLength(2)
    expect(container.textContent).toContain('旧甲')
    expect(container.querySelector('.rhint')).toBeNull()
    // 字符串型 value **原样保留**（不解析再序列化）：store 里就是收到的那段文本。
    expect(getRecoSnapshot()).toBe(JSON.stringify(OLD_ROUND))
  })

  it('R7-3 非法 value 不崩：缺 products 的对象 / products 非数组 → 面板保留上一次内容', async () => {
    let round = 0
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => {
        round += 1
        if (round === 1) return createSseResponse(chatRound('t-r73', 'a1', '第一轮回复', OLD_ROUND))
        if (round === 2) {
          return createSseResponse(chatRound('t-r73', 'a2', '第二轮回复', { message: '缺 products' }))
        }
        return createSseResponse(chatRound('t-r73', 'a3', '第三轮回复', { products: '不是数组' }))
      },
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    await send('推荐一下')
    await waitFor(() => expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙']))
    const before = cardTexts(container)

    // 非法形态 1：根是对象但不含 `products`。
    await send('再来一次')
    await waitFor(() => expect(container.textContent).toContain('第二轮回复'))
    // 事件**确实**送达了 store（否则下面的「保留」可能只是事件被丢弃的假象）。
    expect(getRecoSnapshot()).toBe(JSON.stringify({ message: '缺 products' }))
    expect(cardTexts(container)).toEqual(before)
    expect(container.querySelector('.rhint')).toBeNull()

    // 非法形态 2：`products` 存在但非数组。
    await send('再来一次')
    await waitFor(() => expect(container.textContent).toContain('第三轮回复'))
    expect(getRecoSnapshot()).toBe(JSON.stringify({ products: '不是数组' }))
    expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙'])
    expect(cardTexts(container)).toEqual(before)

    // 应用不崩溃、不白屏：主界面与输入框都还在，且没有错误提示。
    expect(container.querySelector('.chat')).not.toBeNull()
    expect(screen.getByLabelText('消息')).not.toBeNull()
    expect(container.querySelector('.toast')?.textContent).toBe('')
  })

  it('R9-1 同轮先工具结果、后 CUSTOM：面板最终为 CUSTOM，且不回退到更旧一轮', async () => {
    let round = 0
    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => {
        round += 1
        // 第 1 轮：更早的一轮推荐（后面「不回退」要拿它当反例）。
        if (round === 1) return createSseResponse(chatRound('t-r91', 'a1', '第一轮回复', OLD_ROUND))
        // 第 2 轮：同一轮内先工具结果、后 `CUSTOM`（后到者胜）。
        return createSseResponse(
          toolRound('t-r91', 'tc1', 'a2', JSON.stringify(TOOL_ROUND), PUSH_ROUND),
        )
      },
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    await send('推荐一下')
    await waitFor(() => expect(cardNames(container)).toEqual(['旧轮商品甲', '旧轮商品乙']))

    await send('再推荐一下')
    await waitFor(() => expect(cardNames(container)).toEqual(['推送来源商品']))

    // 正向锚点：同轮的工具结果**确实到达并落库**了（否则「CUSTOM 胜出」可能只是工具结果根本没来）。
    const toolMessage = storedMessages().find(
      (message) => message.role === 'tool' && message.toolCallId === 'tc1',
    )
    expect(toolMessage).toBeDefined()

    expect(container.querySelectorAll('.rcard')).toHaveLength(1)
    expect(container.textContent).not.toContain('工具来源商品')
    expect(container.textContent).not.toContain('旧轮商品甲')
    expect(container.querySelector('.rhint')).toBeNull()
    expect(getRecoSnapshot()).toBe(JSON.stringify(PUSH_ROUND))
  })

  it('R9-2 兜底文案只走工具结果路径：hasRecommendation=false 且无 CUSTOM → 渲染 message，不渲染空列表', async () => {
    const NO_RECO = {
      message: '暂时没有合适的推荐，换个关键词试试',
      hasRecommendation: false,
      categories: [],
      products: [],
    }
    const resultText = JSON.stringify(NO_RECO)

    const stub = installFetchStub({
      '/models': MODELS,
      // 只有工具结果那一路，**没有** `CUSTOM`（门控下 `CUSTOM` 永不承载「无推荐」）。
      '/agui': () => createSseResponse(toolRound('t-r92', 'tc1', 'a1', resultText)),
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()
    // 正向锚点：尚无任何推荐时面板是**占位**文案（与下面的兜底文案不同）。
    expect(container.querySelector('.rhint')?.textContent).not.toBe(NO_RECO.message)

    await send('随便看看')
    await waitFor(() => expect(container.querySelector('.rhint')?.textContent).toBe(NO_RECO.message))

    // 兜底文案 = 结果里的 `message`，不是空列表、不是占位。
    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
    // 工具结果确实到达（与 R9-4 同源的 wire 形状）。
    const toolMessage = storedMessages().find(
      (message) => message.role === 'tool' && message.toolCallId === 'tc1',
    )
    expect(toolMessage?.content).toBe(JSON.stringify(resultText))
    expect(container.querySelector('.toast')?.textContent).toBe('')
  })

  it('R9-4 双编码 TOOL_CALL_RESULT：读取侧剥一层后正常渲染，且持久化内容未被改写', async () => {
    const resultText = JSON.stringify(TOOL_ROUND)
    const wireContent = JSON.stringify(resultText) // 宿主多编码一层后的 wire 值
    expect(wireContent).not.toBe(resultText) // 自证：这确实是「多编码」形态

    const stub = installFetchStub({
      '/models': MODELS,
      '/agui': () => createSseResponse(toolRound('t-r94', 'tc1', 'a1', resultText)),
    })
    restoreFetch = stub.restore

    seedIdentity()
    const container = await enterMain()

    await send('推荐一下')
    // 剥一层后正常渲染卡片（套用两次就会把 JSON 文本的外层引号吃掉，渲染不出来）。
    await waitFor(() => expect(cardNames(container)).toEqual(['工具来源商品']))
    expect(container.querySelector('.rhint')).toBeNull()

    // 解码只发生在**读取侧**：持久化里仍是宿主原样送达的那个值，没有被解回来的文本覆盖。
    const toolMessage = storedMessages().find(
      (message) => message.role === 'tool' && message.toolCallId === 'tc1',
    )
    expect(toolMessage?.content).toBe(wireContent)
    expect(toolMessage?.content).not.toBe(resultText)
  })
})
