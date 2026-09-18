/**
 * 应用外壳与主界面装配（C6 切屏 + C11 主界面；spec R5 / R6 / R7 / R10 / R12 / R17）。
 *
 * 屏幕流转与持久化：
 * - 选定账户 → 写 `agui.username`（刷新后回到同一身份，R5-2）→ 模型屏；
 * - 选定模型 → 写 `agui.model`（存 **`id`** 节键，R6-6）→ 主界面；
 * - 启动时 `initialScreen()` 按持久化状态决定落点：无账户 → 账户屏；有账户无模型 → 模型屏；两者都有 → 主界面。
 * - 模型清单到达后统一用 `resolveModelId` 校验一次：持久化 `id` 已失效时按 `isDefault`（全 false 取首项）
 *   回落并**回写**，避免把服务端不认识的键回传进 `forwardedProps.model`（R6-7）。
 *
 * C11 装配（本工单新增的四条接线，漏一处即功能静默失效）：
 * 1. **会话生命周期**：进入主界面即 `startSession({ model })`（用户名由 store 内部从 `currentUsername()`
 *    取，单一来源），并同步创建/挂载工具胶囊追踪器（`attachToolEvents`，handoff-C7 遗留 1）；
 * 2. **Toast 出口**：`setToastHandler(showToast)` —— 不接的话 404 / 5xx 的提示只落到控制台
 *    （handoff-C5 遗留 2）；
 * 3. **会话失效订阅**：`onSessionInvalid` → 清该账户持久化 + 清应用级身份 + 回账户选择页
 *    （R4 第 1 段；`agui.username` 必须一并清，否则刷新会被 `initialScreen()` 带回已不存在的账户）；
 * 4. **错误分派复用**：`ProductModal.onFailure` 走 `dispatchApiFailure`（R4 末段：REST 面的 404
 *    MUST NOT 退化为「只弹一个 Toast」）。
 */
import type { Message } from '@ag-ui/client'
import { useCallback, useEffect, useMemo, useState } from 'react'

import { setToastHandler } from './agui/agent'
import {
  endSession,
  getAgent,
  runRound,
  setModel,
  startSession,
  useSession,
} from './agui/store'
import {
  attachToolEvents,
  createToolTracker,
  decodeToolResultContent,
  type ToolCallEntry,
  type ToolTracker,
} from './agui/tools'
import { dispatchApiFailure, onSessionInvalid } from './api/errors'
import { currentUsername } from './api/http'
import { AccountPicker, accountOf } from './components/AccountPicker'
import CartDrawer from './components/CartDrawer'
import ChatPanel from './components/ChatPanel'
import { ModelBadge, ModelPicker } from './components/ModelPicker'
import ProductModal from './components/ProductModal'
import RecoPanel from './components/RecoPanel'
import Toast, { showToast } from './components/Toast'
import { fetchModels, resolveModelId, type ModelInfo } from './data/models'
import { addToCart, refreshCart, resetCart, useCart } from './state/cart'
import {
  clearSession,
  clearUsername,
  readModelId,
  readUsername,
  writeModelId,
  writeUsername,
} from './state/session'

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

/**
 * 「最近一次 `recommend_products` 的工具结果原文」（spec R9 第 1 段的数据来源）。
 *
 * 口径（handoff-C8 遗留 1）：**必须是该工具自己的结果**，不能把别的工具结果（`get_cart_summary`
 * 的纯文本等）喂给推荐面板；取**最新一条**（消息序最后），且**不在轮次之间清空** —— 清空会让
 * 「解析失败保留上一次内容」（R9-3）退化成闪回占位。
 *
 * 宿主把工具结果字符串多编码了一层（`content === JSON.stringify(result)`，design §15.1 / D1）：
 * 在**读取侧**恰好剥一次（`decodeToolResultContent`）。这是读取边界，**不改写** `messages`
 * —— 持久化（R2）里仍是宿主原样送达的 `content`。只调用一次，勿链式。
 */
