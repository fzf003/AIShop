/**
 * 购物车投影 store 用例（tasks.md C10「验收 / 测试」的状态层部分）。
 *
 * 覆盖 spec `agui-client` R10（购物车抽屉走 REST 读写并与 AI 工具共写同一份服务端状态）：
 * - R10-1 读走 `GET /cart?username=`，投影就是响应体；
 * - R10-3 写走 4 个 REST 端点、**以响应体替换投影、不补发 `GET /cart`**；
 * - R10-4 一轮出现过购物车类工具调用 → 轮结束后重新 `GET /cart`（AI 侧写入可见化）；
 * - R10-7 失败不改变本地投影（唯一例外：404 `Cart item not found` 触发重新拉取校正）；
 * - R4 末段：404 `User not found` 走与 AG-UI 面**同一个**分派（通知会话失效）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { endSession, runRound, startSession } from '../agui/store'
import { onSessionInvalid } from '../api/errors'
import { withUsername } from '../api/http'
import { clearProductsCache, loadProducts } from '../api/products'
import { dismissToast, getToastMessage } from '../components/Toast'
import { installFetchStub, jsonResponse, type FetchStub, type RouteSpec } from '../test/fetch-stub'
import {
  createSseResponse,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
  toolCallEnd,
  toolCallResult,
  toolCallStart,
} from '../test/sse'
import {
  addToCart,
  clearCart,
  decreaseQuantity,
  getCartState,
  increaseQuantity,
  refreshCart,
  removeCartItem,
  resetCart,
} from './cart'
import { writeUsername } from './session'

/**
 * 服务端 `CartResponse` 的替身（**测试夹具**）。
 *
 * 刻意不标注 `CartResponse` 类型：`interface` 没有隐式索引签名，标注后不可赋给 `fetch-stub` 的
 * `JsonValue`（handoff-C9 经验 3）。
 */
const CART_TWO = {
  id: 'cart-1',
  items: [
    {
      id: 'item-1',
      productId: 1,
      productName: '专业跑鞋',
      productPrice: 129.99,
      productEmoji: '👟',
      quantity: 2,
      addedAt: '2026-09-17T10:00:00Z',
    },
    {
      id: 'item-2',
      productId: 3,
      productName: '无线降噪耳机',
      productPrice: 249.99,
      productEmoji: '🎧',
      quantity: 1,
      addedAt: '2026-09-17T10:05:00Z',
    },
  ],
  totalItems: 3,
  totalPrice: 509.97,
  updatedAt: '2026-09-17T10:05:00Z',
}

/** 「操作后的购物车」替身：加购一件之后的形态。 */
const CART_ONE = {
  id: 'cart-1',
  items: [
    {
      id: 'item-1',
      productId: 1,
      productName: '专业跑鞋',
      productPrice: 129.99,
      productEmoji: '👟',
      quantity: 1,
      addedAt: '2026-09-17T10:00:00Z',
    },
  ],
  totalItems: 1,
  totalPrice: 129.99,
  updatedAt: '2026-09-17T10:10:00Z',
}

/** 空车结构（服务端以 200 + 空车表达，不是 404）。 */
const CART_EMPTY = {
  id: '00000000-0000-0000-0000-000000000000',
  items: [],
  totalItems: 0,
  totalPrice: 0,
  updatedAt: '2026-09-17T10:00:00Z',
}

/** 商品目录替身（只为「已加入购物车：专业跑鞋」这句提示提供商品名）。 */
const PRODUCTS = [
  { id: 1, name: '专业跑鞋', category: '鞋类', tags: ['跑步'], price: 129.99, emoji: '👟' },
]

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

/** 落在 `/cart/items*` 上的写请求（按发生顺序）。 */
function itemCalls(stub: FetchStub) {
  return stub.calls.filter((call) => call.path.startsWith('/cart/items'))
}

function getCartCalls(stub: FetchStub) {
  return stub.calls.filter((call) => call.path === '/cart' && call.method === 'GET')
}

/** 一轮含指定工具调用的 SSE 事件序列。 */
function toolRound(threadId: string, runId: string, toolName: string) {
  return [
    runStarted(threadId, runId),
    textMessageStart('a1'),
    toolCallStart('tc1', toolName, 'a1'),
    toolCallEnd('tc1'),
    toolCallResult('tc1', 'tr1', '已加入购物车'),
    textMessageContent('a1', '已经帮您加好了'),
    textMessageEnd('a1'),
    runFinished(threadId, runId),
  ]
}

