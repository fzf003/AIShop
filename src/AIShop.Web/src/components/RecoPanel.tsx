/**
 * 右侧推荐面板（spec R9 / design §7、§9.5）。
 *
 * - 主来源 = 服务端 AG-UI `CUSTOM` 事件（`name: "recommendation"`）；`recommend_products`
 *   工具的 `TOOL_CALL_RESULT.content` 为**兼源**，两者在 store 层归一为同一字符串后交给本组件
 *   （单行 JSON，camelCase：`message` / `hasRecommendation` / `categories` / `products[]`）；
 * - **不**为推荐面板发起任何 HTTP 请求、**不**轮询（spec R9 第 2 段）；
 * - 每次收到新结果**整体替换**，不做增量合并（design §9.5）；
 * - 解析失败（非 JSON / 结构不符）→ **保留上一次内容**；从未有过合法内容则显示占位，绝不抛错（R9-3）；
 * - 卡片 = `emoji` + `name` + `price` + 来源标签 `reason`（`reason` 为空串时不渲染标签）；
 * - `hasRecommendation === false` 或 `products` 为空 → 渲染 `message` 兜底提示（不渲染空列表，R9-2）；
 * - 卡片右侧 = 透明底 🛒 图标按钮（与全站加购入口同形，spec R13-1），动作由上层注入。
 */
import { useEffect, useState } from 'react'

import { formatPrice } from '../api/products'
import './RecoPanel.css'

/** 推荐项 = 归一后的推荐结果里 `products[]` 的单项（本组件只消费这五个字段）。 */
export interface RecoProduct {
  id: number
  name: string
  price: number
  emoji: string
  reason: string
}

/** 已解析的推荐结果视图模型（`hasRecommendation` 已收敛为「有无可渲染的推荐项」）。 */
export interface RecoView {
  message: string
  hasRecommendation: boolean
  products: RecoProduct[]
}

/**
 * 未收到任何（合法）结果时的占位提示（对齐原型 v3 `.rhint`）。
 *
 * 只提示数据来源，**不**承诺任何接口——服务端没有推荐 HTTP 接口（spec R9 第 2 段）。
 */
const PLACEHOLDER =
  '💡 面板会结合对话内容为您推荐商品——不额外请求接口，不额外等一次大模型生成。'

function isRecoProduct(value: unknown): value is RecoProduct {
  if (typeof value !== 'object' || value === null) return false
  const item = value as Record<string, unknown>
  return (
    typeof item.id === 'number' &&
    Number.isFinite(item.id) &&
    typeof item.name === 'string' &&
    typeof item.price === 'number' &&
    Number.isFinite(item.price) &&
    typeof item.emoji === 'string' &&
    typeof item.reason === 'string'
  )
}

/**
 * 解析归一后的推荐结果文本（主来源 = CUSTOM 事件负载；`recommend_products` 工具结果为兼源）。
 *
 * 结构不符（非 JSON / 根非对象 / `products` 非数组 / 任一条目缺 `id`/`name`/`price`/`emoji`/`reason`）
 * 一律返回 `null` —— 由调用方据此**保留上一次内容**，而不是抛错（spec R9-3）。
 *
 * `hasRecommendation` 收敛口径 = 「既非显式 `false`，又有可渲染的条目」，
 * 因此「显式 false」与「products 为空」两种兜底情形走同一条渲染分支（spec R9 第 4 段）。
 */
export function parseRecommendation(content: string | null | undefined): RecoView | null {
  if (typeof content !== 'string' || content.trim() === '') return null

  let raw: unknown
  try {
    raw = JSON.parse(content)
  } catch {
    // 非 JSON：保留上一次内容（不是错误态、更不是崩溃）。
    return null
  }

  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null

  const record = raw as Record<string, unknown>
  if (!Array.isArray(record.products)) return null
  if (!record.products.every(isRecoProduct)) return null

  const products = record.products as RecoProduct[]
  return {
    message: typeof record.message === 'string' ? record.message : '',
    hasRecommendation: record.hasRecommendation !== false && products.length > 0,
    products,
  }
}

export interface RecoPanelProps {
  /**
   * 最近一次推荐结果原文（主来源 = CUSTOM 事件负载；工具结果为兼源，store 层归一）
   * （`undefined` / `null` / 空串 = 尚未收到结果）。
   */
  content?: string | null
  /** 加购入口：由上层接到 `POST /cart/items`（C10 / C11）。 */
  onAdd: (productId: number) => void
}

export default function RecoPanel({ content, onAdd }: RecoPanelProps) {
  const [view, setView] = useState<RecoView | null>(null)

  useEffect(() => {
    const parsed = parseRecommendation(content)
    // 解析失败（null）时**不动** state —— 面板保持上一次内容（spec R9-3）。
    // 解析成功时整体替换，不做增量合并（design §9.5）。
    if (parsed !== null) setView(parsed)
  }, [content])

  return (
    <aside className="reco" aria-label="推荐面板">
      <div className="reco-hd">
        <h4>为你推荐</h4>
        <p>
          结合对话与偏好 · 每轮自动更新
        </p>
      </div>

      <div className="reco-body">
        {view === null && <div className="rhint">{PLACEHOLDER}</div>}

        {view !== null && !view.hasRecommendation && <div className="rhint">{view.message}</div>}

        {view !== null &&
          view.hasRecommendation &&
          view.products.map((product) => (
            <div className="rcard" key={product.id}>
              <div className="pthumb" aria-hidden="true">
                {product.emoji}
              </div>
              <div className="pinfo">
                <div className="pname">{product.name}</div>
                <div className="pprice">{formatPrice(product.price)}</div>
                {/* `reason` 为空串时不渲染来源标签（spec R9 第 3 段）。 */}
                {product.reason !== '' && <div className="rtag">{product.reason}</div>}
              </div>
              <button
                type="button"
                className="radd"
                title="加入购物车"
                onClick={() => onAdd(product.id)}
              >
                🛒
              </button>
            </div>
          ))}
      </div>
    </aside>
  )
}