function lastRecommendationContent(messages: readonly Message[]): string | null {
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

export default function App() {
  const [screen, setScreen] = useState<Screen>(initialScreen)
  const [models, setModels] = useState<readonly ModelInfo[] | null>(null)
  const [modelsError, setModelsError] = useState<string | null>(null)
  const [modelId, setModelId] = useState<string | null>(readModelId)
  const [cartOpen, setCartOpen] = useState(false)
  const [productsOpen, setProductsOpen] = useState(false)
  const [tracker, setTracker] = useState<ToolTracker>(() => createToolTracker())

  const { messages, isRunning } = useSession()
  const { cart } = useCart()

  // 顶部提示出口：AG-UI 面上的 404 / 5xx 经这里变成可见提示（handoff-C5 遗留 2）。
  useEffect(() => {
    setToastHandler(showToast)
    return () => setToastHandler(null)
  }, [])

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

  /**
   * 会话生命周期 = 与「主界面」同生命周期。
   *
   * 依赖只写 `screen`：切模型（`selectModel`）**不得**重建会话——`startSession` 会重建 `HttpAgent`
   * 并只从持久化恢复历史，而 R6-5 要求「切换从下一轮生效、在途本轮不受影响」，重建会话会把
   * 在途轮次一并抹掉。因此模型值在进入主界面时读一次（`readModelId()`），之后的切换走 `setModel`。
   *
   * 追踪器与 agent 成对重建：`attachToolEvents` 订阅的是**当期** `HttpAgent`，切账户后旧订阅
   * 随旧 agent 失效（handoff-C7 遗留 1），因此这里必须随 `startSession` 一起换新实例。
   */
  useEffect(() => {
    if (screen !== 'main' || readUsername() === null) return
    const model = readModelId()
    if (model === null) return

    startSession({ model })

    const next = createToolTracker()
    setTracker(next)

    const agent = getAgent()
    if (agent === null) return
    return attachToolEvents(agent, next)
  }, [screen])

  /**
   * 会话失效（服务端判定该账户不存在）→ 清该账户本地数据 + 回账户选择页（spec R4 第 1 段）。
   *
   * `dispatchApiFailure` 已清过该账户的 threadId / 消息（幂等，这里再清一次无害），并已给出提示；
   * 本订阅**额外必须**清 `agui.username` —— 否则刷新页面时 `initialScreen()` 会按残留的身份
   * 把用户直接带回主界面（handoff-C3 遗留 2 / handoff-C6 遗留 3）。
   */
  useEffect(() => {
    return onSessionInvalid(() => {
      closeToAccount()
    })
  }, [])

  /** 退出到账户选择屏的公共善后：清当前账户 + 清身份 + 断会话 + 丢购物车投影 + 关浮层。 */
  function closeToAccount(): void {
    const username = readUsername()
    if (username !== null) clearSession(username)
    clearUsername()
    endSession()
    // 购物车是服务端权威态且按用户名归属：换账户后旧投影必须丢弃（handoff-C10 遗留 2）。
    resetCart()
    setCartOpen(false)
    setProductsOpen(false)
    setScreen('account')
  }

  const selectAccount = (username: string): void => {
    writeUsername(username)
    setScreen('model')
  }

  const selectModel = (id: string): void => {
    writeModelId(id)
    setModelId(id)

    // 已在主界面 = 顶栏下拉切换：只改「当前选中项」，**下一轮**生效（R6-5）。
    // 在途本轮的 `forwardedProps` 已在 `runAgent` 时快照，不受影响。
    if (screen === 'main') {
      setModel(id)
      return
    }
    setScreen('main')
  }

  /**
   * 发一轮。
   *
   * 失败轮的 `runAgent()` **会 reject**（handoff-C5 遗留 1），而失败处理已由协议层的
   * `onRunFailed` 完成 —— 这里只负责不让它变成控制台的 unhandled rejection。
   */
  const send = (text: string): void => {
    void runRound(text).catch(() => undefined)
  }

  const findToolCall = useCallback(
    (toolCallId: string): ToolCallEntry | null => tracker.findToolCall(toolCallId),
    [tracker],
  )

  const handleProductFailure = (error: unknown): void => {
    void dispatchApiFailure(error, { toast: showToast, refreshCart })
  }

  const modelsPending = models === null || modelsError !== null
  const account = accountOf(currentUsername())
  const cartCount = cart?.totalItems ?? 0
  const recoContent = useMemo(() => lastRecommendationContent(messages), [messages])

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
            {account !== null && (
              <div className={`chip ${account.tone}`} data-username={account.id}>
                <span className="av">{account.emoji}</span>
                {account.label}
              </div>
            )}
            <button type="button" className="logout" title="退出登录" onClick={closeToAccount}>
              退出
            </button>
            <div className="spacer" />
            <button type="button" className="ghost" onClick={() => setProductsOpen(true)}>
              🛍 浏览商品
            </button>
            <ModelBadge models={models} currentId={modelId} onSelect={selectModel} />
            <button type="button" className="cartbtn" onClick={() => setCartOpen(true)}>
              🛒 购物车
              {cartCount > 0 && <span className="cnt">{cartCount}</span>}
            </button>
          </header>

          <div className="main">
            <ChatPanel
              messages={messages}
              isRunning={isRunning}
              findToolCall={findToolCall}
              onSend={send}
            />
            <RecoPanel content={recoContent} onAdd={addToCart} />
          </div>

          <CartDrawer open={cartOpen} onClose={() => setCartOpen(false)} />
          {/* `ProductDetail` 由 `ProductModal` 内部渲染（handoff-C9 遗留 3），此处不重复装配。 */}
          <ProductModal
            open={productsOpen}
            onClose={() => setProductsOpen(false)}
            onAdd={addToCart}
            onFailure={handleProductFailure}
          />
        </>
      )}

      {/* 提示位：全应用只渲染一次（加购成功 / 各类失败共用）。 */}
      <Toast />
    </div>
  )
}
