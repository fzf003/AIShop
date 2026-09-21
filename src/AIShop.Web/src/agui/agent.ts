/**
 * AG-UI 会话装配（tasks.md C4 + C5；spec R1 / R3 / R4 / R12 / R18-2）。
 *
 * 定位：**协议层**。与 AG-UI 的对接全部交给官方客户端 `@ag-ui/client` 的 `HttpAgent`
 * （R18-2：MUST NOT 自造「手写 SSE 逐行解析 + 自行维护 messages」），本文件只做五件事：
 *
 * 1. **装配** `new HttpAgent({ url: '/agui', threadId, initialMessages })`；
 * 2. **每轮显式传 `{ username, model }`**（硬契约 3 / R3）：`forwardedProps` **不会自动携带**，
 *    漏传一轮服务端就回落缺省身份（`steve`）与缺省模型（`ActiveModel`）；
 * 3. **订阅状态变化**（消息变更 / 运行失败 / 运行结束）并通知上层（store 侧据此持久化 + 渲染）；
 * 4. **运行失败分派**（硬契约 2 / R4，C5）：失败经 `onRunFailed` 到达 → 复用 `api/errors.ts` 的
 *    `dispatchApiFailure`（404 清会话回账户选择页 / 5xx 只提示不破坏历史），见 `handleRunFailure`；
 * 5. **推理消息的排除**（C14 / spec R1 追加条款「推理（reasoning）消息的排除」）：入站事件过滤
 *    （`dropReasoningEvents`）+ 恢复种子过滤（`filterReasoningMessages`），见下文两块注释。
 *
 * 消息数组由 SDK 的 `defaultApplyEvents` 自动维护，客户端**只订阅、不手工拼装**。
 * `agent.messages` 即「客户端持有的完整历史」——服务端 `ProvideChatHistoryAsync` 恒返回空，
 * 每轮 `RunAgentInput.messages` 由 SDK 取 `agent.messages` 的**全量快照**（硬契约 1 / R1）。
 *
 * 切换账户 = 重建本对象（`store.ts` 的 `startSession`）；`threadId` 随之重置，
 * 但客户端**不依赖 `threadId` 做隔离**：服务端按用户名归属会话（R12）。
 */
import { HttpAgent, type Message, type MiddlewareFunction, type RunAgentResult } from '@ag-ui/client'
import { filter, tap } from 'rxjs'

import { dispatchApiFailure } from '../api/errors'
import { setRecoFromCustomEvent, setRecoFromToolResult } from './reco'
import { decodeToolResultContent } from './tools'

/**
 * 顶部提示（Toast）出口。
 *
 * 用「注入」而非直接依赖 UI 组件：真实实现由 App 层（C11 装配）经 `setToastHandler` 注入；
 * 默认实现只写控制台 —— 目的是「失败不静默」，而不是替代 UI。
 *
 * 注：**这不等于本文件整体 React-free** —— 本文件经 `./reco` 间接依赖 React（推荐 store 走直连，
 * 见 `design.md` §4.4 B「协议层只做事件 → store 的搬运」）。此处用注入是为「默认不静默」这一语义，
 * 并非对协议层作依赖承诺。
 */
export type ToastHandler = (message: string) => void

const defaultToastHandler: ToastHandler = (message) => {
  console.warn(`[AIShop] ${message}`)
}

let toastHandler: ToastHandler = defaultToastHandler

/** 注入 / 复位（传 `null` 复位为默认）顶部提示出口。 */
export function setToastHandler(handler: ToastHandler | null): void {
  toastHandler = handler ?? defaultToastHandler
}

/**
 * AG-UI 端点路径。
 *
 * AguiHost 把 AG-UI 端挂在**根路径 `/`**，而 Vite dev server 自己占用 `/`（页面 + HMR），
 * 因此 `vite.config.ts` 用专用前缀 `/agui` + `rewrite` 去前缀转发（R14：禁裸 `/` 作代理前缀）。
 * 生产同域反代按同一规则配置。
 */
export const AGUI_ENDPOINT = '/agui'

