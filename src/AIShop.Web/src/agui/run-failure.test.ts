/**
 * 运行失败回调用例（tasks.md C5；spec R4「非 200 响应（含非法 username 的 404）的识别与处理」，硬契约 2）。
 *
 * 覆盖场景：
 * - **R4-1 / R4-2**：AG-UI 面 404 `{"detail":"User not found"}`（响应体**不是 SSE 流**）→ 客户端
 *   由**运行失败回调** `onRunFailed` 拿到带状态码的异常并正确处理（清会话、通知回账户选择页并提示），
 *   界面不卡在运行中、不产生伪造的助手回复；该失败**与 `RUN_ERROR` 事件帧无关**（该路径上根本没有 SSE）；
 * - **R4-3**：5xx → 可见错误提示，且**既有历史完整保留**（不清会话、不清消息）；
 * - **R4-4**：AG-UI 面与 REST 面（`/cart*`）的 404 走**同一个**分派函数（`errors.ts` 的
 *   `dispatchApiFailure`），行为一致 —— 不是「REST 侧只弹一条 Toast 就停在原页面」。
 *
 * 测试走**真实 `@ag-ui/client`**：`fetch` 替身回一个非 SSE 的 404 响应，SDK 的 `runHttpRequest`
 * 自行读错误体、构造挂了 `status` / `payload` 的 `Error` 并派发到 `onRunFailed`（R18-2 同源证据）。
 *
 * ⚠️ **与 design §4.3 / tasks.md C5 文案的偏差（已实测，见 handoff-C5）**：tasks 写「`runAgent()` 的
 * Promise **不会 reject**」，但 `@ag-ui/client` **0.0.59** 的 `AbstractAgent.onError` 在派发
 * `onRunFailed` 之后会把异常**重新抛出**（除非订阅者返回 `stopPropagation: true`，而该字段不在
 * `onRunFailed` 的返回类型里）→ Promise **确实会 reject**。本文件按**实测事实**断言（spec R4 只要求
 * 「通过运行失败回调（而非仅等待 Promise 返回）捕获该失败」，未要求 Promise 不 reject）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, dispatchApiFailure, onSessionInvalid } from '../api/errors'
import { apiFetch } from '../api/http'
import { readMessages, readThreadId, writeMessages, writeUsername } from '../state/session'
import { installFetchStub, jsonResponse } from '../test/fetch-stub'
import {
  createSseResponse,
  runFinished,
  runStarted,
  textMessageContent,
  textMessageEnd,
  textMessageStart,
} from '../test/sse'
import { setToastHandler } from './agent'
import { endSession, getSnapshot, runRound, startSession } from './store'

const MODEL_ID = 'gpt-4.1'

/** 服务端 404 契约（与 `agui-client-support` 的 username 校验短路逐字节一致）。 */
const USER_NOT_FOUND = { detail: 'User not found' }

/** 一轮纯文本对话的 SSE 事件序列（用于先制造一段既有历史）。 */
function textRound(assistantId: string, reply: string) {
  return [
    runStarted('t', 'r1'),
    textMessageStart(assistantId),
    textMessageContent(assistantId, reply),
    textMessageEnd(assistantId),
    runFinished('t', 'r1'),
  ]
}

let restoreFetch: (() => void) | null = null
let unsubscribeInvalid: (() => void) | null = null
let warnSpy: ReturnType<typeof vi.spyOn>
let sessionInvalidations: number

beforeEach(() => {
  localStorage.clear()
  sessionInvalidations = 0
  warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
})

afterEach(() => {
  endSession()
  unsubscribeInvalid?.()
  unsubscribeInvalid = null
  restoreFetch?.()
  restoreFetch = null
  setToastHandler(null)
  warnSpy.mockRestore()
})

