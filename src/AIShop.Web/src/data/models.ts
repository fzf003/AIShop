/**
 * 模型清单（spec R6「模型清单消费 GET /models（不得硬编码）」/ design §9.2）。
 *
 * 数据唯一来源 = AguiHost 的 `GET /models`（**根级、公开可读**，端点不做身份校验也不需要 `?username=`）——
 * 客户端 MUST NOT 硬编码模型 id / 显示名 / 数量：服务端增删模型节后客户端零代码改动即随响应变化。
 *
 * 渲染口径（spec R6 第 2 段）：
 * - 网格顺序 = **响应数组顺序**（服务端为配置子键的序数升序，与 appsettings 书写顺序不同，客户端不得假设书写序）；
 * - 初始选中 = `isDefault === true` 的项；全为 false 时回落响应**首项**（界面上恒有选中项）；
 * - 卡片标题 = `name`，副标题 = `model`；`id` 只用于 `forwardedProps.model` 回传，**不作为展示文案**。
 */
import { readModelId } from '../state/session'

/** `GET /models` 响应项（服务端 `ModelDescriptor` 的 camelCase 形状）。 */
export interface ModelInfo {
  /** `Models` 节键 —— `forwardedProps.model` 的回传值（**不是** `model` 字段）。 */
  id: string
  /** 显示名（卡片标题）。 */
  name: string
  /** 真实 wire 模型名（卡片副标题）。 */
  model: string
  /** 是否为服务端默认模型（`ActiveModel` 配成未知键时可能**全为 false**，客户端须自行兜底）。 */
  isDefault: boolean
}

/** 模型清单端点（AguiHost 根级，公开可读、不带身份）。 */
export const MODELS_PATH = '/models'

/**
 * 拉取模型清单。
 *
 * 失败（非 2xx / 网络不可达 / 响应结构非法）一律抛出，由调用方渲染「错误态 + 重试」——
 * MUST NOT 静默回落本地硬编码清单（spec R6 第 4 段）。
 *
 * 不走 `api/http.ts` 的 `apiFetch`：那个封装会给所有 REST 请求追加 `?username=`，
 * 而 `/models` 是**公开可读**的根级端点（前端在选定身份之前就要用它），也不需要账户失效分派。
 */
export async function fetchModels(): Promise<ModelInfo[]> {
  const response = await fetch(MODELS_PATH)
  if (!response.ok) {
    throw new Error(`模型清单拉取失败（HTTP ${response.status}）`)
  }

  const payload: unknown = await response.json()
  if (!Array.isArray(payload)) {
    throw new Error('模型清单响应结构非法（期望数组）')
  }

  return payload as ModelInfo[]
}

/** 未知 `id` 的通用占位图标（spec R6：未知 id MUST 用占位图标，而不是猜一个名字或留空）。 */
const FALLBACK_MODEL_EMOJI = '🧠'

/**
 * 展示用 emoji 映射（按 `id`）。
 *
 * 原型 v3 的卡片带 emoji，而 `GET /models` 的响应里**没有**这个字段 —— spec R6 明确允许客户端维护一份
 * 展示映射，未收录的 `id` 用通用占位图标。这张表**不是**清单来源：数量 / 顺序 / 文案一律以响应为准。
 */
const MODEL_EMOJI: Record<string, string> = {
  qwen: '⚡',
  deepseek: '🐋',
  'gpt-4.1': '🤖',
}

/** 取模型展示图标；未知 `id` 回落通用占位。 */
export function modelEmoji(id: string): string {
  return MODEL_EMOJI[id] ?? FALLBACK_MODEL_EMOJI
}

/**
 * 解析当前应当选中的模型 `id` —— **初始选中与持久化恢复共用同一口径**（spec R6）：
 *
 * 1. 持久化 `id` 仍在清单中 → 用它（刷新后保持同一项，R6-6）；
 * 2. 否则取 `isDefault === true` 的项（R6-1 / R6-7）；
 * 3. 全为 `false` → 取响应数组**首项**（R6-2 / R6-7）；
 * 4. 清单为空 → `null`（服务端未配置任何模型；界面走错误态，不抛异常、不猜一个 id）。
 */
export function resolveModelId(
  models: readonly ModelInfo[],
  persistedId: string | null,
): string | null {
  if (persistedId !== null && models.some((model) => model.id === persistedId)) return persistedId

  const fallback = models.find((model) => model.isDefault) ?? models[0]
  return fallback?.id ?? null
}

/**
 * 当前选中模型的 `id`（即 `forwardedProps.model` 的回传值）。
 *
 * 语义同 `currentUsername()`（`api/http.ts`）：**每次调用时**读取持久化值，不缓存、不在轮次开始时快照。
 * 因此顶栏在对话进行中切换模型时，**已在途的当前轮**（请求体已构造发出）不受影响，
 * **下一轮**构造 `forwardedProps` 时读到的新值才生效（spec R6 的「切换从下一轮生效」）。
 */
export function currentModelId(): string | null {
  return readModelId()
}
