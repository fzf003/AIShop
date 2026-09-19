/**
 * 会话持久化（spec R2「消息持久化的键控、完整性与容错」/ design §8.2）。
 *
 * 职责边界：
 * - **键控**：消息、工具调用栏数据、推荐负载与 threadId 一律按用户名分键，不同账户的会话与历史互不覆盖；
 * - **完整性**：消息**整体**序列化（含 assistant 的 `toolCalls` 与配对的 `role: "tool"` 消息）——
 *   只存纯文本会让下一轮出现「有工具结果、无 tool_calls」的孤儿消息（硬契约 1）；
 * - **容错**：存储不可用 / JSON 解析失败 / 结构非法 → 降级为「以空历史开始」+ 一条告警，
 *   绝不抛未捕获异常、绝不白屏。
 *
 * 不持久化的东西（design §8.2）：购物车（服务端权威态，用时 `GET /cart` 拉）与欢迎语
 * （由「消息列表是否为空」派生）。购物车与欢迎语都不在本模块出现。
 *
 * 本模块不使用 `window.*`，一律走 `globalThis`，便于在非浏览器环境（测试 / SSR）复用。
 */
import type { Message } from '@ag-ui/client'

import type { ToolRound } from '../agui/tools'

/** 六个持久化键（design §8.2 / §15.4 / agui-reco-realtime design §4.5 D）。按用户名分区的键用函数形式。 */
export const STORAGE_KEYS = {
  /** 该账户的 threadId（首次进入时 `crypto.randomUUID()` 生成）。 */
  threadId: (username: string) => `agui.threadId.${username}`,
  /** **完整** `agent.messages`（JSON；含 toolCalls 与 tool 结果消息）。 */
  messages: (username: string) => `agui.messages.${username}`,
  /**
   * 该账户的**工具调用栏数据**（`JSON.stringify(tracker.getRounds())`，收口裁决 D3 / design §15.4）。
   *
   * 与 `messages` 是同级的「某账户的会话数据」：写入 MUST 与 `messages` 同一处同一时刻、
   * 清除 MUST 与 `messages` 同生共死（两键内容有重复，各自独立增删会出现漂移）。
   */
  tools: (username: string) => `agui.tools.${username}`,
  /**
   * 该账户的**最近一次推荐负载**（`agui-reco-realtime` spec R8；与 CUSTOM 事件的 `value` 同形状）。
   *
   * 与 `messages` / `tools` 同为「某账户的会话数据」：写入 MUST 与 `messages` 同一处同一时刻、
   * 清除 MUST 与 `messages` 同生共死 —— 否则会出现「消息已清、推荐数据未清」（或反向）的漂移。
   */
  reco: (username: string) => `agui.reco.${username}`,
  /** 当前选中模型的 `id`（配置节键，即 `forwardedProps.model` 的回传值；不存 wire 名/显示名）。 */
  model: 'agui.model',
  /** 上次选择的账户（刷新后回到同一身份）。 */
  username: 'agui.username',
} as const

/** 告警前缀：便于在控制台里与其它日志区分（spec 要求的「一条告警」）。 */
const WARN_PREFIX = '[AIShop] '

function warn(message: string): void {
  console.warn(`${WARN_PREFIX}${message}`)
}

/**
 * 取 `localStorage`；不可用时返回 `null`。
 *
 * 两层都可能抛：某些环境（隐私模式 / 无 DOM）连**访问属性**都会抛，故整体包 try。
 */
function getStorage(): Storage | null {
  try {
    return globalThis.localStorage ?? null
  } catch {
    return null
  }
}

/**
 * 读原始字符串。任何失败（存储不可用 / 读取抛异常）都降级为 `null` + 一条告警 ——
 * 「读不到」与「没写过」在本模块同等对待（都走「空历史」分支）。
 */
function readRaw(key: string): string | null {
  const storage = getStorage()
  if (storage === null) {
    warn(`localStorage 不可用，忽略读取 ${key}`)
    return null
  }
  try {
    return storage.getItem(key)
  } catch (error) {
    warn(`读取 ${key} 失败（已降级）：${String(error)}`)
    return null
  }
}

/** 写原始字符串。失败（配额溢出 / 存储不可用）只告警 —— 持久化失败不得影响当前会话继续使用。 */
function writeRaw(key: string, value: string): void {
  const storage = getStorage()
  if (storage === null) {
    warn(`localStorage 不可用，忽略写入 ${key}`)
    return
  }
  try {
    storage.setItem(key, value)
  } catch (error) {
    warn(`写入 ${key} 失败（本次仅存内存）：${String(error)}`)
  }
}

/** 删键。失败只告警（清不掉不应阻断退出流程）。 */
function removeRaw(key: string): void {
  const storage = getStorage()
  if (storage === null) {
    warn(`localStorage 不可用，忽略删除 ${key}`)
    return
  }
  try {
    storage.removeItem(key)
  } catch (error) {
    warn(`删除 ${key} 失败：${String(error)}`)
  }
}

