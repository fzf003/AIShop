/**
 * 购物车 API 用例（L11）。
 *
 * 只覆盖**加购的幂等键**：加购是累加语义（服务端 `existing.Quantity += quantity`），而网络层重试 /
 * 代理重发会让**同一个**请求到达两次 —— 客户端因此为每次调用生成一个 `Idempotency-Key` 随请求带上，
 * 服务端据此回放首次结果、不再累加。
 *
 * 服务端那一半（同一 key 只生效一次、不同 key 正常累加）由 `AguiCartEndpointTests` 的真实宿主用例锁定；
 * 本文件只锁「客户端确实带了、且每次调用换新键」这一发送侧契约。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { writeUsername } from '../state/session'
import { installFetchStub, type FetchStub } from '../test/fetch-stub'
import { addItem } from './cart'

/** 加购的响应形状（本用例只关心请求头，故给最小合法体）。 */
const CART_RESPONSE = { items: [], totalItems: 0, totalPrice: 0 }

let stub: FetchStub | null = null

beforeEach(() => {
  writeUsername('marla')
})

afterEach(() => {
  stub?.restore()
  stub = null
})

describe('api/cart.ts：加购带幂等键（L11）', () => {
  it('每次 addItem 都带 Idempotency-Key，且两次调用用不同的键', async () => {
    stub = installFetchStub({ '/cart/items': CART_RESPONSE })

    await addItem(5)
    await addItem(5)

    const calls = stub.callsTo('/cart/items')
    expect(calls).toHaveLength(2)

    const keys = calls.map((call) => call.headers['Idempotency-Key'])
    expect(keys[0]).toBeTruthy()
    expect(keys[1]).toBeTruthy()

    // 每次用户操作一个新键 —— 服务端据此区分「同一次操作的重发」与「用户真的买两次」。
    expect(keys[0]).not.toBe(keys[1])
  })
})
