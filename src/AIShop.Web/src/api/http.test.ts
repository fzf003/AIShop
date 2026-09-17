/**
 * REST 基座用例（tasks.md C3「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` 的：
 * - R4-2（REST 侧基础）非 2xx 抛带 `status` / `payload` 的错误；2xx 返回解析后的对象；
 * - R4-4「REST 面的 404 走同一分派」：清会话 + 回账户选择页（`onSessionInvalid`）+ 提示；
 * - R4-3 语义：400 / 5xx **不**触发会话失效、**不**清会话（既有历史保留）；
 * - R3-4「两条通道写同一用户名」的 REST 侧：`withUsername` 的身份来自 `currentUsername()`；
 * - R10 第 5 段：单次请求在途时同一按钮的重复点击被忽略（且**不**靠禁用按钮解决）。
 */
import type { Message } from '@ag-ui/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  readMessages,
  readThreadId,
  readUsername,
  writeMessages,
  writeThreadId,
  writeUsername,
} from '../state/session'
import { installFetchStub, jsonResponse, textResponse } from '../test/fetch-stub'
import { ApiError, dispatchApiFailure, onSessionInvalid } from './errors'
import { apiFetch, createInFlightGuard, currentUsername, withUsername } from './http'

const MARLA_HISTORY: Message[] = [
  { id: 'm-u1', role: 'user', content: '你好' },
  { id: 'm-a1', role: 'assistant', content: '你好，我是 AIShop 购物助手' },
]

/** 捕获 `apiFetch` 抛出的 `ApiError`；没抛就显式失败（避免断言空转）。 */
async function captureApiError(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise
  } catch (error) {
    if (error instanceof ApiError) return error
    throw error
  }
  throw new Error('期望抛出 ApiError，但请求成功了')
}

let warnSpy: ReturnType<typeof vi.spyOn>
let restoreFetch: (() => void) | null = null

beforeEach(() => {
  localStorage.clear()
  writeUsername('marla')
  warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
})

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
  warnSpy.mockRestore()
})

describe('apiFetch：状态码 → 值 / ApiError（R4-2 的 REST 侧基础）', () => {
  it('2xx 返回解析后的对象，且请求 URL 自动带上当前身份', async () => {
    const stub = installFetchStub({ '/cart': { items: [], totalItems: 0 } })
    restoreFetch = stub.restore

    const data = await apiFetch<{ items: unknown[]; totalItems: number }>('/cart')

    expect(data).toEqual({ items: [], totalItems: 0 })
    expect(stub.callsTo('/cart')).toHaveLength(1)
    expect(stub.callsTo('/cart')[0]?.search).toBe('?username=marla')
  })

  it('400 / 404 / 500 抛 ApiError，status 与 payload.detail 可读', async () => {
    const stub = installFetchStub({
      '/bad': () => jsonResponse({ detail: 'Quantity must be greater than 0' }, 400),
      '/missing': () => jsonResponse({ detail: 'User not found' }, 404),
      '/boom': () => jsonResponse({ detail: '服务器炸了' }, 500),
    })
    restoreFetch = stub.restore

    const bad = await captureApiError(apiFetch('/bad'))
    expect(bad.status).toBe(400)
    expect(bad.payload).toEqual({ detail: 'Quantity must be greater than 0' })

    const missing = await captureApiError(apiFetch('/missing'))
    expect(missing.status).toBe(404)
    expect((missing.payload as { detail: string }).detail).toBe('User not found')

    const boom = await captureApiError(apiFetch('/boom'))
    expect(boom.status).toBe(500)
    expect(boom).toBeInstanceOf(ApiError)
  })

  it('错误体不是 JSON 时 payload 保留原文（不因解析失败把状态码一起丢掉）', async () => {
    const stub = installFetchStub({ '/gateway': () => textResponse('Bad Gateway', 502) })
    restoreFetch = stub.restore

    const error = await captureApiError(apiFetch('/gateway'))

    expect(error.status).toBe(502)
    expect(error.payload).toBe('Bad Gateway')
  })
})