// ---------------------------------------------------------------------------
// threadId
// ---------------------------------------------------------------------------

/** 读取该账户的 threadId；不存在返回 `null`。 */
export function readThreadId(username: string): string | null {
  return readRaw(STORAGE_KEYS.threadId(username))
}

/** 写入该账户的 threadId。 */
export function writeThreadId(username: string, threadId: string): void {
  writeRaw(STORAGE_KEYS.threadId(username), threadId)
}

/** 删除该账户的 threadId。 */
export function clearThreadId(username: string): void {
  removeRaw(STORAGE_KEYS.threadId(username))
}

/**
 * 取该账户的 threadId；**首次进入时**用 `crypto.randomUUID()` 生成并持久化。
 *
 * threadId 只是 AG-UI 协议的**必填字段**：服务端在身份存在时按用户名归属会话
 * （`store_id = AGUIShopping:{username}`、`conversation_id = username`），threadId 会被忽略，
 * 客户端**不依赖它做隔离或连续性**，也不提供「新建对话」（spec R12）。
 */
export function ensureThreadId(username: string): string {
  const existing = readThreadId(username)
  if (existing !== null) return existing

  const created = crypto.randomUUID()
  writeThreadId(username, created)
  return created
}

// ---------------------------------------------------------------------------
// 消息历史
// ---------------------------------------------------------------------------

/**
 * 读取该账户的完整消息序列。
 *
 * 降级口径（spec R2 第 2 段）：无历史 / 存储不可用 / JSON 解析失败 / 结构非数组
 * → 返回 `[]`（空历史）并告警，绝不抛出。
 */
export function readMessages(username: string): Message[] {
  const key = STORAGE_KEYS.messages(username)
  const raw = readRaw(key)
  // 「没写过」不是异常（新账户 / 退出后重进），不告警。
  if (raw === null) return []

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch (error) {
    warn(`历史 JSON 解析失败，以空历史开始（${key}）：${String(error)}`)
    return []
  }

  if (!Array.isArray(parsed)) {
    warn(`历史结构非法（期望数组），以空历史开始（${key}）`)
    return []
  }

  return parsed as Message[]
}

/**
 * 写入该账户的完整消息序列。
 *
 * 必须传**全量**消息（含 `toolCalls` 与配对的 tool 结果消息）：服务端 `ProvideChatHistoryAsync`
 * 恒返回空，模型上下文完全来自下一轮重发的 `messages`，裁剪或只存纯文本都会破坏上下文。
 */
export function writeMessages(username: string, messages: readonly Message[]): void {
  writeRaw(STORAGE_KEYS.messages(username), JSON.stringify(messages))
}

/** 删除该账户的消息历史。 */
export function clearMessages(username: string): void {
  removeRaw(STORAGE_KEYS.messages(username))
}

// ---------------------------------------------------------------------------
// 工具调用栏数据（收口裁决 D3 / design §15.3 + §15.4）
// ---------------------------------------------------------------------------

/**
 * 读取该账户的工具调用栏数据（`ToolRound[]` 纯数据视图）。
 *
 * 降级口径与 `readMessages` **完全一致**（spec R8 追加条款末段）：无数据 / 存储不可用 /
 * JSON 解析失败 / 结构非数组 → 返回 `[]`（以空开始）并告警，绝不抛出、绝不白屏。
 *
 * ⚠️ 返回的 `ToolCallView.result` 是**已解码**的值（写入前 wire 边界已剥掉宿主多编码层），
 * 调用方（`createToolTracker` 的 `initialRounds`）**MUST NOT** 再调 `decodeToolResultContent`。
 */
export function readToolRounds(username: string): ToolRound[] {
  const key = STORAGE_KEYS.tools(username)
  const raw = readRaw(key)
  // 「没写过」不是异常（新账户 / 退出后重进），不告警。
  if (raw === null) return []

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch (error) {
    warn(`工具调用数据 JSON 解析失败，以空开始（${key}）：${String(error)}`)
    return []
  }

  if (!Array.isArray(parsed)) {
    warn(`工具调用数据结构非法（期望数组），以空开始（${key}）`)
    return []
  }

  return parsed as ToolRound[]
}

/**
 * 写入该账户的工具调用栏数据（`tracker.getRounds()` 的原文序列化）。
 *
 * 与 `writeMessages` **同一处、同一时刻**调用（`agui/store.ts` 的通知链上，见 `App.tsx` 的装配），
 * 两键因此不会漂移（design §15.4「为什么写死同一处同一时刻」）。
 */
export function writeToolRounds(username: string, rounds: readonly ToolRound[]): void {
  writeRaw(STORAGE_KEYS.tools(username), JSON.stringify(rounds))
}

