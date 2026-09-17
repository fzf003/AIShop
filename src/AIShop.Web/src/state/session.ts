/**
 * 会话持久化（spec R2「消息持久化的键控、完整性与容错」/ design §8.2）。
 *
 * 职责边界：
 * - **键控**：消息与 threadId 一律按用户名分键，不同账户的会话与历史互不覆盖；
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

/** 四个持久化键（design §8.2）。按用户名分区的键用函数形式。 */
export const STORAGE_KEYS = {
  /** 该账户的 threadId（首次进入时 `crypto.randomUUID()` 生成）。 */
  threadId: (username: string) => `agui.threadId.${username}`,
  /** **完整** `agent.messages`（JSON；含 toolCalls 与 tool 结果消息）。 */
  messages: (username: string) => `agui.messages.${username}`,
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
 * 退出登录：清除**当前账户**的会话持久化（threadId + 消息历史）。
 *
 * 语义边界（spec R2 第 3 段 + R12 第 3 段）：
 * - 只删该账户自己的两个键 → **其它账户的历史与 threadId 原样保留**；
 * - 全局选择态（`agui.model` / `agui.username`）不在这里动 —— 它们是「应用级」的当前选择，
 *   不是「某账户的会话数据」，由退出流程按需另行清除；
 * - 清的是**本地**会话，不等同于删除服务端会话。
 */
export function clearSession(username: string): void {
  clearThreadId(username)
  clearMessages(username)
}
