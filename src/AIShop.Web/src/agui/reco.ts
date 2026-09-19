/**
 * 推荐内容单一 store（agui-reco-realtime F2 / spec R7、R9 / design §4.5）。
 *
 * 三个来源（工具结果 / `CUSTOM` 事件 / 重置）都写进**这一个**模块级单值，
 * 推荐面板只从这里读 —— 面板「数据唯一来源」的边界因此落在 store 上，
 * 而不是散落在各订阅点的 `useState` 里。
 *
 * 实现形态与 `state/cart.ts` / `components/Toast.tsx` **同款**（模块级可变值 +
 * `listeners` + `useSyncExternalStore`），因为它是本项目已验证的「单一出口 store」写法。
 *
 * 归一化后仍交给 `RecoPanel` 的既有 `parseRecommendation`（本模块**不新增第二份解析器**）：
 * store 只管「最近一次的推荐结果文本是什么」，不管它是否合法、能否渲染。
 *
 * 两条硬约束（实现与评审都必须遵守）：
 *
 * 1. **按到达顺序覆盖，无优先级规则**。三个写入口共用同一条覆盖路径（后到者胜），
 *    不存在「工具结果优先于 CUSTOM」之类的裁决 —— 两条通道承载的是同一个事实（最近一次推荐），
 *    谁先到达谁就是「旧的」。引入优先级会让「后到的更权威」这条显而易见的时序语义失效，
 *    并让 panel 显示一个比实际更陈旧的结果。
 * 2. **不得对 `CUSTOM` 的 value 套解码**。`agui/tools.ts#decodeToolResultContent` 剥的是
 *    宿主对**工具结果**多编码的那一层，**恰好一层且非幂等**（工具真返回带引号文本 `"hi"` 时，
 *    再解一次会把引号吃掉）。`CUSTOM` 事件的 `value` 未经多编码，套用该解码会凭空多剥一层。
 *    故解码只发生在工具结果那条路径的调用方（wire 边界），本模块**不再调**它。
 */
import { useSyncExternalStore } from 'react'

/**
 * 最近一次的推荐结果文本（可直接喂 `RecoPanel` 的 `parseRecommendation`）；从未写过为 `null`。
 *
 * 只存文本不存视图模型：面板的解析口径（结构不符 → 保留上一次 / 显式 `false` → 兜底提示）
 * 属于展示层，放进来会变成第二份解析器。
 */
let latest: string | null = null

const listeners = new Set<() => void>()

function notify(): void {
  // 复制一份再遍历：订阅者在回调里退订不会打乱本次派发（与 cart / Toast 同款）。
  for (const listener of [...listeners]) listener()
}

/** 写入并广播（三个入口的唯一落点，保证覆盖口径只有一处）。 */
function write(next: string): void {
  latest = next
  notify()
}

/**
 * `useSyncExternalStore` 的快照读取点。
 *
 * 返回的是 `string | null` 这类**原始值**：React 用 `Object.is` 比较新旧快照，对原语而言
 * 就是**值比较**，因而「内容变化 → 快照不相等 → 重渲染」自然成立，无需像
 * `state/cart.ts`（持有对象型复合状态）那样每次变更重建对象来制造新引用。
 * 代价是**写入同一字符串不会触发重渲染** —— 这正是所需行为（内容没变，没有可变的东西）。
 */
export function getRecoSnapshot(): string | null {
  return latest
}

/** 订阅推荐内容变化；返回退订函数。 */
export function subscribeReco(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** React 侧订阅入口（与 `state/cart.ts#useCart` / `Toast` 同构）。 */
export function useRecoContent(): string | null {
  return useSyncExternalStore(subscribeReco, getRecoSnapshot, getRecoSnapshot)
}

/**
 * 工具结果来源：写入 `TOOL_CALL_RESULT.content`。
 *
 * **入场值必须是调用方已解码的**（`decodeToolResultContent` 只在 wire 边界调一次）——
 * 本函数再解一次就会吃掉工具本意的引号（`decodeToolResultContent` 的注释已写明「勿链式」）。
 *
 * `null`（该轮没有工具结果 / 尚未收到）时**不覆盖**：推荐面板「不在轮次之间清空」
 * 是既有口径（spec R9-3 的「解析失败保留上一次内容」同理）—— 清空只能由
 * `resetRecoContent` 显式发起（切账户 / 退出）。
 */
export function setRecoFromToolResult(content: string | null): void {
  if (content === null) return
  write(content)
}

/**
 * `CUSTOM` 事件来源：把事件的 `value` 归一化为推荐结果文本。
 *
 * 归一化 = `typeof value === 'string' ? value : JSON.stringify(value)`：
 * - 对象型（服务端实际推送的形状，与持久化 `agui.reco.{username}` 同形）→ 序列化成 JSON 文本；
 * - 字符串型（合法 JSON 文本）→ **原样保留**，不解析再序列化（逐字节可逆，面板拿到的就是收到的）。
 *
 * ⚠️ **MUST NOT 套 `decodeToolResultContent`**：该解码剥的是工具结果路径上的宿主多编码层，
 * 恰好一层且非幂等；`CUSTOM` 的 `value` 未多编码，套用会多剥一层（见文件头硬约束 2）。
 *
 * `null` / 数字 / 数组等非法形状同样走 `JSON.stringify`，**绝不抛** —— 结构不符由面板的
 * `parseRecommendation` 判为「无法渲染」并保留上一次内容（spec「非法 value 不崩」）。
 */
export function setRecoFromCustomEvent(value: unknown): void {
  write(typeof value === 'string' ? value : JSON.stringify(value))
}

/**
 * 重置：丢弃当前内容并以 `value` 为初值（切账户 / 退出共用同一入口，与 `resetCart()` 同款时机）。
 *
 * 默认清为 `null`（面板回到占位）；传入初值则用于「刷新恢复」—— 但**恢复**本身由调用方从
 * `agui.reco.{username}` 读出后经本入口注入（F5），本模块不读存储。
 */
export function resetRecoContent(value: string | null = null): void {
  latest = value
  notify()
}
