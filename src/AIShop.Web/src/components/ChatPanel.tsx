/**
 * 聊天面板（tasks.md C11；spec R7「对话的流式渲染与单轮运行约束」/ R17「空对话欢迎语」/ design §7）。
 *
 * 三条要点：
 * 1. **增量渲染**：消息体由官方 SDK 的 `defaultApplyEvents` 维护（每来一个 `TEXT_MESSAGE_CONTENT`
 *    就写进 `agent.messages`），本组件只把 `messages` 原样渲染 —— 因此**首个增量到达时气泡即出现**
 *    并随后续增量增长，绝不会等流结束（R7-1）。组件自身**不缓存、不拼接文本**。
 * 2. **单轮运行约束**：`isRunning` 期间发送按钮 `disabled`，本轮结束恢复（R7-2）。该约束**只覆盖
 *    「发消息给 Agent」这条通道**——购物车 REST 写入不在本组件内，故不受影响（R10 第 5 段）。
 * 3. **欢迎语是视图元素**：可见性由 `messages.length === 0` 这一**单一条件**在渲染期派生
 *    （R17），不新增状态字段、不持久化、不进入消息序列；首条用户消息入列的瞬间即消失。
 *
 * 工具胶囊：assistant 消息的 `toolCalls[]` 各渲染一个 `ToolChip`；它的耗时/整轮用量来自 C7 的
 * tracker（`findToolCall` 由 App 注入）。`role:"tool"` 的结果消息由对应胶囊承载，**不单独渲染气泡**。
 */
import type { AssistantMessage, Message, ToolMessage, UserMessage } from '@ag-ui/client'
import { useEffect, useRef, useState } from 'react'

import type { ToolCallEntry } from '../agui/tools'
import ToolChip from './ToolChip'

import './ChatPanel.css'

export interface ChatPanelProps {
  /** 当前账户的完整消息序列（来自 `agui/store.ts#useSession`，SDK 维护）。 */
  messages: readonly Message[]
  /** 是否有一轮对话正在进行（决定发送按钮禁用态与流式光标）。 */
  isRunning: boolean
  /**
   * 工具调用查询（C7 的 `ToolTracker.findToolCall`）。
   *
   * 未注入时（例如单独渲染本组件）**跳过**工具胶囊而不是抛错 —— 文本渲染不依赖它。
   */
  findToolCall?: (toolCallId: string) => ToolCallEntry | null
  /** 发送一条用户消息（App 接到 `store.runRound`）。 */
  onSend: (text: string) => void
}

/**
 * 归一消息正文为纯文本。
 *
 * AG-UI 的 `content` 是 `string | 多模态分片[]`（本应用只产生纯文本）；多模态分片只取其中的
 * 文本片段，其余（图片/音频等）不进正文。写成对 `unknown` 的结构判定，避免逐个角色联合类型展开。
 */
function textOf(content: unknown): string {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  return content
    .map((part: unknown) => {
      if (typeof part !== 'object' || part === null) return ''
      const text = (part as { text?: unknown }).text
      return typeof text === 'string' ? text : ''
    })
    .join('')
}

function isUser(message: Message): message is UserMessage {
  return message.role === 'user'
}

function isAssistant(message: Message): message is AssistantMessage {
  return message.role === 'assistant'
}

function isTool(message: Message): message is ToolMessage {
  return message.role === 'tool'
}

/**
 * 一条消息的渲染分支。
 *
 * - `user` → 右对齐深色气泡；
 * - `assistant` → 其 `toolCalls[]` 各一个胶囊，正文非空时再出左对齐白底气泡；
 * - `tool` → 不渲染（结果由胶囊的展开面板展示，避免同一结果出现两次）；
 * - 其余角色（system / developer / reasoning 等）本应用不产生，跳过。
 */
function MessageRow({
  message,
  streaming,
  findToolCall,
}: {
  message: Message
  /** 该消息是否为「正在流式增长的那一条」（仅末条 assistant 可能为真）。 */
  streaming: boolean
  findToolCall?: (toolCallId: string) => ToolCallEntry | null
}) {
  if (isTool(message)) return null

  if (isUser(message)) {
    return (
      <div className="row u">
        <div className="bub">{textOf(message.content)}</div>
      </div>
    )
  }

  if (!isAssistant(message)) return null

  const content = textOf(message.content)
  const toolCalls = message.toolCalls ?? []
  return (
    <>
      {toolCalls.map((call) => {
        const entry = findToolCall?.(call.id) ?? null
        if (entry === null) return null
        return <ToolChip key={call.id} tool={entry.tool} usage={entry.usage} />
      })}

      {content !== '' && (
        <div className="row a">
          {/* `.cur` 在气泡末尾追加一个闪烁光标（原型 v3 的 `.cur::after`）。 */}
          <div className={streaming ? 'bub cur' : 'bub'}>{content}</div>
        </div>
      )}
    </>
  )
}

export default function ChatPanel({ messages, isRunning, findToolCall, onSend }: ChatPanelProps) {
  const [draft, setDraft] = useState('')
  const listRef = useRef<HTMLDivElement>(null)

  // 新消息到达即滚到底部（纯体验，不影响数据与持久化）。
  useEffect(() => {
    const list = listRef.current
    if (list !== null) list.scrollTop = list.scrollHeight
  }, [messages])

  const submit = (): void => {
    const text = draft.trim()
    // 空输入与运行中都不发送：运行中禁用是 spec R7 的硬约束（并发运行会导致消息乱序）。
    if (text === '' || isRunning) return
    onSend(text)
    setDraft('')
  }

  return (
    <div className="chat">
      <div className="msgs" ref={listRef}>
        {/* 欢迎语：`messages.length === 0` 单一条件派生（spec R17），位于消息区顶部 */}
        {messages.length === 0 && <div className="welcome">👋 开始聊天，告诉我您的喜好</div>}

        {messages.map((message, index) => (
          <MessageRow
            key={message.id}
            message={message}
            streaming={isRunning && index === messages.length - 1}
            findToolCall={findToolCall}
          />
        ))}
      </div>

      <div className="inp-area">
        <div className="inp-wrap">
          <input
            value={draft}
            aria-label="消息"
            placeholder="输入消息…"
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault()
                submit()
              }
            }}
          />
          <button type="button" className="send" onClick={submit} disabled={isRunning}>
            发送
          </button>
        </div>
      </div>
    </div>
  )
}
