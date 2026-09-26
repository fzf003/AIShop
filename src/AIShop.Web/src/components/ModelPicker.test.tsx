/**
 * 账户 / 模型选择器与切屏用例（spec R5、R6，含 2026-09-17 追加的 R6-5 / R6-6 / R6-7）。
 *
 * 全部走公开界面（App 或组件）断言，不触碰内部状态；`GET /models` 用 `fetch-stub` 替身驱动。
 * 断言「不出现硬编码模型名」等否定性结论时，配套的反证见 handoff-C6（把清单改成源码内硬编码 → 相关用例变红）。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import App from '../App'
import { currentUsername } from '../api/http'
import { currentModelId } from '../data/models'
import { readModelId, writeModelId, writeUsername } from '../state/session'
import { installFetchStub, jsonResponse } from '../test/fetch-stub'
import { render, screen, userEvent, waitFor, within } from '../test/render'
import { ModelPicker } from './ModelPicker'

/** 响应顺序的替身（仅 `gpt-4.1` 为默认）：与 tasks.md C6 的验收口径一致。 */
const THREE = [
  { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-flash', isDefault: false },
  { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
  { id: 'qwen', name: 'Qwen 3.7', model: 'qwen3.8-flash', isDefault: false },
]

const ALL_FALSE = THREE.map((model) => ({ ...model, isDefault: false }))

const screenName = (container: HTMLElement): string | null | undefined =>
  container.querySelector('.screen')?.getAttribute('data-screen')

const cardsOf = (container: HTMLElement): HTMLElement[] => [
  ...container.querySelectorAll<HTMLElement>('.mcard'),
]

async function waitForCards(container: HTMLElement): Promise<HTMLElement[]> {
  await waitFor(() => expect(cardsOf(container)).toHaveLength(3))
  return cardsOf(container)
}

beforeEach(() => {
  localStorage.clear()
})

afterEach(() => {
  localStorage.clear()
})

describe('模型选择器（R6）', () => {
  it('网格按响应顺序渲染、标题/副标题取自 name/model、仅 isDefault 带默认角标且初始选中它（R6-1）', async () => {
    const stub = installFetchStub({ '/models': THREE })
    try {
      writeUsername('marla')
      const { container } = render(<App />)
      const cards = await waitForCards(container)

      expect(cards.map((card) => card.getAttribute('data-model-id'))).toEqual([
        'deepseek',
        'gpt-4.1',
        'qwen',
      ])
      expect(within(cards[1]!).getByText('MiMo')).toBeDefined()
      expect(within(cards[1]!).getByText('mimo-v2.5')).toBeDefined()

      // 默认角标只出现一次，且挂在 gpt-4.1 上
      expect(screen.getAllByText('默认')).toHaveLength(1)
      expect(within(cards[1]!).getByText('默认')).toBeDefined()

      // 初始选中 = isDefault 项
      expect(cards[1]!.className).toContain('on')
      expect(cards[0]!.className).not.toContain('on')
      expect(cards[2]!.className).not.toContain('on')
      expect(readModelId()).toBe('gpt-4.1')
    } finally {
      stub.restore()
    }
  })

  it('全部 isDefault 为 false 时选中响应首项且不抛异常（R6-2）', async () => {
    const stub = installFetchStub({ '/models': ALL_FALSE })
    try {
      writeUsername('marla')
      const { container } = render(<App />)
      const cards = await waitForCards(container)

      expect(cards[0]!.className).toContain('on')
      expect(readModelId()).toBe('deepseek')
      expect(screenName(container)).toBe('model')
    } finally {
      stub.restore()
    }
  })

  it('展示清单随响应增减（2 项替身渲染 2 张卡，R6-3）', async () => {
    const stub = installFetchStub({ '/models': THREE.slice(0, 2) })
    try {
      writeUsername('marla')
      const { container } = render(<App />)

      await waitFor(() => expect(cardsOf(container)).toHaveLength(2))
      expect(cardsOf(container).map((card) => card.getAttribute('data-model-id'))).toEqual([
        'deepseek',
        'gpt-4.1',
      ])
    } finally {
      stub.restore()
    }
  })

  it('清单拉取失败 → 错误态 + 重试入口，且页面上不出现任何硬编码模型名（R6-4）', async () => {
    let failing = true
    const stub = installFetchStub({
      '/models': () => (failing ? jsonResponse({ detail: 'boom' }, 500) : jsonResponse(THREE)),
    })
    try {
      writeUsername('marla')
      const { container } = render(<App />)

      expect(await screen.findByText('模型清单拉取失败（HTTP 500）')).toBeDefined()
      expect(screen.getByRole('button', { name: '重试' })).toBeDefined()

      // 不得静默回落本地硬编码清单
      expect(cardsOf(container)).toHaveLength(0)
      expect(screen.queryByText('MiMo')).toBeNull()
      expect(screen.queryByText('Qwen 3.7')).toBeNull()
      expect(screen.queryByText('DeepSeek')).toBeNull()

      // 重试入口真的能恢复
      failing = false
      await userEvent.click(screen.getByRole('button', { name: '重试' }))
      await waitForCards(container)
      expect(screen.getByText('MiMo')).toBeDefined()
    } finally {
      stub.restore()
    }
  })

  it('模型 id 只回传给上层、不作为展示文案（R6 第 4 段）', async () => {
    const selected: string[] = []
    const { container } = render(
      <ModelPicker models={THREE} currentId="gpt-4.1" onSelect={(id) => selected.push(id)} />,
    )

    const text = container.textContent ?? ''
    expect(text).toContain('MiMo')
    expect(text).toContain('mimo-v2.5')
    // 'gpt-4.1' 是节键（回传值），不是展示文案
    expect(text).not.toContain('gpt-4.1')

    await userEvent.click(screen.getByRole('button', { name: /Qwen 3.7/ }))
    expect(selected).toEqual(['qwen'])
  })
})

describe('账户选择与切屏（R5）', () => {
  it('3 张等宽账户卡 → 选账户进模型屏 → 选模型进主界面；刷新后仍回到同一账户与同一模型（R5-1、R5-2、R6-6）', async () => {
    const stub = installFetchStub({ '/models': THREE })
    try {
      const first = render(<App />)

      expect(screenName(first.container)).toBe('account')
      expect(first.container.querySelectorAll('.ubtn')).toHaveLength(3)
      expect(screenName(first.container)).not.toBe('main')

      await userEvent.click(screen.getByRole('button', { name: /Marla/ }))
      expect(screenName(first.container)).toBe('model')

      const cards = await waitForCards(first.container)
      // 选一个**非默认**项：刷新后必须仍是它，而不是回落到 isDefault（R6-6）
      await userEvent.click(within(cards[2]!).getByText('Qwen 3.7'))
      expect(screenName(first.container)).toBe('main')
      expect(readModelId()).toBe('qwen')

      // 模拟刷新：卸载后重新挂载（localStorage 保留）
      first.unmount()
      const second = render(<App />)

      await waitFor(() => expect(screenName(second.container)).toBe('main'))
      await waitFor(() =>
        expect(second.container.querySelector('.mbadge')?.textContent).toContain('Qwen 3.7'),
      )
      expect(readModelId()).toBe('qwen')
    } finally {
      stub.restore()
    }
  })

  it('持久化模型 id 已不在清单中时按同一口径回落且不回传无效 id（R6-7）', async () => {
    const stub = installFetchStub({ '/models': THREE })
    try {
      writeUsername('marla')
      writeModelId('old-model')
      const { container } = render(<App />)

      await waitFor(() => expect(screenName(container)).toBe('main'))
      await waitFor(() =>
        expect(container.querySelector('.mbadge')?.textContent).toContain('MiMo'),
      )

      // 不回传服务端不认识的键
      expect(readModelId()).toBe('gpt-4.1')
      expect(currentModelId()).toBe('gpt-4.1')
    } finally {
      stub.restore()
    }
  })

  it('持久化 id 失效且全部非默认时回落响应首项（R6-7）', async () => {
    const stub = installFetchStub({ '/models': ALL_FALSE })
    try {
      writeUsername('marla')
      writeModelId('old-model')
      const { container } = render(<App />)

      await waitFor(() =>
        expect(container.querySelector('.mbadge')?.textContent).toContain('DeepSeek'),
      )
      expect(readModelId()).toBe('deepseek')
    } finally {
      stub.restore()
    }
  })
})

describe('顶栏切换模型（R6-5）', () => {
  it('对话中切换：徽标与 ✓ 立即更新，setModel 落盘、下一轮取新值', async () => {
    const stub = installFetchStub({ '/models': THREE })
    try {
      writeUsername('marla')
      writeModelId('gpt-4.1')
      const { container } = render(<App />)

      await waitFor(() => expect(container.querySelector('.mbadge')?.textContent).toContain('MiMo'))

      // 切换前：直接断言真实持久化读取（不再用测试自造的对象冒充「在途本轮」，那种断言不经过产品代码、恒真）
      expect(currentModelId()).toBe('gpt-4.1')

      await userEvent.click(container.querySelector('.mbadge')!)
      const dropdown = container.querySelector('.mdd') as HTMLElement
      await userEvent.click(within(dropdown).getByRole('button', { name: /Qwen 3.7/ }))

      // 徽标立即更新为当前选中项
      expect(container.querySelector('.mbadge')?.textContent).toContain('Qwen 3.7')

      // setModel 确实写盘：下一轮构造请求体时读到新值（读的是真实持久化）。
      // 「在途本轮不受影响」由 agent.test.ts 的并发用例覆盖，本文件不再以自造对象冒充证据。
      expect({ username: currentUsername(), model: currentModelId() }).toEqual({
        username: 'marla',
        model: 'qwen',
      })

      // 下拉的 ✓ 也指向新项
      await userEvent.click(container.querySelector('.mbadge')!)
      const reopened = container.querySelector('.mdd') as HTMLElement
      expect(within(reopened).getByRole('button', { name: /Qwen 3.7/ }).textContent).toContain('✓')
      expect(
        within(reopened).getByRole('button', { name: /MiMo/ }).textContent,
      ).not.toContain('✓')
    } finally {
      stub.restore()
    }
  })
})