/**
 * C2：挂起兜底 —— **首字节超时**：本轮发出后多久仍收不到**第一个事件**，即判定为挂起并中止。
 *
 * 取值依据（2026-09-20 真机实测）：一轮正常对话服务端约 28 秒，但其中 **19.5 秒是记忆提取**，
 * 那段后处理期间服务端**一个事件都不发**。所以「静默」绝不能当作挂起信号 —— 后处理慢是常态。
 * 真正的挂起是**连响应都起不来**（连接建立后毫无动静），故只对「首字节」计时。
 *
 * **已知取舍**：本方案不覆盖「服务端发了几帧之后才卡死」——那种情况要中段静默检测，
 * 而中段静默与后处理静默在当前链路上无法区分，强行区分会重新引入误杀。宁可少兜一种，
 * 不要误杀正常请求（误杀的表现与真实故障一模一样，只会更难排查）。
 *
 * 导出仅供测试引用（避免用例硬编码一个会与实现漂移的阈值）。
 */
export const FIRST_BYTE_TIMEOUT_MS = 60_000

export interface AguiSessionConfig {
  /** 当前选定账户；决定服务端会话归属（`store_id = AGUIShopping:{username}`）。 */
  username: string
  /**
   * 当前选中模型的 **`id`**（`GET /models` 响应项的配置节键），
   * **不是** `model` 字段（真实 wire 模型名）—— 硬契约 3 / R3「model 传的是 id 而非模型名」。
   */
  model: string
  /** AG-UI 协议必填字段；客户端不依赖它做隔离或连续性（R12）。 */
  threadId: string
  /** 启动时恢复的完整历史（来自 `agui.messages.{username}`）；首次进入为空。 */
  initialMessages?: readonly Message[]
}

/** 会话句柄：一个账户 = 一个实例；切换账户时重建（design §8.1）。 */
export interface AguiSession {
  /** 官方 AG-UI 客户端实例（测试与上层只读地使用它）。 */
  readonly agent: HttpAgent
  readonly username: string
  readonly model: string
  /** 切换模型：**下一轮**生效（在途本轮的 `forwardedProps` 已在 `runAgent` 时快照）。 */
  setModel(model: string): void
  /** 当前完整消息序列（`agent.messages`，SDK 维护）。 */
  getMessages(): readonly Message[]
  isRunning(): boolean
  /** 订阅「消息变更 / 运行失败 / 运行结束」；返回退订函数。 */
  subscribe(listener: () => void): () => void
  /** 发一轮：追加一条用户消息（唯一 id）后 `runAgent({ forwardedProps })`。 */
  runRound(text: string): Promise<RunAgentResult>
  /**
   * 销毁会话（退出登录 / 切账户，L5）：**中止在途轮**、**断开订阅**，并**立起 `disposed` 守卫**。
   *
   * 为什么必须显式销毁（而不是丢引用等 GC）：订阅闭包持有的是**模块级全局**的推荐 store
   * 写入点（`setRecoFromCustomEvent`）。旧账户在途轮晚到的 `CUSTOM` 会写进那个全局 store ——
   * 新账户的面板被上一账户的负载顶掉，随后还会被写进新账户的 `agui.reco.{username}`
   * （违反 spec「退出只清当前账户」）。
   *
   * 三步各挡一段，缺一不可：
   * - `abortRun()` 中止在途轮：SSE 连接断开，旧轮的事件从此不可能送达（L5-a 锁定）；
   * - `disposed = true` 拦住**本轮残留的派发**：SDK 在 `runAgent` 开头把订阅者列表拍成**快照**，
   *   本轮余下的回调 —— **含中止自身引发的 `onRunFailed`** —— 走的就是那份快照，
   *   **`unsubscribe()` 拦不住**。真机实测（2026-09-21）：不设此守卫时，在途轮中点退出会在账户页
   *   弹出「网络异常，请稍后重试」（L5-c 锁定）；
   * - `unsubscribe()` 断开**后续派发**：`setMessages` / `addMessage` 这类 API 遍历的是
   *   `this.subscribers` **活引用**（L5-b 锁定）。
   *
   * 顺序：**先置 `disposed`**，再退订、再中止 —— 标记必须早于 `abortRun()`（它引发的派发是
   * 异步的，标记先立起来才不会漏）。
   *
   * 幂等：重复调用安全（`unsubscribe` 内部是数组过滤；`AbortController.abort()` 本身幂等）。
   * `abortController` 由 SDK 在**每轮** `runAgent` 时重建，故中止不会影响任何后续轮次。
   */
  dispose(): void
}

