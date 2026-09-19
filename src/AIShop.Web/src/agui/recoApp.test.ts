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
import { afterEach, describe, expect, it } from 'vitest'

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
