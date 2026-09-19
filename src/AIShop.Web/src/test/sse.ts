/**
 * AG-UI 事件流测试基建（本变更所有用例复用）。
 *
 * 职责：把「AG-UI 事件数组」编成 `text/event-stream` 的 `Response`，供 `fetch` 替身回给
 * **真实的 `@ag-ui/client`**（官方 SDK）解析 —— 事件应用逻辑不在这里重写，测试里跑的就是
 * 生产同一条 SDK 代码路径（spec R18「经官方客户端接入」）。
 *
 * 编码必须与 `@ag-ui/encoder` 的 `encodeSSE` 逐字节一致：每帧 `data: ${JSON}\n\n`。
 */

/** 事件最小形状：`type` 是 `EventType` 字面量，其余字段随事件类型（本文件只做结构宽松的构造）。 */
export type SseEvent = { type: string } & Record<string, unknown>

/**
 * `RUN_FINISHED` / `RUN_ERROR` 携带的整轮用量（协议里是**数组**，整轮级、可选）。
 * 逐工具 token 在协议中不存在 —— 见 design §9.6。
 */
export interface TokenUsage {
  provider?: string
  model?: string
  inputTokens?: number
  outputTokens?: number
  totalTokens?: number
  reasoningTokens?: number
  cachedInputTokens?: number
}

export const SSE_CONTENT_TYPE = 'text/event-stream'

const textEncoder = new TextEncoder()

/** 单帧编码，与 `@ag-ui/encoder#encodeSSE` 同形。 */
export function encodeSse(event: SseEvent): string {
  return `data: ${JSON.stringify(event)}\n\n`
}

/** 把事件数组拼成完整响应体。 */
export function encodeSseBody(events: readonly SseEvent[]): string {
  return events.map(encodeSse).join('')
}

/** 事件数组 → `Response`（body 为 `ReadableStream<Uint8Array>`，一次入队后关闭）。 */
export function createSseResponse(events: readonly SseEvent[], status = 200): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(textEncoder.encode(encodeSseBody(events)))
      controller.close()
    },
  })

  return new Response(body, {
    status,
    headers: { 'Content-Type': SSE_CONTENT_TYPE },
  })
}

// ---------------------------------------------------------------------------
// 事件构造器（字段名以 @ag-ui/core 的事件 schema 为准）
// ---------------------------------------------------------------------------

export function runStarted(threadId: string, runId: string): SseEvent {
  return { type: 'RUN_STARTED', threadId, runId }
}

export function textMessageStart(messageId: string, role: 'assistant' | 'user' = 'assistant'): SseEvent {
  return { type: 'TEXT_MESSAGE_START', messageId, role }
}

export function textMessageContent(messageId: string, delta: string): SseEvent {
  return { type: 'TEXT_MESSAGE_CONTENT', messageId, delta }
}

export function textMessageEnd(messageId: string): SseEvent {
  return { type: 'TEXT_MESSAGE_END', messageId }
}

export function toolCallStart(toolCallId: string, toolCallName: string, parentMessageId?: string): SseEvent {
  return { type: 'TOOL_CALL_START', toolCallId, toolCallName, parentMessageId }
}

export function toolCallArgs(toolCallId: string, delta: string): SseEvent {
  return { type: 'TOOL_CALL_ARGS', toolCallId, delta }
}

export function toolCallEnd(toolCallId: string): SseEvent {
  return { type: 'TOOL_CALL_END', toolCallId }
}

export function toolCallResult(toolCallId: string, messageId: string, content: string): SseEvent {
  return { type: 'TOOL_CALL_RESULT', toolCallId, messageId, content, role: 'tool' }
}

/**
 * **真机形态**的 `TOOL_CALL_RESULT`：宿主把工具本次的返回字符串又序列化了一次，
 * 故 `content === JSON.stringify(result)`（多编码一层，收口裁决 D1 / design §15.1）。
 *
 * 入参是**工具真正的返回文本**（如 `recommend_products` 的单行 JSON、`search_product` 的纯文本），
 * 本 helper 负责编成宿主实际发来的那个值。单测喂它才能复现真机——`toolCallResult(...)` 的
 * 「原样写 content」是理想形态，这正是 D1 此前漏测的原因。
 */
export function toolCallResultEncoded(
  toolCallId: string,
  messageId: string,
  result: string,
): SseEvent {
  return toolCallResult(toolCallId, messageId, JSON.stringify(result))
}

/**
 * `CUSTOM` 事件（推荐推送的载体；spec R6 / R7，宿主侧 `name` 恒为 `"recommendation"`）。
 *
 * `value` 形状由 `@ag-ui/core` 的 `CustomEventSchema` 声明为 `z.any()` —— 协议不校验内部结构，
 * 服务端推的是推荐结果**对象**（与持久化 `agui.reco.{username}` 同形）。
 *
 * SDK 的 `CUSTOM` 分支**只派发 `onCustomEvent`**、不产生消息（R6-1；实测见 `agent.test.ts`）。
 */
export function customEvent(name: string, value: unknown): SseEvent {
  return { type: 'CUSTOM', name, value }
}

export function runFinished(
  threadId: string,
  runId: string,
  usage?: readonly TokenUsage[],
): SseEvent {
  return { type: 'RUN_FINISHED', threadId, runId, usage }
}

export function runError(message: string, code?: string, usage?: readonly TokenUsage[]): SseEvent {
  return { type: 'RUN_ERROR', message, code, usage }
}