describe('withUsername / currentUsername：身份单一来源（R3-4 的 REST 侧）', () => {
  it('withUsername 产出的 URL 恒等于 currentUsername() —— 换账户即换身份', () => {
    expect(currentUsername()).toBe('marla')
    expect(withUsername('/cart/items')).toBe('/cart/items?username=marla')
    expect(withUsername('/cart/items')).toBe(`/cart/items?username=${currentUsername()}`)

    // 把身份来源换成另一个用户：URL 必须跟着变。
    // 若实现里把用户名写成了字面量（如硬编码 'marla'），下面两条必然失败。
    writeUsername('steve')
    expect(withUsername('/cart/items')).toBe(`/cart/items?username=${currentUsername()}`)
    expect(withUsername('/cart/items')).toContain('username=steve')
    expect(withUsername('/cart/items')).not.toContain('marla')
  })

  it('路径已带查询串时用 & 追加；未选定账户时直接抛错（fail-fast）', () => {
    expect(withUsername('/cart/items?sort=asc')).toBe('/cart/items?sort=asc&username=marla')

    localStorage.clear()
    expect(currentUsername()).toBeNull()
    expect(() => withUsername('/cart')).toThrow(/尚未选定账户/)
  })
})

describe('dispatchApiFailure：统一分派（R4-1 / R4-3 / R4-4）', () => {
  it('404 User not found → 清当前账户会话 + 通知一次 + 提示可见', async () => {
    writeMessages('marla', MARLA_HISTORY)
    writeThreadId('marla', 't-marla')
    const toast = vi.fn()
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)

    try {
      await dispatchApiFailure(new ApiError(404, { detail: 'User not found' }), { toast })

      expect(readMessages('marla')).toEqual([])
      expect(readThreadId('marla')).toBeNull()
      expect(listener).toHaveBeenCalledTimes(1)
      expect(toast).toHaveBeenCalledTimes(1)
      expect(toast).toHaveBeenCalledWith('用户不存在，请重新选择账户')
      // 边界：只清「该账户的会话与历史」；`agui.username`（应用级选择态）不在这里动（见 handoff-C2）。
      expect(readUsername()).toBe('marla')
    } finally {
      unsubscribe()
    }
  })

  it('400 与 5xx 不触发会话失效、不清会话（既有历史完整保留）', async () => {
    writeMessages('marla', MARLA_HISTORY)
    writeThreadId('marla', 't-marla')
    const toast = vi.fn()
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)

    try {
      await dispatchApiFailure(new ApiError(400, { detail: 'Quantity must be greater than 0' }), { toast })
      await dispatchApiFailure(new ApiError(500, { detail: '服务器炸了' }), { toast })

      expect(listener).not.toHaveBeenCalled()
      expect(readMessages('marla')).toEqual(MARLA_HISTORY)
      expect(readThreadId('marla')).toBe('t-marla')
      expect(toast).toHaveBeenNthCalledWith(1, '数量必须大于 0')
      expect(toast).toHaveBeenNthCalledWith(2, '服务器错误，请稍后重试')
    } finally {
      unsubscribe()
    }
  })

  it('404 Cart item not found → 提示「该商品已不在购物车」并重新拉取校正投影', async () => {
    writeMessages('marla', MARLA_HISTORY)
    const toast = vi.fn()
    const refreshCart = vi.fn()

    await dispatchApiFailure(new ApiError(404, { detail: 'Cart item not found' }), { toast, refreshCart })

    expect(toast).toHaveBeenCalledWith('该商品已不在购物车')
    expect(refreshCart).toHaveBeenCalledTimes(1)
    // 「条目不存在」不属于账户失效：会话必须原样保留。
    expect(readMessages('marla')).toEqual(MARLA_HISTORY)
  })

  it('网络失败（无 status）→ 提示且不动本地状态', async () => {
    writeMessages('marla', MARLA_HISTORY)
    const toast = vi.fn()
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)

    try {
      await dispatchApiFailure(new TypeError('Failed to fetch'), { toast })

      expect(toast).toHaveBeenCalledWith('网络异常，请稍后重试')
      expect(listener).not.toHaveBeenCalled()
      expect(readMessages('marla')).toEqual(MARLA_HISTORY)
    } finally {
      unsubscribe()
    }
  })

  it('带 status/payload 的普通 Error（AG-UI 运行失败的形态）走同一分派', async () => {
    // AG-UI 面的异常由 @ag-ui/client 构造，不是 ApiError 实例 —— 分派必须按形状识别（design §4.3）。
    const runFailure = Object.assign(new Error('HTTP 404'), {
      status: 404,
      payload: { detail: 'User not found' },
    })
    writeMessages('marla', MARLA_HISTORY)
    const toast = vi.fn()
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)

    try {
      await dispatchApiFailure(runFailure, { toast })

      expect(listener).toHaveBeenCalledTimes(1)
      expect(readMessages('marla')).toEqual([])
      expect(toast).toHaveBeenCalledWith('用户不存在，请重新选择账户')
    } finally {
      unsubscribe()
    }
  })

  it('404 但 detail 不是两份已知契约时，不误判为账户失效', async () => {
    writeMessages('marla', MARLA_HISTORY)
    const toast = vi.fn()
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)

    try {
      await dispatchApiFailure(new ApiError(404, { detail: 'Endpoint not found' }), { toast })

      expect(listener).not.toHaveBeenCalled()
      expect(readMessages('marla')).toEqual(MARLA_HISTORY)
      expect(toast).toHaveBeenCalledTimes(1)
    } finally {
      unsubscribe()
    }
  })

  it('退订后不再收到通知', async () => {
    const listener = vi.fn()
    const unsubscribe = onSessionInvalid(listener)
    unsubscribe()

    await dispatchApiFailure(new ApiError(404, { detail: 'User not found' }), { toast: vi.fn() })

    expect(listener).not.toHaveBeenCalled()
  })
})

