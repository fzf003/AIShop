/**
 * AG-UI 会话装配（tasks.md C4 + C5；spec R1 / R3 / R4 / R12 / R18-2）。
 *
 * 定位：**协议层**。与 AG-UI 的对接全部交给官方客户端 `@ag-ui/client` 的 `HttpAgent`
 * （R18-2：MUST NOT 自造「手写 SSE 逐行解析 + 自行维护 messages」），本文件只做四件事：
 *
 * 1. **装配** `new HttpAgent({ url: '/agui', threadId, initialMessages })`；
 * 2. **每轮显式传 `{ username, model }`**（硬契约 3 / R3）：`forwardedProps` **不会自动携带**，
 *    漏传一轮服务端就回落缺省身份（`steve`）与缺省模型（`ActiveModel`）；
 * 3. **订阅状态变化**（消息变更 / 运行失败 / 运行结束）并通知上层（store 侧据此持久化 + 渲染）；
 * 4. **运行失败分派**（硬契约 2 / R4，C5）：失败经 `onRunFailed` 到达 → 复用 `api/errors.ts` 的
 *    `dispatchApiFailure`（404 清会话回账户选择页 / 5xx 只提示不破坏历史），见 `handleRunFailure`。
 *
 * 消息数组由 SDK 的 `defaultApplyEvents` 自动维护，客户端**只订阅、不手工拼装**。
 * `agent.messages` 即「客户端持有的完整历史」——服务端 `ProvideChatHistoryAsync` 恒返回空，
 * 每轮 `RunAgentInput.messages` 由 SDK 取 `agent.messages` 的**全量快照**（硬契约 1 / R1）。
 *
 * 切换账户 = 重建本对象（`store.ts` 的 `startSession`）；`threadId` 随之重置，
 * 但客户端**不依赖 `threadId` 做隔离**：服务端按用户名归属会话（R12）。
 */
import { HttpAgent, type Message, type RunAgentResult } from '@ag-ui/client'

import { dispatchApiFailure } from '../api/errors'

/**
 * 顶部提示（Toast）出口。
 *
 * 协议层不依赖 React：真实实现由 App 层（C11 装配）经 `setToastHandler` 注入；
 * 默认实现只写控制台 —— 目的是「失败不静默」，而不是替代 UI。
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
}

/**
 * 装配一个 AG-UI 会话。
 *
 * 订阅钩子说明（为什么四个都挂）：
 * - `onMessagesChanged`：流式增量与工具结果消息都会触发 → 上层据此持久化「完整」消息（R2-3 / 硬契约 1）；
 * - `onNewMessage`：本地 `addMessage`（用户消息）**不触发** `onMessagesChanged`，须单独接；
 * - `onRunFinalized`：刷新 `isRunning`（运行态退出）；
 * - `onRunFailed`：**失败处理的唯一入口**（SDK 把非 2xx 转成此回调，见下）。
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

export function createAgent(config: AguiSessionConfig): AguiSession {
  const agent = new HttpAgent({
    url: AGUI_ENDPOINT,
    threadId: config.threadId,
    initialMessages: config.initialMessages === undefined ? undefined : [...config.initialMessages],
  })

  let model = config.model
  const listeners = new Set<() => void>()
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

  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发。
  const notify = (): void => {
    for (const listener of [...listeners]) listener()
  }

  agent.subscribe({
    onMessagesChanged: notify,
    onNewMessage: notify,
    onRunFinalized: () => {
      if (failed) return
      notify()
    },
    // 失败**不**走 `RUN_ERROR` 事件帧（硬契约 2 / R4）：服务端 username 校验短路在 `MapAGUIServer`
    // 之前，是**普通 HTTP 响应**，`@ag-ui/client` 把它转成挂 `status` / `payload` 的 `Error` 后派发到本回调。
    // 顺序 = 先 notify（让 UI 退出运行态）、再分派（清会话 / 提示），此后本轮**不再**有通知
    // ——「清会话」必须是本轮的**最后一次**写入（理由见 `failed` 与 `handleRunFailure`）。
    onRunFailed: (params) => {
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
      return agent.runAgent({ forwardedProps: { username: config.username, model } })
    },
  }
}
