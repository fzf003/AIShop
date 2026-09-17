import { useState } from 'react'

/** 三个屏幕：账户选择 → 模型选择 → 主界面（不引 router，见 design §6.1）。 */
export type Screen = 'account' | 'model' | 'main'

const SCREEN_HINT: Record<Screen, string> = {
  account: '选择一个账户开始',
  model: '选择模型',
  main: '主界面',
}

const NEXT_SCREEN: Record<Screen, Screen> = {
  account: 'model',
  model: 'main',
  main: 'account',
}

/**
 * 应用骨架。
 *
 * C1 只交付「屏幕枚举 + 本地 state 切屏」，不含任何屏幕内容：
 * - 账户选择 / 模型清单由 C6 接入；
 * - 主界面装配由 C11 接入。
 * 因此这里的切屏控件是**临时骨架**，会被后续工单替换。
 */
export default function App() {
  const [screen, setScreen] = useState<Screen>('account')

  return (
    <div className="screen" data-screen={screen}>
      <h1 className="logo">
        AI<span>Shop</span> 购物助手
      </h1>
      <p className="sub">{SCREEN_HINT[screen]}</p>
      <button type="button" onClick={() => setScreen(NEXT_SCREEN[screen])}>
        切换屏幕
      </button>
    </div>
  )
}