describe('agent.ts：运行失败经 onRunFailed 分派（R4-1、R4-2、R4-3、R4-4）', () => {
  it('AG-UI 面 404：经运行失败回调处理（清会话 + 通知 + 提示），非 RUN_ERROR 帧（R4-1、R4-2）', async () => {
    let call = 0
    const stub = installFetchStub({
      '/agui': () => {
        call += 1
        // 第一轮正常（制造既有历史），第二轮返回**非 SSE** 的普通 404 —— 与硬契约 2 的服务端契约一致。
        return call === 1
          ? createSseResponse(textRound('a1', '第一轮回复'))
          : jsonResponse(USER_NOT_FOUND, 404)
      },
    })
    restoreFetch = stub.restore

    const toast = vi.fn()
    setToastHandler(toast)
    unsubscribeInvalid = onSessionInvalid(() => {
      sessionInvalidations += 1
    })

    writeUsername('marla')
    startSession({ model: MODEL_ID })
    await runRound('第一轮')
    expect(readMessages('marla')).toHaveLength(2)

    // 失败经运行失败回调到达，且该异常带 `status` / `payload`（R4-2「拿到带状态码的异常」）。
    // 响应体是普通 JSON、不是 SSE → 此路径上**不存在** `RUN_ERROR` 事件帧。
    // 注：Promise 的 rejection 是本 SDK 版本的行为（见文件头 ⚠️），下面的状态断言才是
    // 「处理确实发生在 onRunFailed 里」的证据 —— 反证（去掉 onRunFailed 订阅）时它们全部变红。
    const failure = await runRound('第二轮').then(
      () => {
        throw new Error('本 SDK 版本下 404 应以异常结束 run')
      },
      (thrown: unknown) => thrown as { status?: number; payload?: unknown },
    )
    expect(failure.status).toBe(404)
    expect(failure.payload).toEqual(USER_NOT_FOUND)

    // 404 的**处理结果**：清当前账户的本地会话与历史 + 通知订阅者（App 据此回账户选择页）+ 提示
    expect(readMessages('marla')).toEqual([])
    expect(readThreadId('marla')).toBeNull()
    expect(sessionInvalidations).toBe(1)
    expect(toast).toHaveBeenCalledWith('用户不存在，请重新选择账户')

    // 不卡在运行中；末条仍是本轮的用户消息 —— 没有把 404 渲染成一条伪造的助手回复
    expect(getSnapshot().isRunning).toBe(false)
    const messages = getSnapshot().messages
    expect(messages[messages.length - 1]?.role).toBe('user')
  })

  it('AG-UI 面 5xx：显示错误提示且既有历史完整保留（R4-3）', async () => {
    let call = 0
    const stub = installFetchStub({
      '/agui': () => {
        call += 1
        return call === 1
          ? createSseResponse(textRound('a1', '第一轮回复'))
          : jsonResponse({ detail: 'boom' }, 500)
      },
    })
    restoreFetch = stub.restore

    const toast = vi.fn()
    setToastHandler(toast)
    unsubscribeInvalid = onSessionInvalid(() => {
      sessionInvalidations += 1
    })

    writeUsername('marla')
    startSession({ model: MODEL_ID })
    await runRound('第一轮')
    const before = readMessages('marla')
    expect(before).toHaveLength(2)

    await runRound('第二轮').catch(() => {
      // 失败以异常结束 run（见文件头 ⚠️）；本用例关心的是失败后的本地状态。
    })

    // 既有历史逐条保留（前缀逐项相等）——5xx **不**清会话、**不**清消息。
    // 总条数比失败前多 1，那是本轮的用户消息（客户端持有全量历史，R1），不是「历史被破坏」。
    const after = readMessages('marla')
    expect(after.slice(0, before.length)).toEqual(before)
    expect(after).toHaveLength(before.length + 1)
    expect(after[after.length - 1]?.role).toBe('user')

    expect(toast).toHaveBeenCalledWith('服务器错误，请稍后重试')
    expect(sessionInvalidations).toBe(0)
    expect(readThreadId('marla')).not.toBeNull()
    expect(getSnapshot().isRunning).toBe(false)
  })

  it('同一份 404 分派：AG-UI 面与 REST 面行为一致（R4-4）', async () => {
    const stub = installFetchStub({
      '/agui': () => jsonResponse(USER_NOT_FOUND, 404),
      '/cart': () => jsonResponse(USER_NOT_FOUND, 404),
    })
    restoreFetch = stub.restore

    const toast = vi.fn()
    setToastHandler(toast)
    unsubscribeInvalid = onSessionInvalid(() => {
      sessionInvalidations += 1
    })

    writeUsername('marla')
    startSession({ model: MODEL_ID })

    // AG-UI 面：SSE 端点返回普通 404
    await runRound('你好').catch(() => {
      /* Promise 的 rejection 见文件头 ⚠️；此处只关心分派结果 */
    })
    expect(sessionInvalidations).toBe(1)
    expect(readMessages('marla')).toEqual([])

    // REST 面：同一个 404 字节契约，走 `apiFetch` → `dispatchApiFailure`（模拟 C10 的购物车写路径）
    writeMessages('marla', [{ id: 'u1', role: 'user', content: '你好' }])
    const error = await apiFetch('/cart').then(
      () => {
        throw new Error('apiFetch 遇到非 2xx 应当抛错')
      },
      (thrown: unknown) => thrown,
    )
    expect(error).toBeInstanceOf(ApiError)
    await dispatchApiFailure(error, { toast })

    // 与 AG-UI 面**完全一致**：清会话 + 通知 + 同一条提示文案（同一分派函数的证据）
    expect(sessionInvalidations).toBe(2)
    expect(readMessages('marla')).toEqual([])
    expect(toast.mock.calls).toEqual([
      ['用户不存在，请重新选择账户'],
      ['用户不存在，请重新选择账户'],
    ])
  })
})
