/**
 * 购物车抽屉用例（tasks.md C10「验收 / 测试」的组件层部分）。
 *
 * 覆盖 spec `agui-client` R10 的全部场景（除状态层已覆盖的分派细节）与 R13 第 3 段（Toast 形态与时长）：
 * - R10-1 条目与合计取自 `GET /cart` 响应字段，不解析工具结果文本；
 * - R10-2 加购点击**立即**走 REST，对话区无新增消息、不启动 Agent run；
 * - R10-3 `+` / `−` / `🗑` / 清空直连 REST，每次以响应体刷新、**不补发 `GET /cart`**；
 * - R10-5 对话运行在途时写入照常（按钮未禁用）；
 * - R10-7 入参类失败有可见提示且本地投影不变；
 * - R10-8 / R10-9 / R10-10 快捷加购 chips 的口径与两条门控；
 * - R13-3 顶部居中绿底提示、约 3 秒后自动消失。
 *
 * `Toast` 与抽屉一起渲染：C11 会把它挂在 App 根上，这里按同一种装配方式断言「有可见提示」。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { getSnapshot, endSession, runRound, startSession } from '../agui/store'
import { clearProductsCache } from '../api/products'
import { resetCart } from '../state/cart'
import { writeUsername } from '../state/session'
import { installFetchStub, jsonResponse, type FetchStub, type RouteHandler, type RouteSpec } from '../test/fetch-stub'
import { act, render, screen, userEvent, within } from '../test/render'
import {
  encodeSseBody,
  runFinished,
  runStarted,
  SSE_CONTENT_TYPE,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
} from '../test/sse'
import CartDrawer from './CartDrawer'
import Toast, { dismissToast, showToast } from './Toast'

/** 商品目录替身（`GET /products`）：9 件，用于验证筹码上限 6 与「已加购不出现」。 */
const PRODUCTS = [
  { id: 1, name: '专业跑鞋', category: '鞋类', tags: ['跑步'], price: 129.99, emoji: '👟' },
  { id: 2, name: '轻量跑鞋', category: '鞋类', tags: ['跑步'], price: 199, emoji: '👟' },
  { id: 3, name: '无线降噪耳机', category: '配饰', tags: ['音频'], price: 249.99, emoji: '🎧' },
  { id: 4, name: '速干跑步背心', category: '服装', tags: ['跑步'], price: 159, emoji: '🎽' },
  { id: 5, name: '瑜伽垫', category: '健身', tags: ['瑜伽'], price: 89, emoji: '🧘' },
  { id: 6, name: '保温水壶', category: '配饰', tags: ['户外'], price: 69, emoji: '🍶' },
  { id: 7, name: '运动手表', category: '配饰', tags: ['跑步'], price: 899, emoji: '⌚' },
  { id: 8, name: '篮球', category: '健身', tags: ['球类'], price: 129, emoji: '🏀' },
  { id: 9, name: '护膝', category: '配饰', tags: ['训练'], price: 49, emoji: '🦵' },
]

interface ItemSpec {
  id: string
  productId: number
  quantity: number
}

/**
 * 按「条目规格」构造服务端 `CartResponse` 替身（名称/价格/emoji 取自商品目录，与服务端行为一致）。
 *
 * 刻意不给返回值标注 `CartResponse`：`interface` 没有隐式索引签名，标注后不可赋给 `fetch-stub` 的
 * `JsonValue`（handoff-C9 经验 3）。
 */
function cartWith(specs: readonly ItemSpec[]) {
  const items = specs.map((spec) => {
    const product = PRODUCTS.find((item) => item.id === spec.productId)
    if (product === undefined) throw new Error(`夹具缺商品：${spec.productId}`)
    return {
      id: spec.id,
      productId: spec.productId,
      productName: product.name,
      productPrice: product.price,
      productEmoji: product.emoji,
      quantity: spec.quantity,
      addedAt: '2026-09-17T10:00:00Z',
    }
  })
  return {
    id: 'cart-1',
    items,
    totalItems: items.reduce((sum, item) => sum + item.quantity, 0),
    totalPrice: items.reduce((sum, item) => sum + item.productPrice * item.quantity, 0),
    updatedAt: '2026-09-17T10:00:00Z',
  }
}

