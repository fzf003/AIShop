/**
 * REST 面错误类型与**统一错误分派**（spec R4「非 200 响应（含非法 username 的 404）的识别与处理」）。
 *
 * 本模块的定位：AG-UI 面（`onRunFailed` 拿到的带 `status`/`payload` 的异常）与 REST 面
 * （`apiFetch` 抛出的 `ApiError`）**共用同一个分派函数**——spec R4 末段明确要求 REST 侧的 404
 * MUST NOT 退化为「只弹一个 Toast」，因为两侧的 404 是服务端同一份实现产生的同一字节契约。
 *
 * 因此分派**不依赖 `instanceof ApiError`**，而是按 `status` / `payload.detail` 鸭子类型识别
 * （AG-UI 侧抛出的只是挂了两个属性的普通 `Error`，见 design §4.3）。
 *
 * 本文件不依赖 React（订阅者由 App 层自行接线）。
 */
import { clearSession, readUsername } from '../state/session'

/**
 * REST 请求的非 2xx 响应（`apiFetch` 抛出）。
 *
 * `status` 与 `payload` 原样保留：分派表按它们决策，调用方也可自行读取
 * （`payload` 可能是解析后的 JSON，也可能是非 JSON 响应的原文）。
 */
export class ApiError extends Error {
  readonly status: number
  readonly payload: unknown

