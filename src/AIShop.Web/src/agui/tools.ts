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
  /** `TOOL_CALL_RESULT.content`（宿主多编码层已在 wire 边界剥掉，见 `decodeToolResultContent`）；未返回时为 `null`。 */
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
  | {
      type: 'TOOL_CALL_RESULT'
      toolCallId: string
      /** 工具结果文本；`attachToolEvents` 传入前已剥掉宿主多编码层（`decodeToolResultContent`），故此处是**解码后**的值。 */
      content: string
    }
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
  /**
   * **恢复的轮次**（已完成轮的纯数据视图），来自 `agui.tools.{username}` 持久化
   * （收口裁决 D3 / design §15.4 / spec R8 追加条款）。
   *
   * 为什么单独放一个数组、而不是塞进内部 `rounds`：恢复轮**只有 `durationMs`**，
   * 没有原始 `startedAt` / `endedAt`（耗时是上一会话客户端掐表的差值，服务端与 wire 都不带时间戳）。
   * 塞进去就必须伪造 `startedAt` / `endedAt` 这类假值才能让 `toView` 算出同一个数 —— 那是拿假数据
   * 换真数据。分开存放则 `durationMs` **原样保留**，且对已完成轮而言两者语义等价。
   *
   * ⚠️ 恢复轮里的 `result` 已是**解码后**的值（wire 边界的 `decodeToolResultContent` 在写入持久化
   * **之前**就跑过了）：恢复路径不再经过事件流，故 **MUST NOT** 再解码一次（`design §15.1` 的
   * 「只在边界恰好应用一次」）。
   */
  initialRounds?: readonly ToolRound[]
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

/**
 * 剥掉宿主对工具结果字符串的**多编码层**（收口裁决 D1，design §15.1 / spec R9 追加条款）。
 *
 * 真机 wire 形状：宿主把工具本次的返回字符串又 `JsonSerializer.Serialize` 了一次，于是
 * `TOOL_CALL_RESULT.content === JSON.stringify(result)`。对 JSON 文本结果（`recommend_products`
 * 等）表现为「`JSON.parse(content)` 得到的是 string 而非对象」。本函数即那次序列化的**逆**。
 *
 * 语义（精确，恰好一层）：
 * - `raw` 是一个 JSON 文本且 `JSON.parse(raw)` 得到 **string** → 返回该 string；
 * - 其余情况（不含外层引号的普通文本、JSON 对象/数组、非法 JSON、空串）→ **原样返回**，不抛异常。
 *
 * ⚠️ **只应用一次，勿链式调用**（本函数 NOT 幂等）：重复应用必然多剥一层——工具真的返回带引号
 * 文本 `"hi"`（引号是工具原意）时宿主发 `"\"hi\""`，解一次得 `"hi"`（正确），再解一次得 `hi`
 * （把工具本意的引号吃掉，错）。故解码**只发生在 wire 边界**（宿主值进入应用处）恰好一次：
 * `attachToolEvents` 与 `App.tsx` 的 `lastRecommendationContent` 各调用一次；
 * MUST NOT 在可能已是解码后的值上再调，MUST NOT 改写成「循环解析直到不是 JSON 字符串」。
 */
export function decodeToolResultContent(raw: string): string {
  // 只有「JSON 字符串字面量」才可能带这个编码层；先做廉价前缀判断再去解析。
  if (!raw.trimStart().startsWith('"')) return raw
  try {
    const parsed: unknown = JSON.parse(raw)
    return typeof parsed === 'string' ? parsed : raw
  } catch {
    // 非法 JSON → 不是宿主编码层（宿主产出的必是合法 JSON）→ 原样返回，不抛。
    return raw
  }
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
 *
 * 轮次来源有两批（`getRounds()` 的顺序 = **恢复轮在前、实时轮在后**）：
 * 构造期传入的 `initialRounds`（刷新前已落库的历史轮）与本次会话经 `record()` 累积的轮次。
 * 实时事件**不触碰**恢复轮（它们都是已完成轮，`RUN_STARTED` 的「就地封口」只作用于实时轮）。
 */
export function createToolTracker(options: ToolTrackerOptions = {}): ToolTracker {
  const now = options.now ?? ((): number => Date.now())
  /** 构造期恢复的轮次（已完成的纯数据视图，原样透出，见 `ToolTrackerOptions.initialRounds`）。 */
  const restored: readonly ToolRound[] = options.initialRounds ?? []
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

  const toRoundView = (round: PendingRound): ToolRound => ({
    runId: round.runId,
    toolCalls: round.toolCalls.map(toView),
    usage: round.usage,
    finished: round.finished,
  })

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
      // 恢复轮在前、实时轮在后：顺序即「历史 → 现在」，再次持久化时两批都在（`JSON.stringify` 直接可用）。
      return [...restored, ...rounds.map(toRoundView)]
    },

    findToolCall(toolCallId: string): ToolCallEntry | null {
      // 先查实时轮（更新、通常也是被问的那个），再查恢复轮 —— 两处都要查，否则刷新后历史轮次查不到
      // （D3 的缺陷本体：`ChatPanel` 的 `findToolCall?.(call.id) ?? null` 落空即整块不渲染）。
      for (let index = rounds.length - 1; index >= 0; index -= 1) {
        const found = rounds[index].toolCalls.find((call) => call.toolCallId === toolCallId)
        if (found !== undefined) {
          return { tool: toView(found), usage: rounds[index].usage }
        }
      }
      for (let index = restored.length - 1; index >= 0; index -= 1) {
        const round = restored[index]
        const found = round.toolCalls.find((call) => call.toolCallId === toolCallId)
        if (found !== undefined) {
          // 恢复轮里的 `tool` 已是视图对象（`result` 已解码），直接透出、**不再解码**。
          return { tool: found, usage: round.usage }
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
 * 参数对象直接用 `onToolCallEndEvent` 给到的 `toolCallArgs`（不再自行解析 `TOOL_CALL_ARGS` 原文）；
 * 工具结果在**唯一**的 wire 边界处剥一次宿主多编码层（`decodeToolResultContent`，design §15.1）。
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
      tracker.record({
        type: 'TOOL_CALL_RESULT',
        toolCallId: event.toolCallId,
        // 解码**只在此处（wire 边界）发生一次**：工具调用栏展示的结果、失败判据
        // （`isFailureResult`）与 `ToolChip` 的 JSON 美化分支都消费这个解码后的值。
        content: decodeToolResultContent(event.content),
      })
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
