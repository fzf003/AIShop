/**
 * 推荐面板用例（tasks.md C8「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` R9「推荐面板由 recommend_products 工具结果驱动」的三个场景：
 * - R9-1 推荐结果驱动面板（含「未发起任何额外推荐请求」）；
 * - R9-2 无推荐时兜底文案（不渲染空列表、不抛异常）；
 * - R9-3 结果非法不崩（保留上一次内容 / 首次即非法则显示占位）。
 *
 * 另覆盖实现步骤要求的「整体替换」「`reason` 为空串不渲染标签」「🛒 加购回调」。
 * 面板**不发任何请求**，故替身只用于记录调用（未注册路由时任何 `fetch` 都会抛错）。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'

import { installFetchStub } from '../test/fetch-stub'
import { render, screen } from '../test/render'
import RecoPanel from './RecoPanel'

let restoreFetch: (() => void) | null = null

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
})

function install(): ReturnType<typeof installFetchStub> {
  restoreFetch?.()
  // 不注册任何路由：一旦面板发起请求，替身会立刻抛错（比「断言 0 请求」更早暴露）。
  const stub = installFetchStub({})
  restoreFetch = stub.restore
  return stub
}

/** `recommend_products` 的结果文本（单行 JSON，形状见 design §9.5）。 */
function contentOf(value: unknown): string {
  return JSON.stringify(value)
}

/** 面板里的商品名顺序（= 当前渲染顺序）。 */
function cardNames(container: HTMLElement): string[] {
  return [...container.querySelectorAll('.rcard .pname')].map((element) => element.textContent ?? '')
}

const TWO_PRODUCTS = {
  message: '根据您的对话，为您推荐：',
  hasRecommendation: true,
  categories: ['鞋类', '配饰'],
  products: [
    {
      id: 1,
      name: '专业跑鞋',
      category: '鞋类',
      price: 129.99,
      emoji: '👟',
      reason: '因为你提到「跑步」',
    },
    {
      id: 3,
      name: '无线降噪耳机',
      category: '配饰',
      price: 249.99,
      emoji: '🎧',
      reason: '根据你的偏好「耳机」',
    },
  ],
}

function renderPanel(content?: string | null) {
  const onAdd = vi.fn()
  const view = render(<RecoPanel content={content} onAdd={onAdd} />)
  return { ...view, onAdd }
}

describe('R9-1 推荐结果驱动面板', () => {
  it('渲染 2 条推荐（名称 / 价格 / 来源标签），且未发起任何额外推荐请求', () => {
    const stub = install()
    const { container } = renderPanel(contentOf(TWO_PRODUCTS))

    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])
    expect(container.querySelector('.rcard .pprice')?.textContent).toBe('¥129.99')
    expect(container.querySelector('.rcard .pthumb')?.textContent).toBe('👟')
    expect(screen.getByText('因为你提到「跑步」')).toBeDefined()
    expect(screen.getByText('根据你的偏好「耳机」')).toBeDefined()

    // 数据只来自工具结果：整个渲染过程零 fetch（服务端没有推荐 HTTP 接口）。
    expect(stub.calls).toHaveLength(0)
  })

  it('🛒 加购入口与全站同形，点击回传商品 id', async () => {
    install()
    const { container, onAdd } = renderPanel(contentOf(TWO_PRODUCTS))

    const buttons = [...container.querySelectorAll<HTMLButtonElement>('.rcard .radd')]
    expect(buttons.map((button) => button.textContent)).toEqual(['🛒', '🛒'])

    buttons[1].click()

    expect(onAdd).toHaveBeenCalledTimes(1)
    expect(onAdd).toHaveBeenCalledWith(3)
  })

  it('每次新结果整体替换（不做增量合并）', () => {
    install()
    const { container, rerender } = renderPanel(contentOf(TWO_PRODUCTS))
    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])

    const singleResult = {
      message: '根据您的兴趣，为您推荐：',
      hasRecommendation: true,
      categories: ['服装'],
      products: [
        {
          id: 4,
          name: '速干跑步背心',
          category: '服装',
          price: 159,
          emoji: '🎽',
          reason: '因为你提到「跑步」',
        },
      ],
    }
    rerender(<RecoPanel content={contentOf(singleResult)} onAdd={vi.fn()} />)

    expect(cardNames(container)).toEqual(['速干跑步背心'])
    expect(screen.queryByText('专业跑鞋')).toBeNull()
    expect(screen.queryByText('无线降噪耳机')).toBeNull()
  })

  it('`reason` 为空串的卡片不渲染来源标签', () => {
    install()
    const mixed = {
      message: '为您精选商品',
      hasRecommendation: true,
      categories: [],
      products: [
        { id: 1, name: '专业跑鞋', category: '鞋类', price: 129.99, emoji: '👟', reason: '' },
        {
          id: 2,
          name: '轻量跑鞋',
          category: '鞋类',
          price: 199,
          emoji: '👟',
          reason: '根据你的偏好「跑鞋」',
        },
      ],
    }
    const { container } = renderPanel(contentOf(mixed))

    const cards = [...container.querySelectorAll('.rcard')]
    expect(cards).toHaveLength(2)
    expect(cards[0].querySelector('.rtag')).toBeNull()
    expect(cards[1].querySelector('.rtag')?.textContent).toBe('根据你的偏好「跑鞋」')
  })
})

