/**
 * 工具胶囊（tasks.md C7；spec R8 / design §7、§9.6 / 原型 v3 `.tool`）。
 *
 * 折叠态 = 药丸（工具图标 + 等宽工具名 + 实测耗时 + 展开箭头）；
 * 展开态 = 元信息行（耗时 / 状态 / [整轮 token]）+「参数」段（`key: value` 芯片）+「结果」段。
 *
 * Token 口径（spec R8 第 3 段）：**逐工具 token 不可得**，`usage` 是**该轮汇总**，
 * 因此仅在 `usage !== null` 时渲染，并**显式标注整轮口径**；为 `null` 时整项不渲染
 * （不显示 0、不显示「—」、不估算）。
 */
import { useState } from 'react'

import type { ToolCallView, ToolStatus, UsageSummary } from '../agui/tools'
import './ToolChip.css'

/** 已知工具的图标（原型 v3 的 🔍 / 🛒 / ✨；未知工具用通用扳手）。 */
const TOOL_ICONS: Readonly<Record<string, string>> = {
  search_product: '🔍',
  add_to_cart: '🛒',
  update_cart_quantity: '🛒',
  remove_from_cart: '🛒',
  get_cart_summary: '🧾',
  recommend_products: '✨',
}
const DEFAULT_ICON = '🔧'

const STATUS_LABELS: Readonly<Record<ToolStatus, string>> = {
  running: '进行中',
  success: '成功',
  failure: '失败',
}

export interface ToolChipProps {
  tool: ToolCallView
  /** 该工具所属轮的**整轮**用量；无则为 `null`（token 项整体不渲染）。 */
  usage: UsageSummary | null
}

/** 耗时文案：无 RESULT 时为「进行中」（design §9.6）。 */
function durationLabel(tool: ToolCallView): string {
  return tool.durationMs === null ? '进行中' : `${tool.durationMs}ms`
}

/** 整轮用量文案（只拼装已有字段，缺项不补 0）。 */
function usageLabel(usage: UsageSummary): string {
  const parts: string[] = []
  if (usage.inputTokens !== null) parts.push(`输入 ${usage.inputTokens}`)
  if (usage.outputTokens !== null) parts.push(`输出 ${usage.outputTokens}`)
  if (usage.totalTokens !== null) parts.push(`合计 ${usage.totalTokens}`)
  return parts.join(' · ')
}

/** 参数芯片的值：字符串带引号（对齐原型 `query: "跑鞋"`），其余走 JSON 字面量。 */
function argumentValue(value: unknown): string {
  if (value === undefined) return 'undefined'
  return JSON.stringify(value) ?? String(value)
}

/** 结果段文本：可解析的 JSON 缩进美化，其余（含服务端的分行纯文本）原样展示。 */
function resultText(result: string): string {
  const trimmed = result.trim()
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    try {
      const parsed: unknown = JSON.parse(trimmed)
      return JSON.stringify(parsed, null, 2) ?? result
    } catch {
      // 非法 JSON（流被截断 / 纯文本）→ 原样展示
    }
  }
  return result
}

export default function ToolChip({ tool, usage }: ToolChipProps) {
  const [open, setOpen] = useState(false)
  const duration = durationLabel(tool)
  const args = Object.entries(tool.args)

  return (
    <div className={open ? 'tool open' : 'tool'}>
      <button
        type="button"
        className="tool-chip"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
      >
        <span aria-hidden="true">{TOOL_ICONS[tool.name] ?? DEFAULT_ICON}</span>
        <span className="nm">{tool.name}</span>
        <span className="t">{duration}</span>
        <span className="caret" aria-hidden="true">
          ▼
        </span>
      </button>

      {/* 展开体仅在展开时渲染（折叠态不进 DOM）：原型靠类名切 `display`，此处用条件渲染，
          折叠态的胶囊因此对可访问树与测试都真正不可见。 */}
      {open && (
        <div className="tool-body">
          <div className="tool-meta">
            <span>
              耗时 <b>{duration}</b>
            </span>
            <span>
              状态 <b className={`st ${tool.status}`}>{STATUS_LABELS[tool.status]}</b>
            </span>
            {usage !== null && (
              <span className="usage">
                本轮 token（整轮口径） <b>{usageLabel(usage)}</b>
              </span>
            )}
          </div>

          <div className="tool-sec">
            <div className="lbl">参数</div>
            {args.length === 0 ? (
              <span className="empty">无参数</span>
            ) : (
              args.map(([key, value]) => <code key={key}>{`${key}: ${argumentValue(value)}`}</code>)
            )}
          </div>

          <div className="tool-sec">
            <div className="lbl">结果</div>
            {tool.result === null ? (
              <span className="empty">进行中</span>
            ) : (
              <pre className="tool-result">{resultText(tool.result)}</pre>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