  constructor(status: number, payload: unknown) {
    super(`HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.payload = payload
  }
}

// ---------------------------------------------------------------------------
// 会话失效通知（供 App 层切屏订阅）
// ---------------------------------------------------------------------------

/** 会话失效（服务端判定「该账户不存在」）时被调用；由 App 层决定切回账户选择页。 */
export type SessionInvalidListener = () => void

const sessionInvalidListeners = new Set<SessionInvalidListener>()

/**
 * 订阅「会话失效」。返回值即退订函数（React 侧在 `useEffect` 的清理里调用）。
 *
 * 语义：**只**由 `dispatchApiFailure` 在 404 `User not found` 分支触发，其它失败一律不触发。
 */
export function onSessionInvalid(listener: SessionInvalidListener): () => void {
  sessionInvalidListeners.add(listener)
  return () => {
    sessionInvalidListeners.delete(listener)
  }
}

/** 通知全部订阅者会话已失效（同一订阅者在一次通知里只被调用一次）。 */
export function notifySessionInvalid(): void {
  // 复制一份再遍历：订阅者在回调里退订不会打乱本次遍历。
  for (const listener of [...sessionInvalidListeners]) listener()
}

// ---------------------------------------------------------------------------
// 统一错误分派
// ---------------------------------------------------------------------------

/** 分派所需的外部动作，由调用方（UI 层）注入。 */
export interface ApiFailureContext {
  /** 可见提示（Toast）。两条通道共用同一分派，提示出口由调用方提供。 */
  toast: (message: string) => void
  /**
   * **仅**在 404 `Cart item not found`（条目已被另一条路径移除）时调用：
   * 重新 `GET /cart` 校正本地投影（design §9.4 失败表）。
   */
  refreshCart?: () => void | Promise<void>
}

/** 服务端契约定值（与 `agui-client-support` 的 404 / 400 detail 逐字节一致）。 */
const USER_NOT_FOUND_DETAIL = 'User not found'
const CART_ITEM_NOT_FOUND_DETAIL = 'Cart item not found'
const USERNAME_REQUIRED_DETAIL = 'Username is required'
const QUANTITY_INVALID_DETAIL = 'Quantity must be greater than 0'
const PRODUCT_NOT_FOUND_DETAIL = 'Product not found'

/** 提示文案（不导出：用例按字面量断言，避免「断言与实现同源」的空转）。 */
const MESSAGE_USER_NOT_FOUND = '用户不存在，请重新选择账户'
const MESSAGE_CART_ITEM_NOT_FOUND = '该商品已不在购物车'
const MESSAGE_USERNAME_REQUIRED = '请求缺少用户名参数'
const MESSAGE_QUANTITY_INVALID = '数量必须大于 0'
const MESSAGE_PRODUCT_NOT_FOUND = '商品不存在'
const MESSAGE_BAD_REQUEST = '请求参数有误'
const MESSAGE_SERVER_ERROR = '服务器错误，请稍后重试'
const MESSAGE_NETWORK_ERROR = '网络异常，请稍后重试'
const MESSAGE_UNCLASSIFIED = '请求失败，请稍后重试'

interface NormalizedFailure {
  /** HTTP 状态码；拿不到（网络失败 / 非 HTTP 异常）时为 `null`。 */
  status: number | null
  /** 服务端错误体里的 `detail` 字符串；不存在或非字符串时为 `null`。 */
  detail: string | null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function extractDetail(payload: unknown): string | null {
  if (!isRecord(payload)) return null
  const { detail } = payload
  return typeof detail === 'string' ? detail : null
}

/**
 * 把任意异常归一成 `{ status, detail }`。
 *
 * 鸭子类型（不 `instanceof ApiError`）：AG-UI 面的失败是 `@ag-ui/client` 构造的普通 `Error`
 * 挂了 `status` / `payload`（design §4.3），与 `ApiError` 只有形状一致。
 */
function normalizeFailure(error: unknown): NormalizedFailure {
  const record = isRecord(error) ? error : null
  const status = record !== null && typeof record.status === 'number' ? record.status : null
  const detail = extractDetail(record?.payload)
  return { status, detail }
}

/** 400 的提示文案：已知 detail 译成中文，未知 detail 原样透出（服务端是权威）。 */
function describeBadRequest(detail: string | null): string {
  switch (detail) {
    case USERNAME_REQUIRED_DETAIL:
      return MESSAGE_USERNAME_REQUIRED
    case QUANTITY_INVALID_DETAIL:
      return MESSAGE_QUANTITY_INVALID
    case PRODUCT_NOT_FOUND_DETAIL:
      return MESSAGE_PRODUCT_NOT_FOUND
    default:
      return detail ?? MESSAGE_BAD_REQUEST
  }
}

/**
 * 统一错误分派（AG-UI 面与 REST 面共用的唯一入口）。
 *
 * | 情形 | 动作 |
 * |---|---|
 * | 404 `User not found` | 清当前账户的本地会话 → 通知订阅者（回账户选择页）→ 提示（**不动**购物车投影，那是 UI 层的事） |
 * | 404 `Cart item not found` | 提示「该商品已不在购物车」+ `refreshCart()` 校正投影 |
 * | 400（`Username is required` / 数量 / 商品） | 可见提示，**不改动**本地投影 |
 * | 5xx | 可见提示，**不改动**本地投影、**不自动重试** |
 * | 网络失败 / 其它 | 可见提示（同上） |
 *
 * 除「会话失效」与「条目已不存在」两行外，本函数**不修改任何本地状态**——
 * 界面不得显示一个服务端并不存在的状态（design §9.4 失败表）。
 */
export async function dispatchApiFailure(error: unknown, ctx: ApiFailureContext): Promise<void> {
  const { status, detail } = normalizeFailure(error)

  // 账户已不存在：AG-UI 面与 REST 面走**同一分支**（spec R4 末段）。
  if (status === 404 && detail === USER_NOT_FOUND_DETAIL) {
    const username = readUsername()
    // 只清「该账户的会话与历史」；`agui.username` 是应用级选择态，不在这里动（见 handoff-C2）。
    if (username !== null) clearSession(username)
    notifySessionInvalid()
    ctx.toast(MESSAGE_USER_NOT_FOUND)
    return
  }

  // 条目已被另一条路径移除：投影已过期，必须重拉校正。
  if (status === 404 && detail === CART_ITEM_NOT_FOUND_DETAIL) {
    ctx.toast(MESSAGE_CART_ITEM_NOT_FOUND)
    await ctx.refreshCart?.()
    return
  }

  if (status === 400) {
    ctx.toast(describeBadRequest(detail))
    return
  }

  if (status === null) {
    ctx.toast(MESSAGE_NETWORK_ERROR)
    return
  }

  if (status >= 500) {
    ctx.toast(MESSAGE_SERVER_ERROR)
    return
  }

  // 其它 4xx（例如服务端未归类的 404）：统一提示，不猜测语义。
  ctx.toast(MESSAGE_UNCLASSIFIED)
}
