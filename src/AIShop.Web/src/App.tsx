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
 *    追踪器初值 = 该账户持久化的轮次，且在同一次 store 通知里把工具调用栏数据一并落库
 *    （D3 / design §15.4，见下方会话 effect）；
 * 2. **Toast 出口**：`setToastHandler(showToast)` —— 不接的话 404 / 5xx 的提示只落到控制台
 *    （handoff-C5 遗留 2）；
 * 3. **会话失效订阅**：`onSessionInvalid` → 清该账户持久化 + 清应用级身份 + 回账户选择页
 *    （R4 第 1 段；`agui.username` 必须一并清，否则刷新会被 `initialScreen()` 带回已不存在的账户）；
 * 4. **错误分派复用**：`ProductModal.onFailure` 走 `dispatchApiFailure`（R4 末段：REST 面的 404
 *    MUST NOT 退化为「只弹一个 Toast」）。
 *
 * 推荐面板接线（agui-reco-realtime F4 / design §4.5 B、C、D）：面板数据源 = 推荐内容 store
 * （`agui/reco.ts`），三个来源分别是 —— `CUSTOM` 事件（协议层已在 `agent.ts` 接好）、
 * **工具结果**（本文件对 `lastRecommendationContent(messages)` 的变化做 effect）、
 * **恢复初值**（会话 effect 内以 `readReco(username)` 优先、历史工具结果兜底）。
 * 面板组件与 props 形状零改动，`lastRecommendationContent` 函数保留（既是工具结果来源，
 * 也是刷新恢复的兜底来源）。负载的**持久化回写**属 F5，不在本处。
 */
import type { Message } from '@ag-ui/client'
import { useCallback, useEffect, useRef, useState } from 'react'