/** 删除该账户的工具调用栏数据（只由 `clearSession` 调用，与消息历史同生共死）。 */
export function clearToolRounds(username: string): void {
  removeRaw(STORAGE_KEYS.tools(username))
}

// ---------------------------------------------------------------------------
// 推荐负载（agui-reco-realtime spec R8 / design §4.5 D）
// ---------------------------------------------------------------------------

/**
 * 读取该账户的最近一次推荐负载（**原文文本**，直接喂 `parseRecommendation`，不做二次解析分叉）。
 *
 * 降级口径与 `readToolRounds` **完全一致**（spec R8 第 4 段）：无数据（从未写过）/ 存储不可用 /
 * JSON 解析失败 / 结构既非对象也非字符串 / 是对象但 `products` 非数组 → 返回 `null`
 * （面板走占位或历史工具结果回退）并告警，绝不抛出、绝不白屏。
 *
 * 合法值原样返回**原文**（不 `JSON.parse` 后再 `stringify`）：面板需要的是可以直接
 * `parseRecommendation` 的 JSON 文本，回写时也必须逐字节可逆（刷新前后面板内容一致）。
 */
export function readReco(username: string): string | null {
  const key = STORAGE_KEYS.reco(username)
  const raw = readRaw(key)
  // 「没写过」不是异常（新账户 / 退出后重进），不告警。
  if (raw === null) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch (error) {
    warn(`推荐数据 JSON 解析失败，以空开始（${key}）：${String(error)}`)
    return null
  }

  // 字符串（JSON 字符串字面量）与「带 products 数组的对象」是两种合法结构。
  if (typeof parsed !== 'string') {
    const isPlainObject = typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
    if (!isPlainObject || !Array.isArray((parsed as { products?: unknown }).products)) {
      warn(`推荐数据结构非法（期望推荐负载对象或字符串），以空开始（${key}）`)
      return null
    }
  }

  return raw
}

/**
 * 写入该账户的最近一次推荐负载（归一化后的推荐结果 JSON 文本）。
 *
 * 与 `writeMessages` / `writeToolRounds` **同一处、同一时刻**调用（见 `App.tsx` 的通知链），
 * 三键因此不会漂移（agui-reco-realtime design §4.5 D）。
 */
export function writeReco(username: string, value: string): void {
  writeRaw(STORAGE_KEYS.reco(username), value)
}

/** 删除该账户的推荐负载（只由 `clearSession` 调用，与消息历史同生共死）。 */
export function clearReco(username: string): void {
  removeRaw(STORAGE_KEYS.reco(username))
}

// ---------------------------------------------------------------------------
// 全局选择态（模型 / 账户）
// ---------------------------------------------------------------------------

/** 当前选中模型的 `id`（节键）；未选过返回 `null`。 */
export function readModelId(): string | null {
  return readRaw(STORAGE_KEYS.model)
}

export function writeModelId(modelId: string): void {
  writeRaw(STORAGE_KEYS.model, modelId)
}

export function clearModelId(): void {
  removeRaw(STORAGE_KEYS.model)
}

/** 上次选择的账户；未选过返回 `null`。 */
export function readUsername(): string | null {
  return readRaw(STORAGE_KEYS.username)
}

export function writeUsername(username: string): void {
  writeRaw(STORAGE_KEYS.username, username)
}

export function clearUsername(): void {
  removeRaw(STORAGE_KEYS.username)
}

// ---------------------------------------------------------------------------
// 组合操作
// ---------------------------------------------------------------------------

/**
 * 退出登录：清除**当前账户**的会话持久化（threadId + 消息历史 + 工具调用栏数据 + 推荐负载）。
 *
 * 语义边界（spec R2 第 3 段 + R12 第 3 段 + agui-reco-realtime R8 第 4 段）：
 * - 只删该账户自己的四个键 → **其它账户的历史与 threadId 原样保留**；
 * - 工具调用栏数据与消息历史**同生共死**（spec R8 追加条款）：它里面有一部分内容（工具名 / 参数 /
 *   结果）与 `messages` 重复，只清一个会留下「指向不存在消息」的孤儿工具数据；
 * - 推荐负载同样与消息历史**同生共死**（agui-reco-realtime R8）：推荐面板的内容是「上一轮会话」
 *   的产物，只清消息不清推荐会留下「消息已清、推荐数据未清」的漂移（下次进入该账户时面板会
 *   显示上一个会话残留的推荐）；反向漏清 `messages` 也同理 —— 四键必须一起消失；
 * - 全局选择态（`agui.model` / `agui.username`）不在这里动 —— 它们是「应用级」的当前选择，
 *   不是「某账户的会话数据」，由退出流程按需另行清除；
 * - 清的是**本地**会话，不等同于删除服务端会话。
 */
export function clearSession(username: string): void {
  clearThreadId(username)
  clearMessages(username)
  clearToolRounds(username)
  clearReco(username)
}
