/**
 * 购物车 REST 客户端（spec R10「购物车抽屉走 REST 读写并与 AI 工具共写同一份服务端状态」/ design §9.4）。
 *
 * 端点（由变更 `agui-client-support` 交付，全部要求 `?username=`）：
 * - `GET /cart` —— 读权威态（空车也是 **200 + 空车结构**，不是 404）；
 * - `POST /cart/items` body `{productId, quantity}` —— 加购（同商品在车内由服务端累加）；
 * - `PUT /cart/items/{itemId}` body `{quantity}` —— 改量，语义是**绝对数量**（非累加）；
 * - `DELETE /cart/items/{itemId}` —— 移除条目；
 * - `DELETE /cart` —— 清空（幂等）。
 *
 * **四个写端点统一返回「操作后的购物车」**（`CartResponse`），调用方直接用响应体替换本地投影，
 * 不再补发 `GET /cart`（design §9.4）。
 *
 * 身份与 AG-UI 的 `forwardedProps.username` 同源：一律经 `withUsername`（R10 第 4 段）；
 * 非 2xx 统一抛 `ApiError`，交给 `errors.ts` 的 `dispatchApiFailure` 分派（R4 末段）。
 */
import { apiFetch } from './http'

/** 购物车条目（字段与展示一一对应：抽屉**不再**按 `productId` 去商品数据里查名字/价格/emoji）。 */
export interface CartItem {
  /** 条目 ID（GUID）——`PUT` / `DELETE` 的目标。 */
  id: string
  productId: number
  productName: string
  productPrice: number
  productEmoji: string
  quantity: number
  addedAt?: string
}

/** 服务端权威购物车（camelCase，字段与老链同名同义）。 */
export interface CartResponse {
  id: string
  items: CartItem[]
  totalItems: number
  totalPrice: number
  updatedAt?: string
}

const CART_PATH = '/cart'
const CART_ITEMS_PATH = '/cart/items'

const JSON_HEADERS = { 'Content-Type': 'application/json' } as const

function isCartItem(value: unknown): value is CartItem {
  if (typeof value !== 'object' || value === null) return false
  const item = value as Record<string, unknown>
  return (
    typeof item.id === 'string' &&
    typeof item.productId === 'number' &&
    typeof item.productName === 'string' &&
    typeof item.productPrice === 'number' &&
    typeof item.productEmoji === 'string' &&
    typeof item.quantity === 'number'
  )
}

/**
 * 校验响应确实是「操作后的购物车」。
 *
 * 形状不符时**显式抛错**（不把看不懂的响应渲染成「购物车是空的」——那会显示一个服务端并不存在的
 * 状态，正是 spec 第 5 段禁止的）。抛出的错误没有 `status`，分派表按「网络异常」提示。
 */
export function parseCart(payload: unknown): CartResponse {
  if (typeof payload !== 'object' || payload === null) {
    throw new Error('[AIShop] /cart 响应不是对象，无法作为购物车状态使用')
  }
  const cart = payload as Record<string, unknown>
  if (!Array.isArray(cart.items)) {
    throw new Error('[AIShop] /cart 响应缺少 items 数组')
  }
  cart.items.forEach((item, index) => {
    if (!isCartItem(item)) {
      throw new Error(`[AIShop] /cart 响应第 ${index} 个条目缺少必需字段`)
    }
  })
  return payload as CartResponse
}

/** 读购物车（打开抽屉 / 校正投影）。 */
export async function getCart(): Promise<CartResponse> {
  return parseCart(await apiFetch<unknown>(CART_PATH))
}

/**
 * 加购：相同商品已在车内时由服务端累加，客户端不做本地数量推算。
 *
 * **幂等键（L11）**：加购是累加语义，而网络层重试 / 代理重发会让**同一个**请求到达两次 —— 没有幂等
 * 约定时数量会翻倍，且返回 200 看起来一切正常。这里为**每次调用**生成一个新键：同一次用户操作的重发
 * 带同一个键（服务端据此回放首次结果、不再累加），不同操作各带各的键（正常累加）。
 */
export async function addItem(productId: number, quantity = 1): Promise<CartResponse> {
  return parseCart(
    await apiFetch<unknown>(CART_ITEMS_PATH, {
      method: 'POST',
      headers: { ...JSON_HEADERS, 'Idempotency-Key': crypto.randomUUID() },
      body: JSON.stringify({ productId, quantity }),
    }),
  )
}

/**
 * 改量：`quantity` 是**绝对数量**（服务端语义，非累加）。
 *
 * 因此调用方必须基于当前响应值算目标值；`PUT` 不是幂等「增减」，重复发同一个数字是安全的。
 */
export async function updateQuantity(itemId: string, quantity: number): Promise<CartResponse> {
  return parseCart(
    await apiFetch<unknown>(`${CART_ITEMS_PATH}/${encodeURIComponent(itemId)}`, {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({ quantity }),
    }),
  )
}

/** 移除某个条目。 */
export async function removeItem(itemId: string): Promise<CartResponse> {
  return parseCart(
    await apiFetch<unknown>(`${CART_ITEMS_PATH}/${encodeURIComponent(itemId)}`, {
      method: 'DELETE',
    }),
  )
}

/** 清空购物车（幂等：本就空车也返回 200 空车结构）。 */
export async function clearCart(): Promise<CartResponse> {
  return parseCart(await apiFetch<unknown>(CART_PATH, { method: 'DELETE' }))
}
