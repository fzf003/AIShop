/**
 * 购物车投影 store（spec R10「购物车抽屉走 REST 读写并与 AI 工具共写同一份服务端状态」/ design §9.4）。
 *
 * 三条不变量（本模块的存在理由）：
 * 1. **本地只有一个投影** = 「最近一次服务端响应」——没有乐观自增 / 自减推算，
 *    写成功后**直接用响应体替换**并重渲染，**不补发 `GET /cart`**（R10 第 3 段）；
 * 2. **投影的刷新只有三个来源**（design §9.4「不依赖推送」）：打开抽屉、条目 404 校正、
 *    以及一轮运行结束后**若该轮出现过购物车类工具调用**（AI 侧写入的可见化）；
 * 3. **不因对话运行在途而禁用写入**（R10 第 5 段）——REST 写与服务端 run 无共享可变状态，
 *    唯一的轻量约束是「同一按钮在途时不接受重复点击」（`createInFlightGuard`）。
 *
 * 身份只从 `api/http.ts#withUsername` 取（R10 第 4 段「两条通道写同一用户名」），
 * 本模块不硬编码用户名、也不持有第二份副本。
 */
import type { Message } from '@ag-ui/client'
import { useSyncExternalStore } from 'react'

import { subscribe as subscribeSession, getSnapshot } from '../agui/store'
import {
  addItem,
  clearCart as clearCartRequest,
  getCart,
  removeItem,
  updateQuantity,
  type CartResponse,
} from '../api/cart'
import { dispatchApiFailure } from '../api/errors'
import { createInFlightGuard } from '../api/http'
import { getCachedProducts } from '../api/products'
import { showToast } from '../components/Toast'

/** 交给 React 的购物车状态（每次变更换新引用，满足 `useSyncExternalStore` 的引用比较）。 */
export interface CartState {
  /** **最近一次服务端响应**；尚未拉取过为 `null`。 */
  cart: CartResponse | null
  /** `GET /cart` 是否在途（首次打开抽屉时为 `true`）。 */
  loading: boolean
  /**
   * 最近一次拉取是否失败。
   *
   * 失败时**不**把 `cart` 当成空车渲染 —— 界面不得显示一个服务端并不存在的状态
   * （「购物车是空的」也是一种状态，它必须来自服务端的空车响应）。
   */
  failed: boolean
}

let state: CartState = { cart: null, loading: false, failed: false }

const listeners = new Set<() => void>()

/** 按键的请求在途防抖（同一个按钮一个键）。 */
const guard = createInFlightGuard()

/** 上一次观察到的运行态（用于识别「一轮结束」的 true→false 跳变）。 */
let previousRunning = false

/** 已见过的购物车类工具调用 id（跨轮累计，用于识别「本轮**新增**的调用」）。 */
const knownCartCallIds = new Set<string>()

function setState(patch: Partial<CartState>): void {
  state = { ...state, ...patch }
  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发。
  for (const listener of [...listeners]) listener()
}

/** `useSyncExternalStore` 的快照读取点。 */
export function getCartState(): CartState {
  return state
}