/** 一轮纯文本对话的 SSE 事件序列。 */
function textRound(threadId: string, runId: string) {
  return [
    runStarted(threadId, runId),
    textMessageStart('a1'),
    textMessageContent('a1', '你好'),
    textMessageEnd('a1'),
    runFinished(threadId, runId),
  ]
}

describe('读：GET /cart（R10-1 的状态层）', () => {
  it('带当前账户的 ?username=，投影即响应体', async () => {
    const stub = install({ '/cart': CART_TWO })

    await refreshCart()

    const calls = getCartCalls(stub)
    expect(calls).toHaveLength(1)
    expect(calls[0]!.search).toBe('?username=marla')
    // 身份与 AG-UI 面的 forwardedProps.username 同源
    expect(withUsername('/cart')).toBe('/cart?username=marla')
    expect(getCartState().cart).toEqual(CART_TWO)
    expect(getCartState().loading).toBe(false)
  })

  it('空车结构（200 + items: []）也照原样成为投影', async () => {
    install({ '/cart': CART_EMPTY })

    await refreshCart()

    expect(getCartState().cart).toEqual(CART_EMPTY)
    expect(getCartState().cart?.items).toEqual([])
  })
})

describe('写：4 个 REST 端点（R10-2 / R10-3）', () => {
  it('加购：POST /cart/items 立即生效、以响应体替换投影、不补发 GET /cart', async () => {
    const stub = install({
      '/products': PRODUCTS,
      '/cart/items': () => jsonResponse(CART_ONE),
    })
    await loadProducts()

    await addToCart(1)

    const posts = itemCalls(stub)
    expect(posts).toHaveLength(1)
    expect(posts[0]!.method).toBe('POST')
    expect(posts[0]!.search).toBe('?username=marla')
    expect(posts[0]!.body).toEqual({ productId: 1, quantity: 1 })
    // 加购成功提示（spec R13-3；商品名取自商品缓存，未为此另发请求）
    expect(getToastMessage()).toBe('已加入购物车：专业跑鞋')
    // 投影 = 写响应体；**没有**再补一次 GET /cart
    expect(getCartState().cart).toEqual(CART_ONE)
    expect(getCartCalls(stub)).toHaveLength(0)
  })

  it('`+` / `−` / `🗑` / 清空：PUT 绝对数量、DELETE 条目、DELETE /cart，均以响应体刷新', async () => {
    const stub = install({
      '/cart': CART_TWO,
      '/cart/items/item-1': () => jsonResponse(CART_ONE),
      '/cart/items': () => jsonResponse(CART_ONE),
    })

    await increaseQuantity('item-1', 3)
    await decreaseQuantity('item-1', 1)
    await removeCartItem('item-1')
    await clearCart()

    const calls = itemCalls(stub)
    expect(calls.map((call) => call.method)).toEqual(['PUT', 'PUT', 'DELETE'])
    expect(calls.map((call) => call.path)).toEqual([
      '/cart/items/item-1',
      '/cart/items/item-1',
      '/cart/items/item-1',
    ])
    // PUT 语义是**绝对数量**（不是增减量）：+ 传 当前+1、− 传 当前-1
    expect(calls[0]!.body).toEqual({ quantity: 3 })
    expect(calls[1]!.body).toEqual({ quantity: 1 })
    expect(calls.every((call) => call.search === '?username=marla')).toBe(true)

    const clears = stub.calls.filter((call) => call.path === '/cart' && call.method === 'DELETE')
    expect(clears).toHaveLength(1)
    expect(clears[0]!.search).toBe('?username=marla')

    // 每次都用响应体替换投影（最后一步是清空 → 取 DELETE 的响应体）
    expect(getCartState().cart).toEqual(CART_TWO)
    // 全程没有补发 GET /cart
    expect(getCartCalls(stub)).toHaveLength(0)
  })

  it('同一按钮在途时的重复点击被吞掉，不同商品（不同键）互不影响', async () => {
    const stub = install({ '/cart/items': () => jsonResponse(CART_ONE) })

    const first = addToCart(1)
    // 同步发起：第一个请求仍在途 → 同键的这次点击被防抖吞掉
    const duplicated = addToCart(1)
    // 另一个商品是另一个键 → 照常发出
    const other = addToCart(2)

    await Promise.all([first, duplicated, other])

    expect(itemCalls(stub).map((call) => call.body)).toEqual([
      { productId: 1, quantity: 1 },
      { productId: 2, quantity: 1 },
    ])
  })
})

