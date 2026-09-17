/**
 * 账户选择器（spec R5「账户选择器（3 个固定账户）」/ design §7）。
 *
 * - 3 张**横排等宽卡片**（158px / 间距 16px，与模型卡对齐，靠 `.lcard` 定宽 + `flex:1` + `gap:16px` 实现）；
 * - 各带独立配色与头像标识（与原型 v3 逐项对齐）；
 * - 账户是**固定的三个种子用户**：MUST NOT 提供自由输入（无凭证体系，身份由请求体自称）。
 */
import './AccountPicker.css'

/** 一个固定账户的展示信息。 */
export interface AccountInfo {
  /** 账户名（写进 `agui.username`，也是 `forwardedProps.username` 与 `?username=` 的取值）。 */
  id: string
  /** 展示名（卡片主文案）。 */
  label: string
  /** 头像标识 emoji。 */
  emoji: string
  /** 独立配色的 CSS 类名（定义在本文件的 `.css` 里）。 */
  tone: string
}

/** 服务端种子的 3 个账户（spec R5 第 1 段）。 */
export const ACCOUNTS: readonly AccountInfo[] = [
  { id: 'marla', label: 'Marla', emoji: '👩', tone: 'u-marla' },
  { id: 'steve', label: 'Steve', emoji: '👨', tone: 'u-steve' },
  { id: 'fzf003', label: 'fzf003', emoji: '🧑', tone: 'u-fzf' },
]

/**
 * 按账户名取展示信息；非种子账户（如手工改过 localStorage）返回 `null`。
 *
 * 主界面的用户徽标（C11）与退出流程复用同一张表，避免「同一个账户两处配色/文案」。
 */
export function accountOf(username: string | null): AccountInfo | null {
  if (username === null) return null
  return ACCOUNTS.find((account) => account.id === username) ?? null
}

export interface AccountPickerProps {
  /** 选中某账户（App 层负责持久化并切到模型屏）。 */
  onSelect: (username: string) => void
}

export function AccountPicker({ onSelect }: AccountPickerProps) {
  return (
    <>
      <div className="hd">
        <h1 className="logo">
          AI<span>Shop</span> 购物助手
        </h1>
        <p className="sub">选择一个账户开始</p>
      </div>

      <div className="lcard">
        <h3>选择账户</h3>
        <div className="login-btns">
          {ACCOUNTS.map((account) => (
            <button
              key={account.id}
              type="button"
              className={`ubtn ${account.tone}`}
              data-username={account.id}
              onClick={() => onSelect(account.id)}
            >
              <span className="av">{account.emoji}</span>
              {account.label}
              <span className="key">{account.id}</span>
            </button>
          ))}
        </div>
      </div>
    </>
  )
}