import { setToastHandler } from './agui/agent'
import { resetRecoContent, setRecoFromToolResult, useRecoContent } from './agui/reco'
import {
  endSession,
  getAgent,
  runRound,
  setModel,
  startSession,
  subscribe,
  useSession,
} from './agui/store'
import {
  attachToolEvents,
  createToolTracker,
  decodeToolResultContent,
  type ToolCallEntry,
  type ToolRound,
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
  readMessages,
  readModelId,
  readReco,
  readToolRounds,
  readUsername,
  writeModelId,
  writeToolRounds,
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
 * 启动时按持久化身份读回**工具调用栏数据**（收口裁决 D3 / design §15.4）。
 *
 * 这只是「首次渲染就有数据」的初值：真正的初值在进入主界面的那个 effect 里按当前账户再读一次
 * （那时账户一定已确定）。未选定账户时以空开始 —— 账户屏不渲染任何工具调用栏，读它没有意义。
 * 容错（读不到 / 非法 JSON / 非数组 → 空 + 告警）全部收敛在 `readToolRounds` 内。
 */
function initialToolRounds(): readonly ToolRound[] {
  const username = readUsername()
  return username === null ? [] : readToolRounds(username)
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
  const [tracker, setTracker] = useState<ToolTracker>(() =>
    createToolTracker({ initialRounds: initialToolRounds() }),
  )

  /**
   * 当前追踪器的引用：持久化订阅在 effect 外存活期内读它，避免闭包捕获到过期的 tracker 实例
   * （切账户/重建时 `setTracker` 换新实例，订阅却还挂着上一次的引用）。
   */
  const trackerRef = useRef<ToolTracker | null>(null)

  /**
   * 工具结果来源的「已见值」：`lastRecommendationContent(messages)` 上一次被写进推荐 store 的原文。
   *
   * 两个用途（agui-reco-realtime F4）：
   * 1. **变化判定** —— 该 effect 的依赖是 `messages`（每次 store 通知都换新引用），若不做值比较，
   *    每一轮纯文本对话都会把同一条旧工具结果重写一遍（对 store 是无谓广播，对面板是无谓覆盖：
   *    会把更晚到达的 `CUSTOM` 内容顶掉）；
   * 2. **会话启动基线** —— 进入主界面时由会话 effect 置为「历史里最后一条工具结果」，使恢复期
   *    不把历史旧值当成新变化（否则 `agui.reco.{username}` 的恢复优先级失效）。
   */
  const toolRecoRef = useRef<string | null>(null)

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
   * 随旧 agent 失效（handoff-C7 遗留 1），因此这里必须随 `startSession` 一起换新实例；
   * 初值取该账户持久化的轮次，刷新后工具调用栏据此恢复（D3 / design §15.4）。
   *
   * 工具调用栏数据的**持久化**也挂在这里（design §15.4「与 messages 同一处、同一时刻」）：
   * `subscribe` 订阅的是 store 的通知链，而 store 正在**同一次通知**里写 `agui.messages.{username}`
   * （`store.ts` 的 `handleSessionChange`）——两键因此同批次落库，不会出现「消息清了、工具数据没清」。
   * 不把写入口放进 `store.ts` 的理由：tracker 是 App 的 React state，store 拿不到它；在这里订阅
   * 是「不动 store 行为」的最小接缝。
   *
   * **推荐内容的初值**（agui-reco-realtime F4 / design §4.5 D）也在这里注入：优先用该账户持久化的
   * `agui.reco.{username}`（刷新后面板原样恢复），读不到才回退「历史里最后一条 `recommend_products`
   * 工具结果」，两者都没有则以 `null` 开始（面板显示占位）。同时把这份**回退值**记为工具结果来源的
   * 基线（`toolRecoRef`）——否则紧随其后的工具结果 effect 会立刻用历史里的旧工具结果把刚恢复的
   * `agui.reco.{username}` 盖掉，恢复优先级就形同虚设。
   */
  useEffect(() => {
    const username = readUsername()
    if (screen !== 'main' || username === null) return
    const model = readModelId()
    if (model === null) return

    startSession({ model })

    const restoredToolReco = lastRecommendationContent(readMessages(username))
    resetRecoContent(readReco(username) ?? restoredToolReco)
    toolRecoRef.current = restoredToolReco

    const next = createToolTracker({ initialRounds: readToolRounds(username) })
    trackerRef.current = next
    setTracker(next)

    const agent = getAgent()
    if (agent === null) return

    const detachTools = attachToolEvents(agent, next)
    const detachPersist = subscribe(() => {
      // 会话已结束（退出登录 / 切账户）→ 不再回写：`closeToAccount` 的顺序是「先 `clearSession`
      // 再 `endSession`」，而 `endSession` 也会发一次通知；若在这里照写，刚被清掉的两个键（消息由
      // store 的同款守卫挡住）会被工具数据这一路**重新创建**（C5「404 之后历史还在」是同类事故）。
      if (getAgent() === null) return
      writeToolRounds(username, trackerRef.current?.getRounds() ?? [])
    })

    return () => {
      detachTools()
      detachPersist()
    }
  }, [screen])

  /**
   * 推荐内容的**工具结果来源**（agui-reco-realtime F4 / design §4.5 C）：`messages` 里
   * `lastRecommendationContent` 的**变化**写进推荐 store。
   *
   * 三条口径：
   * - **只在非 `null` 时写**：没有推荐工具结果的轮次（闲聊、纯加购）不得把面板清空 ——
   *   「不在轮次之间清空」是既有口径（`reco.ts#setRecoFromToolResult` 同样忽略 `null`），
   *   面板的「解析失败保留上一次」也依赖它；
   * - **只在值变化时写**：与 `toolRecoRef` 比较（`messages` 每次通知都换新引用，不做值比较会对
   *   同一条旧结果反复广播，见该 ref 的注释）；
   * - **解码在读取侧恰好一次**：`lastRecommendationContent` 内的 `decodeToolResultContent` 是
   *   唯一的 wire 边界解码点，store 侧不再解码（`CUSTOM` 路径的硬约束 2）。
   *
   * 到达顺序即覆盖顺序（后到者胜，spec R9-1）：本 effect 写的是**早于** `CUSTOM` 到达的那一路，
   * 因此同轮「先工具结果、后 `CUSTOM`」时面板最终显示 `CUSTOM` 的内容。
   */
  useEffect(() => {
    const content = lastRecommendationContent(messages)
    if (content === null || content === toolRecoRef.current) return
    toolRecoRef.current = content
    setRecoFromToolResult(content)
  }, [messages])

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

  /**
   * 退出到账户选择屏的公共善后：清当前账户 + 清身份 + 断会话 + 丢购物车投影 + 丢推荐投影 + 关浮层。
   *
   * `resetRecoContent()` 与 `resetCart()` **同款时机**：推荐面板的内容是「上一轮会话」的产物，
   * 换账户后旧投影必须丢弃（否则面板会显示上一个账户残留的推荐）。@see reco.ts#resetRecoContent
   */
  function closeToAccount(): void {
    const username = readUsername()
    if (username !== null) clearSession(username)
    clearUsername()
    endSession()
    // 购物车是服务端权威态且按用户名归属：换账户后旧投影必须丢弃（handoff-C10 遗留 2）。
    resetCart()
    resetRecoContent()
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
  // 推荐面板的数据源 = 推荐内容 store（三个来源：工具结果 / `CUSTOM` 事件 / 恢复初值，
  // 见 `agui/reco.ts`）。面板 props 形状不变，仍是 `content?: string | null`。
  const recoContent = useRecoContent()

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