describe('失败：可见提示 + 投影不变（R10-7）', () => {
  it('400 Product not found：提示且投影保持原样', async () => {
    install({
      '/cart': CART_TWO,
      '/cart/items': () => jsonResponse({ detail: 'Product not found' }, 400),
    })
    await refreshCart()
    const before = getCartState().cart

    await addToCart(999)

    expect(getToastMessage()).toBe('商品不存在')
    expect(getCartState().cart).toBe(before)
  })

  it('400 Quantity must be greater than 0：提示且投影保持原样', async () => {
    install({
      '/cart': CART_TWO,
      '/cart/items/item-1': () => jsonResponse({ detail: 'Quantity must be greater than 0' }, 400),
    })
    await refreshCart()
    const before = getCartState().cart

    await increaseQuantity('item-1', 0)

    expect(getToastMessage()).toBe('数量必须大于 0')
    expect(getCartState().cart).toBe(before)
  })

  it('500：提示「服务器错误，请稍后重试」且投影保持原样', async () => {
    install({
      '/cart': CART_TWO,
      '/cart/items': () => jsonResponse({ detail: 'boom' }, 500),
    })
    await refreshCart()
    const before = getCartState().cart

    await addToCart(1)

    expect(getToastMessage()).toBe('服务器错误，请稍后重试')
    expect(getCartState().cart).toBe(before)
  })

  it('404 Cart item not found：提示 + 重新 GET /cart 校正投影（本情形是唯一例外）', async () => {
    let cartReads = 0
    const stub = install({
      '/cart': () => {
        cartReads += 1
        return jsonResponse(cartReads === 1 ? CART_TWO : CART_ONE)
      },
      '/cart/items/item-1': () => jsonResponse({ detail: 'Cart item not found' }, 404),
    })
    await refreshCart()
    expect(getCartState().cart).toEqual(CART_TWO)

    await removeCartItem('item-1')

    expect(getToastMessage()).toBe('该商品已不在购物车')
    expect(getCartCalls(stub)).toHaveLength(2)
    // 投影被重新拉取的权威态修正（不再显示服务端已经没有的条目）
    expect(getCartState().cart).toEqual(CART_ONE)
  })

  it('404 User not found：走与 AG-UI 面同一个分派（通知会话失效）且投影不变', async () => {
    install({
      '/cart': CART_TWO,
      '/cart/items': () => jsonResponse({ detail: 'User not found' }, 404),
    })
    await refreshCart()
    const before = getCartState().cart

    const invalid = vi.fn()
    const unsubscribe = onSessionInvalid(invalid)

    await addToCart(1)

    expect(invalid).toHaveBeenCalledTimes(1)
    expect(getToastMessage()).toBe('用户不存在，请重新选择账户')
    expect(getCartState().cart).toBe(before)
    unsubscribe()
  })
})

describe('AI 侧写入的可见化（R10-4）', () => {
  it('一轮出现过购物车类工具调用 → 轮结束后重新 GET /cart', async () => {
    const stub = install({
      '/agui': () => createSseResponse(toolRound('t', 'r1', 'add_to_cart')),
      '/cart': CART_ONE,
    })
    writeUsername('marla')
    startSession({ model: 'gpt-4.1' })

    await runRound('帮我把跑鞋加到购物车')

    // 轮结束触发的拉取是异步的：等投影落到服务端权威态上
    await vi.waitFor(() => {
      expect(getCartState().cart).toEqual(CART_ONE)
    })
    expect(getCartCalls(stub)).toHaveLength(1)
  })

  it('一轮没有购物车类工具调用（纯文本）→ 不触发 GET /cart', async () => {
    const stub = install({
      '/agui': () => createSseResponse(textRound('t', 'r1')),
      '/cart': CART_ONE,
    })
    writeUsername('marla')
    startSession({ model: 'gpt-4.1' })

    await runRound('你好')

    expect(getCartCalls(stub)).toHaveLength(0)
  })
})

describe('resetCart：换账户后不显示上一个账户的购物车', () => {
  it('丢弃投影，且不再是「已见过的工具调用」', async () => {
    install({ '/cart': CART_TWO })
    await refreshCart()
    expect(getCartState().cart).toEqual(CART_TWO)

    resetCart()

    expect(getCartState().cart).toBeNull()
    expect(getCartState().failed).toBe(false)
  })
})