/**
 * 装配一个 AG-UI 会话。
 *
 * 订阅钩子说明（为什么五个都挂）：
 * - `onMessagesChanged`：流式增量与工具结果消息都会触发 → 上层据此持久化「完整」消息（R2-3 / 硬契约 1）；
 * - `onNewMessage`：本地 `addMessage`（用户消息）**不触发** `onMessagesChanged`，须单独接；
 * - `onRunFinalized`：刷新 `isRunning`（运行态退出）；
 * - `onRunFailed`：**失败处理的唯一入口**（SDK 把非 2xx 转成此回调，见下）；
 * - `onCustomEvent`：推荐推送（spec R6 / R7）→ 把事件的 `value` 搬进推荐 store（本层只搬运不解析，见下）。
 *
 * ⚠️ **硬契约 2**：失败**不**以 `RUN_ERROR` 事件帧到达（服务端 username 校验短路在 `MapAGUIServer`
 * 之前，返回的是**普通 HTTP 响应** `404 + {"detail":"User not found"}`）；`@ag-ui/client` 的
 * `runHttpRequest` 读完整错误体后构造挂了 `status` / `payload` 的普通 `Error`，经 `AbstractAgent` 的
 * `catchError` → `onError` **派发到订阅者的 `onRunFailed`**。
 *
 * **实测（`@ag-ui/client` 0.0.59，本仓安装版本）**：`onRunFailed` 被派发的**同时**，`runAgent()` 的
 * Promise 也会以**同一个**异常 reject —— `AbstractAgent.onError` 在派发之后重新抛出，除非订阅者的
 * 返回值带 `stopPropagation: true`（而该字段按类型声明**不属于** `onRunFailed` 的返回值，SDK 不把它
 * 当作该回调的约定）。因此：
 * - 失败处理**必须**收在此处的 `onRunFailed` 内（spec R4：「通过运行失败回调（**而非仅等待 Promise
 *   返回**）捕获该失败」）——**不得**改成 `try { await agent.runAgent() } catch { ... }` 作为替代；
 * - `runAgent()` 的 rejection 由**调用方**兜底（见 handoff-C5：C11 的发送处需要吞掉它），本层不吞，
 *   以免把「失败已被处理」与「调用方还要不要处理」两件事混在一起。
 */

/**
 * 运行失败处理（硬契约 2 / spec R4）。
 *
 * AG-UI 面与 REST 面（`/products`、`/cart*`）的 404 是**同一份服务端实现产生的同一字节契约**，
 * 故复用 `errors.ts` 的 `dispatchApiFailure` 这一条分派：404 `User not found` → 清当前账户的本地
 * 会话 + 通知订阅者（App 据此回账户选择页）+ 提示「用户不存在，请重新选择账户」；5xx / 网络失败
 * → 只提示，**不改动**任何本地状态（既有历史完整保留）；**不**把它渲染成助手回复、**不**停在运行中。
 *
 * 顺序：**先 `notify()`，再分派**。`notify()` 会经 store 把当前消息整体写回
 * `agui.messages.{username}`（C4 的持久化约定）；若先分派（`clearSession` 已删键）再 notify，
 * 刚清掉的键会被这次回写**重新创建**——表现为「404 之后历史还在」。先回写、后清理，最终态才是
 * 「会话已清」。
 *
 * 调用点不 `await`：`dispatchApiFailure` 的 404 `User not found` 分支内**同步**完成清会话 / 通知 /
 * 提示，失败处理在此处**已经完结**，不需要等待。
 */
function handleRunFailure(error: unknown): void {
  void dispatchApiFailure(error, { toast: toastHandler })
}

/** 推理消息的角色名（AG-UI 协议里的 `ReasoningMessage.role`）。 */
const REASONING_ROLE = 'reasoning'

/**
 * 推理事件的两类前缀：新协议的 `REASONING_*` 与**旧协议的 `THINKING_*`**（后者是前者的前身）。
 *
 * `THINKING_*` 必须一并丢弃，理由见 `dropReasoningEvents`。
 */
const REASONING_EVENT_PREFIXES = ['REASONING_', 'THINKING_']

/** 该事件是否属于推理（reasoning）事件。抽成纯函数：前缀判定只有一处、可读可测。 */
function isReasoningEvent(type: string): boolean {
  return REASONING_EVENT_PREFIXES.some((prefix) => type.startsWith(prefix))
}