describe('R9-2 无推荐时兜底文案', () => {
  it('hasRecommendation:false 且 products:[] → 显示 message、不渲染空列表、不抛异常', () => {
    install()
    const empty = {
      message: '暂无特定推荐 — 浏览精选商品',
      hasRecommendation: false,
      categories: [],
      products: [],
    }
    const { container } = renderPanel(contentOf(empty))

    expect(screen.getByText('暂无特定推荐 — 浏览精选商品')).toBeDefined()
    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
  })

  it('hasRecommendation:true 但 products 为空 → 同样走 message 兜底（不渲染空列表）', () => {
    install()
    const inconsistent = {
      message: '暂无特定推荐',
      hasRecommendation: true,
      categories: [],
      products: [],
    }
    const { container } = renderPanel(contentOf(inconsistent))

    expect(screen.getByText('暂无特定推荐')).toBeDefined()
    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
  })
})

describe('R9-3 结果非法不崩', () => {
  it('非法 JSON → 保留上一次内容，应用不崩', () => {
    install()
    const { container, rerender } = renderPanel(contentOf(TWO_PRODUCTS))
    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])

    rerender(<RecoPanel content="{ 这不是合法 JSON" onAdd={vi.fn()} />)

    // 上一次内容原样保留（不是空列表、也不是错误页）。
    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])
    expect(screen.getByText('因为你提到「跑步」')).toBeDefined()
  })

  it('结构不符（products 非数组 / 条目缺字段）→ 同样保留上一次内容', () => {
    install()
    const { container, rerender } = renderPanel(contentOf(TWO_PRODUCTS))

    rerender(<RecoPanel content={contentOf({ message: 'x', products: 'oops' })} onAdd={vi.fn()} />)
    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])

    rerender(
      <RecoPanel
        content={contentOf({ message: 'x', products: [{ id: 1, name: '缺字段' }] })}
        onAdd={vi.fn()}
      />,
    )
    expect(cardNames(container)).toEqual(['专业跑鞋', '无线降噪耳机'])
  })

  it('从未收到合法结果（非法 JSON / 空串 / undefined）→ 显示占位，不抛异常', () => {
    install()
    const { container, rerender } = renderPanel('{ 坏 JSON')

    expect(container.querySelectorAll('.rcard')).toHaveLength(0)
    expect(container.querySelector('.rhint')?.textContent).toContain('recommend_products')

    rerender(<RecoPanel content="" onAdd={vi.fn()} />)
    expect(container.querySelector('.rhint')).not.toBeNull()

    rerender(<RecoPanel onAdd={vi.fn()} />)
    expect(container.querySelector('.rhint')).not.toBeNull()
  })
})
