/**
 * 应用外壳与切屏（C6：账户选择 → 模型选择 → 主界面；spec R5 / R6）。
 *
 * 屏幕流转与持久化：
 * - 选定账户 → 写 `agui.username`（刷新后回到同一身份，R5-2）→ 模型屏；
 * - 选定模型 → 写 `agui.model`（存 **`id`** 节键，R6-6）→ 主界面；
 * - 启动时 `initialScreen()` 按持久化状态决定落点：无账户 → 账户屏；有账户无模型 → 模型屏；两者都有 → 主界面。
 * - 模型清单到达后统一用 `resolveModelId` 校验一次：持久化 `id` 已失效时按 `isDefault`（全 false 取首项）
 *   回落并**回写**，避免把服务端不认识的键回传进 `forwardedProps.model`（R6-7）。
 *
 * 本工单只交付「切屏 + 两个选择器 + 主界面外壳」：主界面的聊天 / 推荐面板 / 购物车由 C11 装配。
 */
import { useCallback, useEffect, useState } from 'react'

import { AccountPicker } from './components/AccountPicker'
import { ModelBadge, ModelPicker } from './components/ModelPicker'
import { fetchModels, resolveModelId, type ModelInfo } from './data/models'
import { readModelId, readUsername, writeModelId, writeUsername } from './state/session'

import './styles/screens.css'

/** 三个屏幕：账户选择 → 模型选择 → 主界面（不引 router，见 design §6.1）。 */
export type Screen = 'account' | 'model' | 'main'

/**
 * 启动落点（spec R5-2「刷新保持身份」/ R6-6「刷新保持同一模型」）：
 * 有账户且有模型 → 直接进主界面（模型是否仍有效由清单到达后的 `resolveModelId` 校验）。
 */
function initialScreen(): Screen {
  if (readUsername() === null) return 'account'
  return readModelId() === null ? 'model' : 'main'
}

export default function App() {
  const [screen, setScreen] = useState<Screen>(initialScreen)
  const [models, setModels] = useState<readonly ModelInfo[] | null>(null)
  const [modelsError, setModelsError] = useState<string | null>(null)
  const [modelId, setModelId] = useState<string | null>(readModelId)

  const loadModels = useCallback(async () => {
    setModelsError(null)
    try {
      const list = await fetchModels()
      setModels(list)

      const resolved = resolveModelId(list, readModelId())
      if (resolved === null) {
        // 清单为空 = 服务端没配任何模型节：这是配置错误，不猜 id、不硬编码（spec R6）。
        setModelsError('服务端未配置任何模型')
        return
      }

      setModelId(resolved)
      writeModelId(resolved)
    } catch (error) {
      setModelsError(error instanceof Error ? error.message : '模型清单加载失败')
    }
  }, [])

  // 模型屏与主界面（顶栏徽标）都要清单，账户屏不需要。失败后不自动重试，等用户点「重试」。
  useEffect(() => {
    if (screen === 'account' || models !== null || modelsError !== null) return
    void loadModels()
  }, [screen, models, modelsError, loadModels])

  const selectAccount = (username: string) => {
    writeUsername(username)
    setScreen('model')
  }

  const selectModel = (id: string) => {
    writeModelId(id)
    setModelId(id)
    setScreen('main')
  }

  const modelsPending = models === null || modelsError !== null

  return (
    <div
      className={`screen ${screen === 'main' ? 'screen--main' : 'screen--auth'}`}
      data-screen={screen}
    >
      {screen === 'account' ? (
        <AccountPicker onSelect={selectAccount} />
      ) : modelsPending ? (
        <div className="gate">
          {modelsError === null ? (
            <p className="sub">正在加载模型清单…</p>
          ) : (
            <>
              <p className="sub">{modelsError}</p>
              <button type="button" className="ghost" onClick={() => void loadModels()}>
                重试
              </button>
            </>
          )}
        </div>
      ) : screen === 'model' ? (
        <>
          <div className="hd">
            <h1 className="logo">选择模型</h1>
            <p className="sub">对话中随时可切换</p>
          </div>
          <ModelPicker models={models} currentId={modelId} onSelect={selectModel} />
          <p className="note">
            清单来自 AguiHost 的 <code>GET /models</code>
          </p>
        </>
      ) : (
        <>
          <header className="appbar">
            <div className="brand">
              AI<span>Shop</span>
            </div>
            <div className="spacer" />
            <ModelBadge models={models} currentId={modelId} onSelect={selectModel} />
          </header>
          <main className="placeholder">
            <p className="sub">主界面（聊天 / 推荐面板 / 购物车）由 C11 装配</p>
          </main>
        </>
      )}
    </div>
  )
}
