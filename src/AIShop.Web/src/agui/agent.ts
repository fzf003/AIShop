/**
 * AG-UI 会话装配（tasks.md C4；spec R1 / R3 / R12 / R18-2）。
 *
 * 定位：**协议层**。与 AG-UI 的对接全部交给官方客户端 `@ag-ui/client` 的 `HttpAgent`
 * （R18-2：MUST NOT 自造「手写 SSE 逐行解析 + 自行维护 messages」），本文件只做三件事：
 *
 * 1. **装配** `new HttpAgent({ url: '/agui', threadId, initialMessages })`；
 * 2. **每轮显式传 `{ username, model }`**（硬契约 3 / R3）：`forwardedProps` **不会自动携带**，
 *    漏传一轮服务端就回落缺省身份（`steve`）与缺省模型（`ActiveModel`）；
 * 3. **订阅状态变化**（消息变更 / 运行失败 / 运行结束）并通知上层（store 侧据此持久化 + 渲染）。
 *
 * 消息数组由 SDK 的 `defaultApplyEvents` 自动维护，客户端**只订阅、不手工拼装**。
 * `agent.messages` 即「客户端持有的完整历史」——服务端 `ProvideChatHistoryAsync` 恒返回空，
 * 每轮 `RunAgentInput.messages` 由 SDK 取 `agent.messages` 的**全量快照**（硬契约 1 / R1）。
 *
 * 切换账户 = 重建本对象（`store.ts` 的 `startSession`）；`threadId` 随之重置，
 * 但客户端**不依赖 `threadId` 做隔离**：服务端按用户名归属会话（R12）。
 */
import { HttpAgent, type Message, type RunAgentResult } from '@ag-ui/client'

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
 * 订阅钩子说明（为什么三个都挂）：
 * - `onMessagesChanged`：流式增量与工具结果消息都会触发 → 上层据此持久化「完整」消息（R2-3 / 硬契约 1）；
 * - `onNewMessage`：本地 `addMessage`（用户消息）**不触发** `onMessagesChanged`，须单独接；
 * - `onRunFinalized` / `onRunFailed`：刷新 `isRunning`（运行态退出）。
 *
 * ⚠️ 失败**不**以 `RUN_ERROR` 事件帧到达（服务端 username 校验短路在 `MapAGUIServer` 之前，
 * 是普通 HTTP 响应），`runAgent()` 的 Promise **不会 reject**——错误经 `onRunFailed` 到达订阅者。
 * 该分支的处理（404 → 清会话回账户选择页）由 C5 在此处的 `onRunFailed` 内补齐，
 * **不得**改用 `try/catch await agent.runAgent()`。
 */
export function createAgent(config: AguiSessionConfig): AguiSession {
  const agent = new HttpAgent({
    url: AGUI_ENDPOINT,
    threadId: config.threadId,
    initialMessages: config.initialMessages === undefined ? undefined : [...config.initialMessages],
  })

  let model = config.model
  const listeners = new Set<() => void>()

  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发。
  const notify = (): void => {
    for (const listener of [...listeners]) listener()
  }

  agent.subscribe({
    onMessagesChanged: notify,
    onNewMessage: notify,
    onRunFinalized: notify,
    onRunFailed: notify,
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
      // 每条新用户消息必须带唯一 id：`addMessage` 不会自动生成 id，SDK 也不去重（design §5.1）。
      // 插入位置 = 列表末尾（append）。
      agent.addMessage({ id: crypto.randomUUID(), role: 'user', content: text })

      // 每轮都显式传**全量** `{ username, model }`：`forwardedProps` 不会自动携带（硬契约 3 / R3）。
      return agent.runAgent({ forwardedProps: { username: config.username, model } })
    },
  }
}
