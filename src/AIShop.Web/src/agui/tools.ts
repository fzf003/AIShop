/**
 * 工具胶囊视图模型（tasks.md C7；spec R8 / design §4.2、§9.6）。
 *
 * 职责：把 AG-UI 的 `TOOL_CALL_*` / `RUN_*` 事件流映射为「每轮 → 每次工具调用」的视图模型。
 * 与 `store.ts` 的分工：消息体仍由官方 SDK 的 `defaultApplyEvents` 维护（**不手工拼装**），
 * 本模块只额外记录 SDK 不提供、而胶囊必须展示的三样东西：
 * **参数**、**客户端实测耗时**、**整轮用量**。
 *
 * 协议硬约束（spec R8 第 3 段 / design §9.6，**不得用估算绕过**）：
 * - 逐工具 token **不可得** —— token 只出现在 `RUN_FINISHED` / `RUN_ERROR` 的**可选、整轮级**字段
 *   `usage: TokenUsage[]`，`TOOL_CALL_*` 事件本身不带 token 也不带耗时；
 * - 因此胶囊里的 token 项 = **该轮汇总**（展示时须标明整轮口径），该字段缺失时**整项不渲染**
 *   （不显示 0、不显示占位符、**MUST NOT** 按字符数折算）。
 *
 * 耗时口径（design §9.6）：`TOOL_CALL_START` → `TOOL_CALL_RESULT` 的**墙钟差**，由本模块实测
 * （时钟可注入，默认 `Date.now`），与服务端是否上报无关；无 `TOOL_CALL_RESULT` 时保持「进行中」
 * （`durationMs === null`）。
 */
import type { HttpAgent } from '@ag-ui/client'

/** 单次工具调用的状态（design §9.6「是否有 RESULT 且结果非错误文案」）。 */
export type ToolStatus = 'running' | 'success' | 'failure'

/**
 * `RUN_FINISHED` / `RUN_ERROR` 携带的整轮用量条目。
 *
 * 与 `@ag-ui/core` 的 `TokenUsage` 结构一致（此处本地声明：`@ag-ui/core` 不是本工程的直接依赖，
 * 且该结构是 wire 契约的一部分，不随客户端 SDK 的类型导出位置变化）。
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

/** 整轮用量汇总；三个字段都可能为 `null`（表示各条目均未提供该字段）。 */
export interface UsageSummary {
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
}

/** 单次工具调用的视图模型（胶囊的折叠态 + 展开态都由它派生）。 */
export interface ToolCallView {
  readonly toolCallId: string
  /** 工具名（`TOOL_CALL_START.toolCallName`）。 */
  readonly name: string
  /** `TOOL_CALL_ARGS` 累积的**原文**（JSON 文本；解析失败时展开面板仍可原样展示）。 */
  readonly argsText: string
  /** `TOOL_CALL_END.toolCallArgs`（缺省时回退解析 `argsText`）；未到 END / 解析失败时为空对象。 */
  readonly args: Readonly<Record<string, unknown>>
  /** `TOOL_CALL_RESULT.content`；未返回时为 `null`。 */
  readonly result: string | null
  readonly status: ToolStatus
  /** 客户端实测耗时（ms）；无 `TOOL_CALL_RESULT` 时为 `null`（展示「进行中」）。 */
  readonly durationMs: number | null
}

/** 一轮（一次 `runAgent`）的工具胶囊集合与整轮用量。 */
export interface ToolRound {
  /** 该轮的 `RUN_STARTED.runId`；事件缺失时为 `null`。 */
  readonly runId: string | null
  readonly toolCalls: readonly ToolCallView[]
  /** 整轮用量；仅当结束事件携带有效的 `usage` 时非 `null`（否则 token 项整体隐藏）。 */
  readonly usage: UsageSummary | null
  /** 是否已收到 `RUN_FINISHED` / `RUN_ERROR`。 */
  readonly finished: boolean
}

/** 一次工具调用 + 它所属轮的整轮用量（`ToolChip` 的入参形态）。 */
export interface ToolCallEntry {
  readonly tool: ToolCallView
  readonly usage: UsageSummary | null
}

/**
 * 本模块消费的事件子集。
 *
 * 形状与 `@ag-ui/core` 的对应事件一致（`TOOL_CALL_END` 额外接受 SDK 已解析好的 `toolCallArgs`），
 * 故 `attachToolEvents` 只是把 SDK 回调原样转进来 —— 事件语义不在本模块重新发明。
 */