const CART_TWO_ITEMS = cartWith([
  { id: 'item-1', productId: 1, quantity: 2 },
  { id: 'item-2', productId: 3, quantity: 1 },
])
const CART_PLUS_ONE = cartWith([
  { id: 'item-1', productId: 1, quantity: 3 },
  { id: 'item-2', productId: 3, quantity: 1 },
])
const CART_ONE_ITEM = cartWith([{ id: 'item-1', productId: 1, quantity: 3 }])
const CART_EMPTY = cartWith([])

let restoreFetch: (() => void) | null = null

beforeEach(() => {
  localStorage.clear()
  writeUsername('marla')
  clearProductsCache()
  resetCart()
  dismissToast()
})

afterEach(() => {
  endSession()
  restoreFetch?.()
  restoreFetch = null
  dismissToast()
})

function install(routes: Record<string, RouteSpec>): FetchStub {
  restoreFetch?.()
  const stub = installFetchStub(routes)
  restoreFetch = stub.restore
  return stub
}

/** 抽屉 + Toast（与 C11 的装配方式一致）。 */
function renderDrawer() {
  const onClose = vi.fn()
  const view = render(
    <>
      <CartDrawer open onClose={onClose} />
      <Toast />
    </>,
  )
  return { ...view, onClose }
}

// 作用域查询：商品名会同时出现在条目与快捷加购 chips 上，故一律先定位到 `.citem` 行。
function rowOf(name: string): HTMLElement {
  const rows = [...document.querySelectorAll<HTMLElement>('.citem')]
  const row = rows.find((item) => item.querySelector('.pname')?.textContent === name)
  if (row === undefined) throw new Error(`抽屉里没有条目：${name}`)
  return row
}

function itemNames(): string[] {
  return [...document.querySelectorAll('.citem .pname')].map((el) => el.textContent ?? '')
}

function qtyOf(name: string): string {
  return rowOf(name).querySelector('.qty span')?.textContent ?? ''
}

function chipTexts(): string[] {
  return [...document.querySelectorAll('.qa-chip')].map((el) => el.textContent ?? '')
}

function total(): string {
  return document.querySelector('.ctot .amt')?.textContent ?? ''
}

function itemCalls(stub: FetchStub) {
  return stub.calls.filter((call) => call.path.startsWith('/cart/items'))
}

function getCartCalls(stub: FetchStub) {
  return stub.calls.filter((call) => call.path === '/cart' && call.method === 'GET')
}

describe('读取与渲染（R10-1）', () => {
  it('打开抽屉走 GET /cart?username=，条目与合计全部取自响应字段', async () => {
    const stub = install({ '/cart': CART_TWO_ITEMS, '/products': PRODUCTS })

    const { container } = renderDrawer()
    await screen.findByText('专业跑鞋')

    const calls = getCartCalls(stub)
    expect(calls).toHaveLength(1)
    expect(calls[0]!.search).toBe('?username=marla')

    // 条目字段直接用响应体（不再按 productId 去商品目录里查名字/价格/emoji）
    expect(itemNames()).toEqual(['专业跑鞋', '无线降噪耳机'])
    expect(rowOf('专业跑鞋').querySelector('.pprice')?.textContent).toBe('¥129.99')
    expect(rowOf('专业跑鞋').querySelector('.pthumb')?.textContent).toBe('👟')
    expect(qtyOf('专业跑鞋')).toBe('2')
    expect(total()).toBe('¥509.97')

    // 未解析任何工具结果文本（`get_cart_summary` 的文本形如 `Id:xxx-name-...`）
    expect(container.textContent).not.toContain('Id:')
  })
})