describe('请求在途防抖（R10 第 5 段）', () => {
  it('同一按钮在途时的重复点击被忽略，action 只执行一次', async () => {
    const guard = createInFlightGuard()
    let release!: () => void
    const action = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          release = () => resolve('done')
        }),
    )

    const first = guard.run('add-3', action)
    expect(guard.isBusy('add-3')).toBe(true)

    const second = await guard.run('add-3', action)
    expect(second).toBeUndefined()
    expect(action).toHaveBeenCalledTimes(1)

    release()
    await expect(first).resolves.toBe('done')
    expect(guard.isBusy('add-3')).toBe(false)
  })

  it('请求完成后同一按钮可再次点击', async () => {
    const guard = createInFlightGuard()
    const action = vi.fn(() => Promise.resolve('ok'))

    await expect(guard.run('clear', action)).resolves.toBe('ok')
    await expect(guard.run('clear', action)).resolves.toBe('ok')
    expect(action).toHaveBeenCalledTimes(2)
  })

  it('不同按钮互不阻塞（防抖是按键的，不是全局串行）', async () => {
    const guard = createInFlightGuard()
    let release!: () => void
    const slow = () => new Promise<string>((resolve) => { release = () => resolve('slow') })
    const fast = vi.fn(() => Promise.resolve('fast'))

    const pending = guard.run('add-1', slow)
    await expect(guard.run('add-2', fast)).resolves.toBe('fast')

    release()
    await pending
  })

  it('action 抛错时也释放在途标记（finally 语义）', async () => {
    const guard = createInFlightGuard()

    await expect(guard.run('x', () => Promise.reject(new Error('boom')))).rejects.toThrow('boom')

    expect(guard.isBusy('x')).toBe(false)
  })
})
