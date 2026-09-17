/**
 * 顶部居中 Toast（spec R13 第 3 段 + 场景 R13-3 / design §7 / 原型 v3 `.toast`）。
 *
 * 形态照原型逐条：**顶部居中**、**绿底**（`--ok: #12a150`）白字小圆角、显示约 **3 秒**后自动消失、
 * **不阻塞点击**（`.toast` 上的 `pointer-events: none`）。
 *
 * 单一出口：模块级 store + `showToast(message)`，全应用共享同一个提示位
 * （App 层渲染一次 `<Toast />` 即可；加购成功与各类失败提示都复用它——spec R13-3 的
 * 「加购成功与失败提示复用」，也是 `errors.ts#dispatchApiFailure` 的 `ctx.toast` 出口）。
 */
import { useSyncExternalStore } from 'react'

import './Toast.css'

/** 自动消失时长：原型与 spec 都写「约 3 秒」。 */
export const TOAST_DURATION_MS = 3000

let message: string | null = null
let timer: ReturnType<typeof setTimeout> | null = null

const listeners = new Set<() => void>()

function notify(): void {
  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发。
  for (const listener of [...listeners]) listener()
}

function clearTimer(): void {
  if (timer !== null) {
    clearTimeout(timer)
    timer = null
  }
}

/** 显示一条提示；重复调用会重置 3 秒计时（后一条覆盖前一条）。 */
export function showToast(text: string): void {
  message = text
  clearTimer()
  timer = setTimeout(() => {
    timer = null
    message = null
    notify()
  }, TOAST_DURATION_MS)
  notify()
}

/** 立即收起提示（退出登录 / 测试清理）。 */
export function dismissToast(): void {
  clearTimer()
  message = null
  notify()
}

/** 当前提示文案；无提示时为 `null`。 */
export function getToastMessage(): string | null {
  return message
}

/** 订阅提示变化；返回退订函数。 */
export function subscribeToast(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/**
 * 提示出口（App 层渲染一次）。
 *
 * 无论有无提示都渲染同一个元素、只切换 `show` 类（与原型一致）；有提示时才写入文案，
 * 因此「3 秒后消失」在 DOM 上表现为文案被清空。`role="status"` 让提示可被无障碍读取，
 * 且它是 live region 因而不会抢走焦点、不影响用户继续点击其它按钮。
 */
export default function Toast() {
  const text = useSyncExternalStore(subscribeToast, getToastMessage, getToastMessage)
  return (
    <div className={text === null ? 'toast' : 'toast show'} role="status">
      {text}
    </div>
  )
}