describe('写入（R10-3）', () => {
  it('`+` 发 PUT 绝对数量、`🗑` 发 DELETE 条目、清空发 DELETE /cart；每次以响应体刷新且不补发 GET /cart', async () => {
    const stub = install({
      '/cart': (ctx) =>
        ctx.call.method === 'DELETE' ? jsonResponse(CART_EMPTY) : jsonResponse(CART_TWO_ITEMS),
      '/cart/items/item-1': CART_PLUS_ONE,
      '/cart/items/item-2': CART_ONE_ITEM,
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')

    await userEvent.click(within(rowOf('专业跑鞋')).getByRole('button', { name: '增加 专业跑鞋 数量' }))
    await vi.waitFor(() => {
      expect(itemCalls(stub)).toHaveLength(1)
    })
    // 写成功后**不补发** GET /cart：刷新完全来自写响应体（本断言是 R10-3 的核心）
    expect(getCartCalls(stub)).toHaveLength(1)
    await vi.waitFor(() => {
      expect(qtyOf('专业跑鞋')).toBe('3')
    })
    // 响应体换了 → 合计随之更新（不是本地自增推算出来的）
    expect(total()).toBe('¥639.96')

    await userEvent.click(within(rowOf('无线降噪耳机')).getByRole('button', { name: '移除 无线降噪耳机' }))
    await vi.waitFor(() => {
      expect(itemNames()).toEqual(['专业跑鞋'])
    })

    await userEvent.click(screen.getByRole('button', { name: '🗑 清空' }))
    await screen.findByText('购物车是空的')

    const calls = itemCalls(stub)
    expect(calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'PUT /cart/items/item-1',
      'DELETE /cart/items/item-2',
    ])
    // PUT 是绝对数量（当前 2 → 3），不是增减量
    expect(calls[0]!.body).toEqual({ quantity: 3 })
    expect(calls.every((call) => call.search === '?username=marla')).toBe(true)

    const clears = stub.calls.filter((call) => call.path === '/cart' && call.method === 'DELETE')
    expect(clears).toHaveLength(1)

    // 三次写都以响应体刷新，**没有**任何补发的 GET /cart（只有打开时那一次）
    expect(getCartCalls(stub)).toHaveLength(1)
  })

  it('数量为 1 时点 `−` 走移除（不发 `quantity: 0` 的 PUT）', async () => {
    const stub = install({
      '/cart': cartWith([{ id: 'item-2', productId: 3, quantity: 1 }]),
      '/cart/items/item-2': CART_EMPTY,
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('无线降噪耳机')

    await userEvent.click(
      within(rowOf('无线降噪耳机')).getByRole('button', { name: '减少 无线降噪耳机 数量' }),
    )
    await screen.findByText('购物车是空的')

    expect(itemCalls(stub).map((call) => call.method)).toEqual(['DELETE'])
  })

  it('`−` 数量大于 1 时发 PUT（当前 2 → 1）', async () => {
    const stub = install({
      '/cart': cartWith([{ id: 'item-1', productId: 1, quantity: 2 }]),
      '/cart/items/item-1': cartWith([{ id: 'item-1', productId: 1, quantity: 1 }]),
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')

    await userEvent.click(within(rowOf('专业跑鞋')).getByRole('button', { name: '减少 专业跑鞋 数量' }))
    await vi.waitFor(() => {
      expect(qtyOf('专业跑鞋')).toBe('1')
    })

    expect(itemCalls(stub).map((call) => call.method)).toEqual(['PUT'])
    expect(itemCalls(stub)[0]!.body).toEqual({ quantity: 1 })
  })
})

describe('加购入口（R10-2）', () => {
  it('点 chip 立即发 POST，抽屉按响应体更新；对话区没有新增消息、也没有启动 Agent run', async () => {
    const stub = install({
      '/cart': cartWith([{ id: 'item-1', productId: 1, quantity: 1 }]),
      '/cart/items': cartWith([
        { id: 'item-1', productId: 1, quantity: 1 },
        { id: 'item-2', productId: 3, quantity: 1 },
      ]),
      '/products': PRODUCTS,
    })
    startSession({ model: 'gpt-4.1' })

    renderDrawer()
    await screen.findByText('专业跑鞋')
    await vi.waitFor(() => {
      expect(chipTexts()).toHaveLength(6)
    })

    await userEvent.click(screen.getByRole('button', { name: /无线降噪耳机/ }))

    await vi.waitFor(() => {
      expect(itemNames()).toEqual(['专业跑鞋', '无线降噪耳机'])
    })
    const posts = itemCalls(stub).filter((call) => call.method === 'POST')
    expect(posts).toHaveLength(1)
    expect(posts[0]!.body).toEqual({ productId: 3, quantity: 1 })
    expect(posts[0]!.search).toBe('?username=marla')

    // 不发消息给 Agent：没有任何 AG-UI 请求，会话消息一条没多
    expect(stub.callsTo('/agui')).toHaveLength(0)
    expect(getSnapshot().messages).toHaveLength(0)
  })

  it('加购成功给出可见提示「已加入购物车：<商品名>」', async () => {
    install({
      '/cart': cartWith([{ id: 'item-1', productId: 1, quantity: 1 }]),
      '/cart/items': cartWith([
        { id: 'item-1', productId: 1, quantity: 1 },
        { id: 'item-2', productId: 3, quantity: 1 },
      ]),
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')
    await vi.waitFor(() => {
      expect(chipTexts()).toHaveLength(6)
    })

    await userEvent.click(screen.getByRole('button', { name: /无线降噪耳机/ }))

    await vi.waitFor(() => {
      expect(screen.getByRole('status').textContent).toBe('已加入购物车：无线降噪耳机')
    })
  })
})

describe('运行在途不禁用写入（R10-5）', () => {
  it('一轮对话进行中点击 `+`：按钮未禁用、请求照常发出并生效', async () => {
    const encoder = new TextEncoder()
    // 默认空实现：`start()` 在构造流时同步执行、随后立刻被真实实现覆盖（避免可空调用的类型收窄问题）。
    let finishRun: () => void = () => undefined

    // 一个「只开头、不结束」的 SSE 流：run 停留在进行中，直到用例主动放行。
    const openEndedRun: RouteHandler = () => {
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(
            encoder.encode(
              encodeSseBody([
                runStarted('t', 'r1'),
                textMessageStart('a1'),
                textMessageContent('a1', '正在思考'),
              ]),
            ),
          )
          finishRun = () => {
            controller.enqueue(
              encoder.encode(encodeSseBody([textMessageEnd('a1'), runFinished('t', 'r1')])),
            )
            controller.close()
          }
        },
      })
      return new Response(body, { headers: { 'Content-Type': SSE_CONTENT_TYPE } })
    }

    const stub = install({
      '/agui': openEndedRun,
      '/cart': CART_TWO_ITEMS,
      '/cart/items/item-1': CART_PLUS_ONE,
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')

    startSession({ model: 'gpt-4.1' })
    const running = runRound('帮我看看')
    await vi.waitFor(() => {
      expect(getSnapshot().isRunning).toBe(true)
    })

    const plus = within(rowOf('专业跑鞋')).getByRole('button', { name: '增加 专业跑鞋 数量' })
    expect((plus as HTMLButtonElement).disabled).toBe(false)

    await userEvent.click(plus)
    await vi.waitFor(() => {
      expect(qtyOf('专业跑鞋')).toBe('3')
    })
    expect(itemCalls(stub).map((call) => call.method)).toEqual(['PUT'])
    // 对话仍在进行中（写请求没有打断它）
    expect(getSnapshot().isRunning).toBe(true)

    finishRun()
    await running
  })
})

describe('失败处理（R10-7）', () => {
  it('写请求 400：给出可见提示、本地投影保持与服务端一致', async () => {
    install({
      '/cart': CART_TWO_ITEMS,
      '/cart/items/item-1': () => jsonResponse({ detail: 'Quantity must be greater than 0' }, 400),
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')

    await userEvent.click(within(rowOf('专业跑鞋')).getByRole('button', { name: '增加 专业跑鞋 数量' }))

    await vi.waitFor(() => {
      expect(screen.getByRole('status').textContent).toBe('数量必须大于 0')
    })
    // 没有伪造出「服务端并不存在的状态」
    expect(qtyOf('专业跑鞋')).toBe('2')
  })

  it('404 Cart item not found：提示 + 重新 GET /cart 校正投影', async () => {
    let cartReads = 0
    const stub = install({
      '/cart': () => {
        cartReads += 1
        return jsonResponse(cartReads === 1 ? CART_TWO_ITEMS : CART_ONE_ITEM)
      },
      '/cart/items/item-2': () => jsonResponse({ detail: 'Cart item not found' }, 404),
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('无线降噪耳机')

    await userEvent.click(within(rowOf('无线降噪耳机')).getByRole('button', { name: '移除 无线降噪耳机' }))

    await vi.waitFor(() => {
      expect(screen.getByRole('status').textContent).toBe('该商品已不在购物车')
    })
    // 重新拉取校正：投影里不再有服务端已经没有的条目
    await vi.waitFor(() => {
      expect(itemNames()).toEqual(['专业跑鞋'])
    })
    expect(getCartCalls(stub)).toHaveLength(2)
  })
})

describe('快捷加购 chips 的门控（R10-8 / R10-9 / R10-10）', () => {
  it('购物车为空：只有空态文案，不渲染 chips 区、也不渲染任何引导入口', async () => {
    const stub = install({ '/cart': CART_EMPTY, '/products': PRODUCTS })

    renderDrawer()
    await screen.findByText('购物车是空的')

    expect(document.querySelector('.qa')).toBeNull()
    expect(document.querySelectorAll('.qa-chip')).toHaveLength(0)
    // 空态本身是一个「只有纯文案」的元素：它内部没有任何按钮
    const empty = screen.getByText('购物车是空的')
    expect(within(empty.parentElement as HTMLElement).queryAllByRole('button')).toHaveLength(0)
    // 空车时也不必去取商品目录
    expect(stub.callsTo('/products')).toHaveLength(0)
  })

  it('车非空且有未加购商品：至多 6 个 chip、已加购的不出现、点击发 POST', async () => {
    const stub = install({
      '/cart': cartWith([
        { id: 'item-1', productId: 1, quantity: 1 },
        { id: 'item-2', productId: 3, quantity: 1 },
      ]),
      '/cart/items': cartWith([
        { id: 'item-1', productId: 1, quantity: 1 },
        { id: 'item-2', productId: 3, quantity: 1 },
        { id: 'item-3', productId: 2, quantity: 1 },
      ]),
      '/products': PRODUCTS,
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')
    await vi.waitFor(() => {
      expect(chipTexts()).toHaveLength(6)
    })

    const chips = chipTexts().join('|')
    // 已加购的 2 件不出现
    expect(chips).not.toContain('专业跑鞋')
    expect(chips).not.toContain('无线降噪耳机')
    // 上限 6：第 7 件未加购商品（护膝）被截掉
    expect(chips).not.toContain('护膝')

    await userEvent.click(screen.getByRole('button', { name: /轻量跑鞋/ }))

    await vi.waitFor(() => {
      expect(itemCalls(stub).filter((call) => call.method === 'POST')).toHaveLength(1)
    })
    expect(itemCalls(stub)[0]!.body).toEqual({ productId: 2, quantity: 1 })
  })

  it('商品全部已加购：chips 区隐藏且不残留上一次的 chips', async () => {
    install({
      '/cart': cartWith([{ id: 'item-1', productId: 1, quantity: 1 }]),
      // 加购之后服务端返回「三件都在车里」→ 没有可展示的未加购商品
      '/cart/items': cartWith([
        { id: 'item-1', productId: 1, quantity: 1 },
        { id: 'item-2', productId: 2, quantity: 1 },
        { id: 'item-3', productId: 3, quantity: 1 },
      ]),
      '/products': [PRODUCTS[0], PRODUCTS[1], PRODUCTS[2]],
    })

    renderDrawer()
    await screen.findByText('专业跑鞋')
    await vi.waitFor(() => {
      expect(chipTexts()).toHaveLength(2)
    })

    await userEvent.click(screen.getByRole('button', { name: /轻量跑鞋/ }))

    await vi.waitFor(() => {
      expect(itemNames()).toHaveLength(3)
    })
    // 不残留上一次的 chips、也不渲染空容器
    expect(document.querySelector('.qa')).toBeNull()
    expect(document.querySelectorAll('.qa-chip')).toHaveLength(0)
  })
})

describe('Toast（R13 第 3 段 / R13-3）', () => {
  it('提示可见、带 show 态，约 3 秒后自动消失', () => {
    vi.useFakeTimers()
    try {
      render(<Toast />)

      act(() => {
        showToast('已加入购物车：专业跑鞋')
      })
      const toast = screen.getByRole('status')
      expect(toast.textContent).toBe('已加入购物车：专业跑鞋')
      expect(toast.className).toBe('toast show')

      act(() => {
        vi.advanceTimersByTime(3000)
      })
      expect(screen.getByRole('status').textContent).toBe('')
      expect(screen.getByRole('status').className).toBe('toast')
    } finally {
      vi.useRealTimers()
    }
  })
})