export type ToolStreamEvent =
  | { type: 'RUN_STARTED'; runId?: string }
  | { type: 'TOOL_CALL_START'; toolCallId: string; toolCallName: string }
  | { type: 'TOOL_CALL_ARGS'; toolCallId: string; delta: string }
  | {
      type: 'TOOL_CALL_END'
      toolCallId: string
      /** SDK 在 `onToolCallEndEvent` 已解析好的参数对象（官方订阅回调提供）。 */
      toolCallArgs?: Readonly<Record<string, unknown>>
    }
  | { type: 'TOOL_CALL_RESULT'; toolCallId: string; content: string }
  | { type: 'RUN_FINISHED'; runId?: string; usage?: readonly TokenUsage[] }
  | { type: 'RUN_ERROR'; usage?: readonly TokenUsage[] }

export interface ToolTracker {
  /** 消费一条事件；本模块不关心的事件类型（文本 / 状态 / 推理等）无需传入。 */
  record(event: ToolStreamEvent): void
  /** 全部轮次（含进行中的最后一轮），按发生顺序。 */
  getRounds(): readonly ToolRound[]
  /** 按 `toolCallId` 找工具调用及其所属轮的整轮用量；未找到返回 `null`。 */
  findToolCall(toolCallId: string): ToolCallEntry | null
}

export interface ToolTrackerOptions {
  /** 时钟（毫秒）。测试注入以得到确定的耗时；默认 `Date.now`。 */
  now?: () => number
}

/** 内部可变记录：`ToolCallView` 多一个 `endedAt`（`durationMs` 由两个时间戳相减派生）。 */
interface PendingToolCall {
  toolCallId: string
  name: string
  argsText: string
  args: Record<string, unknown>
  result: string | null
  startedAt: number
  endedAt: number | null
}

interface PendingRound {
  runId: string | null
  toolCalls: PendingToolCall[]
  usage: UsageSummary | null
  finished: boolean
}

/**
 * 服务端工具以「返回文本」表达失败时的前缀。
 *
 * - `❌`：通用工具（`get_weather_forecast` / `get_stock_quote`）的错误文案前缀，见
 *   `src/AIShop.Service/Tools/{Weather,Stock}Tool.cs`；
 * - `Error`：工具抛异常时由 FICC 回填到 `TOOL_CALL_RESULT.content` 的文本。
 */
const FAILURE_PREFIXES = ['❌', 'Error'] as const

/** 结果文本是否为错误文案（design §9.6 的状态判据之一）。 */
function isFailureResult(content: string): boolean {
  const text = content.trim()
  return FAILURE_PREFIXES.some((prefix) => text.startsWith(prefix))
}

/** 参数原文是否为可用的 JSON 对象（数组 / 标量 / 非法文本都不算）。 */
function parseArgs(text: string): Record<string, unknown> | null {
  const trimmed = text.trim()
  if (trimmed === '') return null
  try {
    const parsed: unknown = JSON.parse(trimmed)
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>
    }
  } catch {
    // 参数不是合法 JSON（流被截断 / 模型给了自由文本）→ 保留原文由展开面板展示
  }
  return null
}

/**
 * 汇总整轮用量。
 *
 * 只做**已有数字的加总**（多 provider 条目时逐项相加），字段全缺时返回 `null`
 * —— 绝不因为「数字看起来太小」去按字符数补估（spec R8 第 3 段明令禁止）。
 */
function summarizeUsage(entries: readonly TokenUsage[]): UsageSummary | null {
  const sumOf = (pick: (entry: TokenUsage) => number | undefined): number | null => {
    let total = 0
    let seen = false
    for (const entry of entries) {
      const value = pick(entry)
      if (typeof value === 'number' && Number.isFinite(value)) {
        total += value
        seen = true
      }
    }
    return seen ? total : null
  }

  const inputTokens = sumOf((entry) => entry.inputTokens)
  const outputTokens = sumOf((entry) => entry.outputTokens)
  const totalTokens =
    sumOf((entry) => entry.totalTokens) ??
    (inputTokens !== null && outputTokens !== null ? inputTokens + outputTokens : null)

  if (inputTokens === null && outputTokens === null && totalTokens === null) return null
  return { inputTokens, outputTokens, totalTokens }
}

/**
 * 创建一个工具胶囊追踪器。
 *
 * 轮次边界（design §4.2）：`RUN_STARTED` 开新轮；工具事件落在**当前轮**（没有当前轮时隐式开一轮，
 * 兼容事件流从工具事件开始的情形）；`RUN_FINISHED` / `RUN_ERROR` 结束当前轮并把 `usage` 挂到
 * **该轮整体**（因此该轮所有胶囊共享同一份整轮用量）。
 */