/**
 * 入站事件过滤（C14 落点 1 / spec R1 追加条款「入站事件过滤」）。
 *
 * **不变量（design §15.2 D2 裁决）：`reasoning` 消息不得存在于 `agent.messages`，无论来源。**
 * 官方 SDK 的 `defaultApplyEvents` 会把 `REASONING_*` 落成 `role:"reasoning"` 的消息塞进
 * `agent.messages`；而客户端每轮把该序列**整体**重发（R1 硬契约），.NET AG-UI 宿主的
 * `MapChatRole` 不识别 `reasoning` → 抛 `InvalidOperationException: Unknown chat role: reasoning`
 * → **HTTP 500**（现象：第 1 轮正常、第 2 轮点发送后无任何回复）。服务端零改动是硬约束，
 * 所以在**产生侧**把推理消息掐掉。
 *
 * **为什么丢事件、而不是事后过滤 `agent.messages` 数组**：官方 SDK 的中间件链是
 * `middlewares.reduceRight(...).run(input)` → `pipe(transformChunks, verifyEvents, …)` →
 * `apply(defaultApplyEvents)` → `processApplyEvents` —— 中间件**包住传输层、跑在 `applyEvents`
 * 之前**，在这里丢事件等于「推理消息根本不产生」，是全链唯一真源。若改成事后过滤数组
 * （出站 / 持久化双边界过滤）则是治症状：`agent.messages` 里仍留着推理消息，此后每一个新读点
 * 都得记得再过滤一次；且 `setMessages` 在流式期间替换数组有索引失效风险。此处用官方公开 API
 * `agent.use(...)`，**不继承 / 不覆写 `HttpAgent`**（R18-2）。
 *
 * **为什么 `THINKING_*` 也要丢**：`use()` 把中间件**追加**到链尾（最内层、紧贴传输层），而 SDK
 * 自带的前向兼容中间件（`BackwardCompatibility_0_0_45`）是 `unshift` 到链首（最外层），它会把旧协议
 * 的 `THINKING_*` 映射成 `REASONING_*`。中间件是「外层的输出喂给内层的输入」的反向包裹，故本过滤器
 * 看到的是**未经映射的原始事件**——只丢 `REASONING_*` 的话，`THINKING_*` 会被我们放过、再被外层映射成
 * `REASONING_*` 而照样产生消息。两个前缀一起丢才算把口子堵死。
 * （实测：本仓安装的 0.0.59 因版本门控未挂载该兼容中间件——`new HttpAgent(...)` 后
 * `middlewares.length === 0`——所以这条是**防御性**的：对发旧协议事件的宿主、以及任何会挂载该
 * 兼容中间件的 SDK 版本都必需，成本为零。丢弃推理事件本身就是 spec「非目标：不消费推理事件」的
 * 字面落地，不是范围扩张。）
 *
 * 本应用**未开 debug**（`new HttpAgent({url, threadId, initialMessages})`，无 `debug` 参数）。
 *
 * ⚠️ **更正（agui-reco-realtime F3 实测，2026-09-19）**：**`verifyEvents` 并不被 debug 门控** ——
 * 它**无条件**挂在组合链上（实读 SDK 0.0.59 `index.mjs`：`RUN_FINISHED` 分支内直接做配平检查，
 * 外层没有任何 debug 条件包裹），对「`RUN_FINISHED` 时仍有未闭合的 text message / tool call / subagent」
 * 这类非法流**会抛错**。此前本注释写的「默认无效」是**错的**（当时据 `.d.ts` 注释推断，未实测）。
 * 本过滤器只丢 `REASONING_*` / `THINKING_*`，**不触碰文本与工具调用的配平**，故不受影响；
 * 但**不要再把这层当「无效」** —— 今后改事件流时必须顾及 `verifyEvents` 的配平约束。
 */
export const dropReasoningEvents: MiddlewareFunction = (input, next) =>
  next.run(input).pipe(filter((event) => !isReasoningEvent(event.type)))

/**
 * 恢复种子过滤（C14 落点 2 / spec R1 追加条款「恢复种子过滤」）。
 *
 * **只修入站事件不够**：升级前已经写进 `localStorage` 的 `agui.messages.{username}` 里就躺着
 * `role:"reasoning"` 消息，启动时作为 `initialMessages` 恢复回来、照样被全量重发 → 老用户升级后
 * 首次发轮仍然 500。故 seed 入口必须再挡一道。
 *
 * 保持既有语义：`undefined` 进 `undefined` 出（`HttpAgentConfig.initialMessages` 可选）；
 * 传了数组则**返回过滤后的新数组**（不改调用方的数组）。
 */
