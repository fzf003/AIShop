/**
 * REST 请求基座（spec R3 第 4 段「两条通道写同一用户名」、R4-2 的 REST 侧基础）。
 *
 * 三件事：
 * 1. **身份单一来源** `currentUsername()`：AG-UI 请求体的 `forwardedProps.username` 与 REST URL 的
 *    `?username=` 都从它取 —— 杜绝「聊天用一个用户、购物车用另一个用户」；
 * 2. `withUsername(path)`：把身份拼进查询串（`/cart*` 与 `/products` 都带）；
 * 3. `apiFetch<T>`：统一封装 —— 解析响应体、**非 2xx 抛 `ApiError`**（保留 `status` 与 `payload`）。
 *    错误怎么处理由 `errors.ts` 的 `dispatchApiFailure` 统一分派，本文件只负责「如实抛出」。
 *
 * 边界声明：`?username=` 与 `forwardedProps.username` **不是认证**，只是「自称」——
 * 服务端 3 个种子账户无凭证，写谁就是谁（design §1.2）。服务端的校验只证明该用户名存在。
 */
import { readUsername } from '../state/session'
import { ApiError } from './errors'

/**
 * 当前身份（唯一来源）；未选定账户时为 `null`。
 *
 * 直接读持久化的 `agui.username`（`state/session.ts`），不另存一份内存副本 ——
 * 单一来源不是「多一个模块级变量」，而是「所有通道都只从这一处取值」。
 */
export function currentUsername(): string | null {
  return readUsername()
}

/**
 * 把当前身份拼进 URL 查询串。
 *
 * - 路径已带查询串时用 `&` 追加；
 * - 未选定账户时**直接抛错**（fail-fast）：REST 面缺 `?username=` 服务端一律 400，
 *   静默发一个注定失败的请求只会把缺陷藏起来。
 */
export function withUsername(path: string): string {
  const username = currentUsername()
  if (username === null) {
    throw new Error('[AIShop] 尚未选定账户，无法构造带身份标识的请求 URL')
  }
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}username=${encodeURIComponent(username)}`
}

/**
 * 读响应体：JSON 优先，非 JSON（如网关返回的 HTML / 纯文本）回落为原文，空体为 `null`。
 *
 * 回落而非抛错：错误体格式不受本端控制，读不到 JSON 也要能把 `status` 交出去。
 */
async function readPayload(response: Response): Promise<unknown> {
  const text = await response.text()
  if (text === '') return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

/**
 * 带身份的 REST 请求。
 *
 * - 自动追加 `?username=`（来自 `currentUsername()`）；
 * - 2xx → 返回解析后的响应体（类型由调用方声明）；
 * - 非 2xx → 抛 `ApiError`（`status` + `payload`），由调用方交给 `dispatchApiFailure`；
 * - 网络失败 → `fetch` 自身抛出的 `TypeError` 原样上抛（分派表按「无 status」识别）。
 *
 * 请求体的 `Content-Type` 由调用方在 `init` 里提供（本函数不做推测）。
 */
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(withUsername(path), init)
  const payload = await readPayload(response)
  if (!response.ok) {
    throw new ApiError(response.status, payload)
  }
  return payload as T
}

// ---------------------------------------------------------------------------
// 请求在途防抖
// ---------------------------------------------------------------------------

/** 按键（一个按钮一个键）记录「是否有在途请求」。 */
export interface InFlightGuard {
  /** 该键是否有请求在途（UI 可据此置灰/降透明度，但**不禁用写入**——见下）。 */
  isBusy(key: string): boolean
  /**
   * 执行 `action`；若该键已有请求在途则**不执行**并返回 `undefined`。
   *
   * 无论成功、失败还是抛错都会释放在途标记（`finally`）。
   */
  run<T>(key: string, action: () => Promise<T>): Promise<T | undefined>
}

/**
 * 按键的请求在途防抖（spec R10 第 5 段：运行中**不禁用**购物车写入，
 * 唯一的轻量约束是「单次请求在途时不接受同一按钮的重复点击」）。
 *
 * 返回 `T | undefined`：`undefined` 专指「本次点击被防抖吞掉」，不代表请求结果。
 */
export function createInFlightGuard(): InFlightGuard {
  const busyKeys = new Set<string>()

  return {
    isBusy: (key: string) => busyKeys.has(key),

    run: async <T,>(key: string, action: () => Promise<T>): Promise<T | undefined> => {
      if (busyKeys.has(key)) return undefined
      busyKeys.add(key)
      try {
        return await action()
      } finally {
        busyKeys.delete(key)
      }
    },
  }
}