export function createToolTracker(options: ToolTrackerOptions = {}): ToolTracker {
  const now = options.now ?? ((): number => Date.now())
  const rounds: PendingRound[] = []

  /** 当前轮：最后一轮未结束就用它，否则新开一轮。 */
  const currentRound = (): PendingRound => {
    const last = rounds[rounds.length - 1]
    if (last !== undefined && !last.finished) return last
    const created: PendingRound = { runId: null, toolCalls: [], usage: null, finished: false }
    rounds.push(created)
    return created
  }

  const findPending = (toolCallId: string): PendingToolCall | null => {
    for (let index = rounds.length - 1; index >= 0; index -= 1) {
      const found = rounds[index].toolCalls.find((call) => call.toolCallId === toolCallId)
      if (found !== undefined) return found
    }
    return null
  }

  const toView = (call: PendingToolCall): ToolCallView => {
    const hasResult = call.result !== null
    return {
      toolCallId: call.toolCallId,
      name: call.name,
      argsText: call.argsText,
      args: call.args,
      result: call.result,
      status: !hasResult ? 'running' : isFailureResult(call.result ?? '') ? 'failure' : 'success',
      durationMs: call.endedAt === null ? null : call.endedAt - call.startedAt,
    }
  }

  return {
    record(event: ToolStreamEvent): void {
      switch (event.type) {
        case 'RUN_STARTED': {
          // 上一轮没有结束事件（异常中断）→ 就地封口：不挂用量，token 项自然隐藏。
          const last = rounds[rounds.length - 1]
          if (last !== undefined && !last.finished) last.finished = true
          rounds.push({ runId: event.runId ?? null, toolCalls: [], usage: null, finished: false })
          return
        }

        case 'TOOL_CALL_START': {
          const round = currentRound()
          round.toolCalls.push({
            toolCallId: event.toolCallId,
            name: event.toolCallName,
            argsText: '',
            args: {},
            result: null,
            startedAt: now(),
            endedAt: null,
          })
          return
        }

        case 'TOOL_CALL_ARGS': {
          const call = findPending(event.toolCallId)
          if (call === null) return
          call.argsText += event.delta
          return
        }

        case 'TOOL_CALL_END': {
          const call = findPending(event.toolCallId)
          if (call === null) return
          const parsed = event.toolCallArgs ?? parseArgs(call.argsText)
          if (parsed !== null) call.args = { ...parsed }
          return
        }

        case 'TOOL_CALL_RESULT': {
          const call = findPending(event.toolCallId)
          if (call === null) return
          call.result = event.content
          call.endedAt = now()
          return
        }

        case 'RUN_FINISHED':
        case 'RUN_ERROR': {
          const last = rounds[rounds.length - 1]
          if (last === undefined) return
          last.usage = summarizeUsage(event.usage ?? [])
          last.finished = true
          return
        }
      }
    },

    getRounds(): readonly ToolRound[] {
      return rounds.map((round) => ({
        runId: round.runId,
        toolCalls: round.toolCalls.map(toView),
        usage: round.usage,
        finished: round.finished,
      }))
    },

    findToolCall(toolCallId: string): ToolCallEntry | null {
      for (let index = rounds.length - 1; index >= 0; index -= 1) {
        const found = rounds[index].toolCalls.find((call) => call.toolCallId === toolCallId)
        if (found !== undefined) {
          return { tool: toView(found), usage: rounds[index].usage }
        }
      }
      return null
    },
  }
}

/**
 * 把追踪器接到官方客户端的订阅面上（`agent.subscribe`）。
 *
 * 官方 SDK 已把 SSE 解析与事件分派做好，这里只做「事件 → `record()`」的搬运；
 * 参数对象直接用 `onToolCallEndEvent` 给到的 `toolCallArgs`（不再自行解析 `TOOL_CALL_ARGS` 原文）。
 * 返回退订函数（`agent.subscribe` 返回的 `unsubscribe`）。
 */
export function attachToolEvents(agent: HttpAgent, tracker: ToolTracker): () => void {
  const { unsubscribe } = agent.subscribe({
    onRunStartedEvent: ({ event }) => {
      tracker.record({ type: 'RUN_STARTED', runId: event.runId })
    },
    onToolCallStartEvent: ({ event }) => {
      tracker.record({
        type: 'TOOL_CALL_START',
        toolCallId: event.toolCallId,
        toolCallName: event.toolCallName,
      })
    },
    onToolCallArgsEvent: ({ event }) => {
      tracker.record({ type: 'TOOL_CALL_ARGS', toolCallId: event.toolCallId, delta: event.delta })
    },
    onToolCallEndEvent: ({ event, toolCallArgs }) => {
      tracker.record({ type: 'TOOL_CALL_END', toolCallId: event.toolCallId, toolCallArgs })
    },
    onToolCallResultEvent: ({ event }) => {
      tracker.record({ type: 'TOOL_CALL_RESULT', toolCallId: event.toolCallId, content: event.content })
    },
    onRunFinishedEvent: ({ event }) => {
      tracker.record({ type: 'RUN_FINISHED', runId: event.runId, usage: event.usage })
    },
    onRunErrorEvent: ({ event }) => {
      tracker.record({ type: 'RUN_ERROR', usage: event.usage })
    },
  })
  return unsubscribe
}