function filterReasoningMessages(messages: readonly Message[]): Message[] {
  return messages.filter((message) => message.role !== REASONING_ROLE)
}

/**
 * 「最近一次 `recommend_products` 的工具结果原文」（spec R9 第 1 段的数据来源）。
 *
 * **L6 起住在协议层**（原先在 `App.tsx`）：该来源必须与 `CUSTOM` **同一时序**写入推荐 store ——
 * 否则「模型先调工具、宿主随后推 CUSTOM」这一真机次序下，更早的工具结果会顶掉更晚的 CUSTOM
 * （论证见 `createAgent` 里 `onMessagesChanged` 的注释）。`App.tsx` 仍用它做**会话初值**注入。
 *
 * 口径（handoff-C8 遗留 1）：**必须是该工具自己的结果**，不能把别的工具结果（`get_cart_summary`
 * 的纯文本等）喂给推荐面板；取**最新一条**（消息序最后），且**不在轮次之间清空** —— 清空会让
 * 「解析失败保留上一次内容」（R9-3）退化成闪回占位。
 *
 * 宿主把工具结果字符串多编码了一层（`content === JSON.stringify(result)`，design §15.1 / D1）：
 * 在**读取侧**恰好剥一次（`decodeToolResultContent`）。这是读取边界，**不改写** `messages`
 * —— 持久化（R2）里仍是宿主原样送达的 `content`。只调用一次，勿链式。
 */
export function lastRecommendationContent(messages: readonly Message[]): string | null {
  const callIds = new Set<string>()
  for (const message of messages) {
    if (message.role !== 'assistant') continue
    for (const call of message.toolCalls ?? []) {
      if (call.function.name === 'recommend_products') callIds.add(call.id)
    }
  }

  let content: string | null = null
  for (const message of messages) {
    if (message.role === 'tool' && callIds.has(message.toolCallId)) {
      content = decodeToolResultContent(message.content)
    }
  }
  return content
}