/** 订阅购物车状态；返回退订函数。 */
export function subscribeCart(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** React 侧订阅入口（与 `agui/store.ts#useSession` 同构）。 */
export function useCart(): CartState {
  return useSyncExternalStore(subscribeCart, getCartState, getCartState)
}

/**
 * 清空本地投影（退出登录 / 切换账户 / 测试）。**不**发请求。
 *
 * 购物车属于服务端权威态、且按用户名归属：换了账户后旧投影必须丢弃，否则会把上一个
 * 账户的购物车显示给新账户（重登后打开抽屉时的 `GET /cart` 会重新填充）。
 * 「已见过的工具调用 id」一并清掉，新账户的历史才不会与本账户混在一起。
 */
export function resetCart(): void {
  knownCartCallIds.clear()
  previousRunning = false
  setState({ cart: null, loading: false, failed: false })
}

/**
 * 用服务端返回的「操作后的购物车」替换投影。
 *
 * 四个写端点都返回完整购物车，因此写成功的刷新路径就是本函数（**不再补发 `GET /cart`**）。
 */
export function applyCart(cart: CartResponse): void {
  setState({ cart, loading: false, failed: false })
}

// ---------------------------------------------------------------------------
// 读：GET /cart
// ---------------------------------------------------------------------------

/** 两条通道共用的失败动作：提示出口是 Toast store，`refreshCart` 供「条目已不在购物车」校正。 */
function failureContext() {
  return {
    toast: showToast,
    refreshCart: () => refreshCart(),
  }
}

/**
 * 拉取服务端权威态（打开抽屉 / 条目 404 校正 / 工具轮后同步）。
 *
 * 在途时重复调用被防抖吞掉（返回即结束，不是失败）；失败走统一分派（`errors.ts`），
 * 投影保持不变、只翻 `failed` 标志。
 */
export async function refreshCart(): Promise<void> {
  await guard.run('refresh', async () => {
    setState({ loading: true })
    try {
      applyCart(await getCart())
    } catch (error) {
      setState({ loading: false, failed: true })
      await dispatchApiFailure(error, failureContext())
    }
  })
}

// ---------------------------------------------------------------------------
// 写：4 个 REST 端点
// ---------------------------------------------------------------------------

/**
 * 写操作的公共外壳。
 *
 * - **防抖**：同键请求在途时忽略本次点击（被吞掉的点击返回 `false`，它不是失败、不提示）；
 * - **成功**：以响应体替换投影（不补发 `GET /cart`）；
 * - **失败**：交给统一分派 —— 400 / 5xx **不改动投影**；404 `Cart item not found`
 *   由分派触发一次 `GET /cart` 校正。
 *
 * 全程**不因对话运行在途而阻塞**（R10 第 5 段）。
 */
async function runWrite(key: string, action: () => Promise<CartResponse>): Promise<boolean> {
  const result = await guard.run(key, async () => {
    try {
      applyCart(await action())
      return true
    } catch (error) {
      await dispatchApiFailure(error, failureContext())
      return false
    }
  })
  return result === true
}

/** 加购成功的提示文案（商品名取自商品缓存；未缓存时退化为通用文案，不为此另发请求）。 */
function addedMessage(productId: number): string {
  const name = getCachedProducts()?.find((product) => product.id === productId)?.name
  return name === undefined ? '已加入购物车' : `已加入购物车：${name}`
}

/**
 * 加购（商品卡 / 商品详情 / 推荐卡 / 快捷加购 chip 共用）。
 *
 * **点击立即生效**：直接打 `POST /cart/items`，不发消息给 Agent、不产生对话记录（R10 第 2 段）。
 */
export async function addToCart(productId: number): Promise<void> {
  if (await runWrite(`add:${productId}`, () => addItem(productId, 1))) {
    showToast(addedMessage(productId))
  }
}

/**
 * 抽屉 `+`：`quantity` 是**目标绝对数量**（服务端 `PUT` 语义非累加，故由调用方基于当前响应值算出）。
 *
 * 与「`−`」各用一个防抖键：同一行两个按钮互不吞掉对方的点击。
 */
export async function increaseQuantity(itemId: string, quantity: number): Promise<void> {
  await runWrite(`qty+:${itemId}`, () => updateQuantity(itemId, quantity))
}

/** 抽屉 `−`：同上（数量为 1 时调用方改走 `removeCartItem`，故不会发出 0）。 */
export async function decreaseQuantity(itemId: string, quantity: number): Promise<void> {
  await runWrite(`qty-:${itemId}`, () => updateQuantity(itemId, quantity))
}

/** 移除条目（抽屉 `🗑`；数量为 1 时的 `−` 也走这里）。 */
export async function removeCartItem(itemId: string): Promise<void> {
  await runWrite(`remove:${itemId}`, () => removeItem(itemId))
}

/** 清空购物车（幂等：本就空车也返回 200 空车结构）。 */
export async function clearCart(): Promise<void> {
  await runWrite('clear', () => clearCartRequest())
}

// ---------------------------------------------------------------------------
// AI 侧写入的可见化：一轮结束后按需重新拉取
// ---------------------------------------------------------------------------

/**
 * 「购物车类工具」（spec R10 末段 / design §9.4）。
 *
 * 含只读的 `get_cart_summary`：它不改状态，但多拉一次 `GET /cart` 无副作用（服务端权威态），
 * 而**漏判一个写工具**的代价是「AI 侧加购在抽屉里不可见」——两害相权取其轻。
 */
const CART_TOOL_NAMES = new Set([
  'add_to_cart',
  'update_cart_quantity',
  'remove_from_cart',
  'get_cart_summary',
])

/**
 * 消息序列里出现过的购物车类工具调用 id。
 *
 * 用 **id**（而非消息下标）判定新调用：`agent.messages` 在流式期间**就地修改**同一个数组与
 * 同一条消息对象（`TOOL_CALL_START` 把 `toolCalls` 挂到已存在的 assistant 消息上），
 * 按下标增量扫描会漏掉「先建消息、后挂工具」的那一批（handoff-C4 经验 2）。
 */
function cartCallIds(messages: readonly Message[]): string[] {
  const ids: string[] = []
  for (const message of messages) {
    if (message.role !== 'assistant') continue
    for (const call of message.toolCalls ?? []) {
      if (CART_TOOL_NAMES.has(call.function.name)) ids.push(call.id)
    }
  }
  return ids
}

/**
 * 轮次边界 → 购物车同步（spec R10 末段：一轮运行结束后若该轮出现过购物车类工具调用则重新
 * `GET /cart`；客户端**不假设服务端有推送通道**）。
 *
 * 判定方式：`isRunning` 的 true→false 跳变即「本轮结束」（C4 的 store 已把
 * `RUN_FINISHED` / `RUN_ERROR` 折叠为运行态变化）。轮次开始时先把**已存在**的工具调用 id
 * 记为已知，于是「刷新页面恢复出的历史」不会被误判为本轮新增。只在**本轮新增了**购物车类
 * 工具调用时才拉取，避免每轮结束都打一次 `GET /cart`。
 */
function handleSessionChange(): void {
  const { messages, isRunning } = getSnapshot()

  if (isRunning && !previousRunning) {
    for (const id of cartCallIds(messages)) knownCartCallIds.add(id)
  }

  if (!isRunning && previousRunning) {
    const fresh = cartCallIds(messages).filter((id) => !knownCartCallIds.has(id))
    for (const id of fresh) knownCartCallIds.add(id)
    if (fresh.length > 0) void refreshCart()
  }

  previousRunning = isRunning
}

// 模块加载即订阅：本模块只有内存状态，订阅无外部副作用；不这样做的话，调用方漏调
// `startCartSync()` 会让「AI 侧加购可见化」静默失效（本项目已出现过同类的「装配点漏传参」事故）。
subscribeSession(handleSessionChange)
