/**
 * 会话 store（单例）：唯一 `HttpAgent` 实例 + 持久化 + React 订阅（tasks.md C4；design §8.1 / §8.2）。
 *
 * 职责：
 * - **单例**：全应用一个会话（`AguiSession`）；**切换账户 = 重建**（`threadId` 随之重置）；
 * - **持久化**：会话内消息变化即把**完整** `agent.messages`（含 `toolCalls` 与配对的 `role:"tool"`
 *   结果消息，**不做任何裁剪**）写入 `agui.messages.{username}`（硬契约 1 / R1 第 2 段 / R2）；
 * - **恢复**：启动时以 `readMessages(username)` 作为 `initialMessages`；
 * - **React 订阅**：`useSyncExternalStore` + `isRunning` 运行态。
 *
 * 身份来源（R3 第 4 段「两条通道写同一用户名」）：本模块**只**从 `currentUsername()`
 * （`api/http.ts`，背后是 `agui.username` 键）取用户名，绝不硬编码字面量、也不另存副本——
 * AG-UI 的 `forwardedProps.username` 与 REST 的 `?username=` 因此必然同源。
 */
import type { HttpAgent, Message } from '@ag-ui/client'
import { useSyncExternalStore } from 'react'

import { currentUsername } from '../api/http'
import { ensureThreadId, readMessages, writeMessages } from '../state/session'
import { createAgent, type AguiSession } from './agent'

/** 交给 React 的不可变快照（每次变更换新引用，满足 `useSyncExternalStore` 的引用比较）。 */
export interface SessionSnapshot {
  messages: readonly Message[]
  isRunning: boolean
}

let session: AguiSession | null = null
let unsubscribeSession: (() => void) | null = null
let snapshot: SessionSnapshot = { messages: [], isRunning: false }
const listeners = new Set<() => void>()

/** 重建快照并通知订阅者（快照对象**换新引用**，React 才能感知变化）。 */
function emitChange(): void {
  snapshot = {
    messages: session === null ? [] : [...session.getMessages()],
    isRunning: session?.isRunning() ?? false,
  }
  for (const listener of [...listeners]) listener()
}

/**
 * 会话内的任何变化（消息增量、用户消息、运行结束）都触发：先持久化，再通知渲染。
 *
 * 持久化收在「变化」这一处而不是渲染期：刷新（模拟）时读到的就是最后落库的那一份。
 */
function handleSessionChange(): void {
  if (session !== null) writeMessages(session.username, session.getMessages())
  emitChange()
}

export interface StartSessionOptions {
  /** 当前选中模型的 `id`（`GET /models` 响应项的节键，R3-2）。 */
  model: string
}

/**
 * 启动 / 切换账户：**重建** agent（`threadId` 随之重置）。
 *
 * 用户名取自 `currentUsername()`（唯一来源）；未选定账户时 fail-fast（调用点都在账户选定之后）。
 * 历史以 `readMessages(username)` 恢复，**不再在本地二次解析**（容错口径已收敛在 `state/session`）。
 *
 * 客户端**不依赖 `threadId` 做隔离**：服务端按用户名归属会话（`store_id = AGUIShopping:{username}`），
 * `threadId` 只是协议必填字段（R12）。
 */
export function startSession({ model }: StartSessionOptions): void {
  endSession()

  const username = currentUsername()
  if (username === null) {
    throw new Error('[AIShop] 尚未选定账户，无法启动会话')
  }

  session = createAgent({
    username,
    model,
    threadId: ensureThreadId(username),
    initialMessages: readMessages(username),
  })
  unsubscribeSession = session.subscribe(handleSessionChange)
  emitChange()
}

/**
 * 结束会话（退出登录 / 切账户前）：断开订阅、**销毁会话**并清内存态，**不动**持久化（清理归 `clearSession`）。
 *
 * `session.dispose()`（L5）是必须的：它断开 agent 侧订阅并中止在途轮。少了这一步，旧账户在途轮
 * 晚到的 `CUSTOM` 仍会写进**模块级全局**推荐 store —— 新账户面板被上一账户负载顶掉，随后还会落进
 * 新账户的 `agui.reco.{username}`。详见 `agent.ts#dispose` 与 `agent.test.ts` 的 L5 用例。
 *
 * 顺序：**先退订 store 侧**（下一句），再 `dispose()`。两侧订阅各管一段，退订在前 ——
 * 保证 `dispose()` 之后不存在仍指向本会话的活订阅。
 */
export function endSession(): void {
  unsubscribeSession?.()
  unsubscribeSession = null
  session?.dispose()
  session = null
  emitChange()
}

/** 切换模型：**下一轮**生效（在途本轮的 `forwardedProps` 已在 `runAgent` 时快照）。 */
export function setModel(model: string): void {
  session?.setModel(model)
}

/** 发一轮。用户消息由会话侧追加（带唯一 id），历史随 `agent.messages` 全量重发（R1）。 */
export async function runRound(text: string): Promise<void> {
  if (session === null) {
    throw new Error('[AIShop] 会话尚未启动，无法发送消息')
  }
  await session.runRound(text)
}

/** `useSyncExternalStore` 的快照读取点；未启动会话时返回空快照。 */
export function getSnapshot(): SessionSnapshot {
  return snapshot
}

/**
 * 当前会话的官方客户端实例；未启动会话时为 `null`。
 *
 * 存在的理由（C11 装配点）：工具胶囊要经 `attachToolEvents(agent, tracker)` 订阅 AG-UI 事件
 * （handoff-C7 遗留 1），而 `startSession` 不返回会话、store 也没有别的暴露面 —— 该只读访问器
 * 是「事件订阅」与「会话生命周期」对齐（切账户重建、旧订阅随旧 agent 失效）的最小接缝。
 * 它**不改变**任何会话行为：仅把已有实例交出去。
 */
export function getAgent(): HttpAgent | null {
  return session?.agent ?? null
}

/** 订阅 store 变更；返回退订函数。 */
export function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** React 侧订阅入口（design §8.1：不引入状态库，用 `useSyncExternalStore`）。 */
export function useSession(): SessionSnapshot {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
}