export function createAgent(config: AguiSessionConfig): AguiSession {
  const agent = new HttpAgent({
    url: AGUI_ENDPOINT,
    threadId: config.threadId,
    // 恢复种子过滤（C14 落点 2）：挡住旧 `localStorage` 里已存在的 `role:"reasoning"` 遗留数据。
    initialMessages:
      config.initialMessages === undefined ? undefined : filterReasoningMessages(config.initialMessages),
  })

  // 入站事件过滤（C14 落点 1）：`use()` 追加的中间件紧贴传输层、跑在 `defaultApplyEvents` 之前，
  // 故推理消息**根本不产生**（理由与「为什么 THINKING_* 也要丢」见 `dropReasoningEvents`）。
  agent.use(dropReasoningEvents)

  let model = config.model
  const listeners = new Set<() => void>()

  /**
   * C2：挂起兜底 —— **首字节**计时器的句柄（`undefined` = 当前不在计时）。
   *
   * **要解的形态**：服务端建立 SSE 连接后**既不产出事件、也不结束、也不报错**。此时
   * `@ag-ui/client` 的 `fromFetch` 会一直挂着 → `agent.isRunning` 恒为 `true` → `ChatPanel`
   * 的发送按钮（`disabled={isRunning}`）永久禁用。SDK 在「正常收尾」与「报错」两条出口都会
   * 复位运行态，**唯一不能自愈的就是挂起**；浏览器 `fetch` 也没有默认超时。
   *
   * 故这里自建一层兜底：只对**首字节**计时（见 {@link FIRST_BYTE_TIMEOUT_MS}），
   * 一旦收到任意事件就撤表 —— 那已证明服务端活着，此后无论多慢都不再干预。
   * 超时则 `abortRun()` 中止本轮，让 `runAgent` 以错误收尾、`isRunning` 得以复位。
   */
  let watchdog: ReturnType<typeof setTimeout> | undefined

  /** 装表：本轮发出后起算。已在计时则不动（首字节只等一次）。 */
  const armFirstByteWatchdog = (): void => {
    if (watchdog !== undefined) return
    watchdog = setTimeout(() => {
      watchdog = undefined
      agent.abortRun()
    }, FIRST_BYTE_TIMEOUT_MS)
  }

  /** 撤表：已收到事件（服务端活着），或本轮已收尾 —— 两种情况都不再需要兜底。 */
  const disarmFirstByteWatchdog = (): void => {
    if (watchdog !== undefined) {
      clearTimeout(watchdog)
      watchdog = undefined
    }
  }

  // 首字节的观测点：`use()` 追加的中间件紧贴传输层，**每个**入站事件都会经过它
  // （比挂在 `onMessagesChanged` 上准 —— 后者只在消息内容变化时触发，会漏掉
  // `RUN_STARTED`、`TEXT_MESSAGE_START` 这类不产生消息的事件，造成误判挂起）。
  agent.use((input, next) => next.run(input).pipe(tap(() => disarmFirstByteWatchdog())))
  /**
   * 本轮是否已失败（`onRunFailed` 派发过）。
   *
   * 用途：**压掉失败轮的收尾通知**。SDK 在派发 `onRunFailed` 之后还会走 `finalize` → `onRunFinalized`，
   * 而 store 的订阅者（C4）在收到通知时会把当前消息**整体写回** `agui.messages.{username}` ——
   * 若这里照常放行，刚被 404 分支 `clearSession` 清掉的键会被这次收尾回写**重新创建**，
   * 表现为「404 之后历史还在」。失败轮的收尾通知没有新增信息（`isRunning` 已在 `onRunFailed`
   * 里随首次 `notify()` 刷成 `false`），因此直接跳过；标记在下一轮 `runRound` 开头复位。
   */
  let failed = false

  /**
   * 会话是否已销毁（L5）。
   *
   * **为什么必须有它**：`dispose()` 里的 `abortRun()` 中止在途轮之后，SDK **仍会**派发本轮的
   * `onRunFailed` —— 真机实测（2026-09-21）：在途轮中点「退出」，账户选择页会弹出
   * 「网络异常，请稍后重试」，把用户主动的操作报成故障。
   *
   * 而 `unsubscribe()` **拦不住它**：本轮余下的派发走的是 `runAgent` 开头拍下的**订阅者快照**，
   * 退订只影响后续（`setMessages` / `addMessage` 这类活引用路径）。故每个回调都要自查本标记 ——
   * 销毁之后，本轮残留的一切派发都不得再向外传播（既不提示失败，也不写推荐 store、不通知渲染）。
   */
  let disposed = false

  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发。
  const notify = (): void => {
    for (const listener of [...listeners]) listener()
  }

  // L5：**接住订阅句柄** —— `dispose()` 靠它断开。返回值原先被丢弃，正是跨账户污染的根因：
  // 订阅闭包里有写**模块级全局**推荐 store 的 `setRecoFromCustomEvent`，断了会话也不会停。
  /**
   * 「已写进推荐 store 的工具结果原文」（L6）：避免每次消息变化都重复写同一个值
   * （`messages` 在流式期间频繁变化，而工具结果整轮通常只出现一次）。
   *
   * 初值 = 恢复种子里的最后一条工具结果 —— 与 `App.tsx` 注入推荐 store 的初值同源，
   * 这样「恢复期不把历史旧值当成新变化」，`agui.reco.{username}` 的恢复优先级不会被顶掉。
   */
  let lastToolReco = lastRecommendationContent(agent.messages)

  /**
   * 工具结果来源（L6）：**同步**派生并写入推荐 store。
   *
   * 为什么放在这里、而不是 `App.tsx` 的 `useEffect([messages])`：那个 effect 要等 React 重渲染后
   * 才跑，而 `CUSTOM` 在 `onCustomEvent` 里是**同步**写的 —— 两源因此**不在同一时序上**。
   * 「模型先调 `recommend_products`、宿主随后推 CUSTOM」这一真机次序下，更早的工具结果会顶掉
   * 更晚的 CUSTOM，违反 spec R9-1「后到者胜」（`App.tsx` 的 `toolRecoRef` 注释已承认该风险，
   * 当时只用「值比较」缓解 —— 两源内容相同时没事，**不同**时仍会顶掉）。
   *
   * `onMessagesChanged` 与 `onCustomEvent` 同为 SDK **同步**派发，搬到这里之后
   * **到达顺序即写入顺序**。
   */
  const syncRecoFromToolResult = (): void => {
    const content = lastRecommendationContent(agent.messages)
    if (content === null || content === lastToolReco) return
    lastToolReco = content
    setRecoFromToolResult(content)
  }

  const subscription = agent.subscribe({
    // 四个回调都以 `disposed` 自查开头 —— 销毁后本轮残留的派发（含中止引发的 `onRunFailed`）
    // 不得再向外传播。理由见 `disposed` 声明。
    onMessagesChanged: () => {
      if (disposed) return
      syncRecoFromToolResult()
      notify()
    },
    onNewMessage: () => {
      if (!disposed) notify()
    },
    onRunFinalized: () => {
      if (disposed || failed) return
      notify()
    },
    // `CUSTOM` 事件（推荐推送的载体，spec R6 / R7）：**只搬运、不解析**。
    //
    // 为什么它不会污染消息与工具调用栏（R6）：
    // 1. **不进 `agent.messages`**：SDK 的 `applyEvents` 里 `EventType.CUSTOM` 分支**只**派发
    //    `onCustomEvent` 订阅者，`messages` 原样透传（实测见 `agent.test.ts` 的 R6-1 用例及其反证）。
    //    故下一轮全量重发历史时，请求体里不含任何推荐负载（R2-1「推荐负载不进历史」）。
    // 2. **不产生工具调用栏条目**：它不是 `TOOL_CALL_START` / `TOOL_CALL_RESULT`，订阅这两个事件的
    //    工具调用栏组件看不到它。
    // 3. **不计入整轮 usage**：整轮用量只由 `RUN_FINISHED` / `RUN_ERROR` 的 `usage` 数组承载（协议里
    //    是整轮级字段），`CUSTOM` 没有 usage 字段，天然不在汇总口径内。
    //
    // **value 不在这里解析**：归一化（对象 → JSON 文本 / 字符串原样保留 / 非法形状不崩）归
    // `reco.ts#setRecoFromCustomEvent`，真正的渲染语义归面板的 `parseRecommendation`。协议层多解析
    // 一次就会变成第二份解析器（F2 的硬约束），也会把「结构不符保留上一次」的口径提前固化在这里。
    onCustomEvent: (params) => {
      if (disposed) return
      setRecoFromCustomEvent(params.event.value)
      notify()
    },
    // 失败**不**走 `RUN_ERROR` 事件帧（硬契约 2 / R4）：服务端 username 校验短路在 `MapAGUIServer`
    // 之前，是**普通 HTTP 响应**，`@ag-ui/client` 把它转成挂 `status` / `payload` 的 `Error` 后派发到本回调。
    // 顺序 = 先 notify（让 UI 退出运行态）、再分派（清会话 / 提示），此后本轮**不再**有通知
    // ——「清会话」必须是本轮的**最后一次**写入（理由见 `failed` 与 `handleRunFailure`）。
    // 【L5 关键守卫】中止在途轮**也会**走到这里（真机实测 2026-09-21：在途轮中点退出，
    // 账户页弹出「网络异常，请稍后重试」）。用户主动切账户不是故障，不得提示。
    onRunFailed: (params) => {
      if (disposed) return
      failed = true
      notify()
      handleRunFailure(params.error)
    },
  })

  return {
    agent,
    username: config.username,
    get model(): string {
      return model
    },
    setModel(next: string): void {
      model = next
    },
    getMessages: () => agent.messages,
    isRunning: () => agent.isRunning,
    subscribe(listener: () => void): () => void {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    runRound: async (text: string): Promise<RunAgentResult> => {
      // 复位失败标记：上一轮的失败不影响本轮的通知与持久化。
      failed = false

      // 每条新用户消息必须带唯一 id：`addMessage` 不会自动生成 id，SDK 也不去重（design §5.1）。
      // 插入位置 = 列表末尾（append）。
      agent.addMessage({ id: crypto.randomUUID(), role: 'user', content: text })

      // 每轮都显式传**全量** `{ username, model }`：`forwardedProps` 不会自动携带（硬契约 3 / R3）。
      // C2：本轮装「首字节」表 → 收到首个事件自动撤表 → 无论成败都在 finally 再撤一次（下一轮重新装）。
      armFirstByteWatchdog()
      try {
        return await agent.runAgent({ forwardedProps: { username: config.username, model } })
      } finally {
        disarmFirstByteWatchdog()
      }
    },
    // L5：销毁 = **中止在途轮 + 断开订阅 + 立起 disposed 守卫**。三者作用面不同，见接口声明。
    dispose(): void {
      // 必须先置标记：`abortRun()` 引发的回调是异步派发的，标记先立起来才不会漏。
      disposed = true
      subscription.unsubscribe()
      agent.abortRun()
    },
  }
}
