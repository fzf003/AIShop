/**
 * 推荐内容单一 store 用例（agui-reco-realtime tasks.md F2「验收 / 测试」/ spec R7、R9）。
 *
 * 覆盖四条：
 * - **到达顺序**（R9-1）：三个写入口共用同一条覆盖路径，后到者胜、**无固定优先级**；
 * - **归一化**（R7-1 / R7-2）：对象型 → JSON 文本（可解析回对象）、字符串型 → 原样保留、
 *   非法形状 → 归一化为其 JSON 文本且不抛；
 * - **不套解码**（R7 第 2 段）：`CUSTOM` 的 value **MUST NOT** 过 `decodeToolResultContent`
 *   （该解码恰好剥一层且非幂等）—— 用「多编码一层的字符串」样本钉死；
 * - **订阅语义**：每次有效写入通知恰好一次、退订生效、`resetRecoContent` 的初值语义、
 *   工具结果 `null` 不覆盖。
 *
 * 反证（否定性断言的硬要求，实测红/绿见 handoff-F2）：
 * - 在 `setRecoFromCustomEvent` 里临时套 `decodeToolResultContent` → 「不套解码」用例必红；
 * - 把覆盖改成「工具结果恒胜」的固定优先级 → 「到达顺序」用例必红。
 */
import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { readReco, writeReco } from '../state/session'
import {
  getRecoSnapshot,
  resetRecoContent,
  setRecoFromCustomEvent,
  setRecoFromToolResult,
  subscribeReco,
  useRecoContent,
} from './reco'
import { decodeToolResultContent } from './tools'

/** 工具结果的**解码后**文本（`attachToolEvents` / `App.tsx` 在 wire 边界已剥掉宿主多编码层）。 */
const TOOL_A = '{"message":"A","products":[]}'

/** 面板能直接消费的推荐对象（与 `CUSTOM` 事件的 `value`、持久化 `agui.reco.{username}` 同形）。 */
const RECO_B = { message: 'B', products: [] }

beforeEach(() => {
  // store 是模块级单例：每个用例前显式清空，避免用例间串味。
  resetRecoContent()
})

describe('setRecoFromToolResult', () => {
  it('写入已解码的工具结果文本，快照即该文本', () => {
    setRecoFromToolResult(TOOL_A)
    expect(getRecoSnapshot()).toBe(TOOL_A)
  })

  it('null 不覆盖（沿用「不在轮次之间清空」的既有口径）', () => {
    setRecoFromToolResult(TOOL_A)
    setRecoFromToolResult(null)
    expect(getRecoSnapshot()).toBe(TOOL_A)
  })

  it('null 也不广播（本次没有产生任何变更）', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeReco(listener)
    setRecoFromToolResult(null)
    expect(listener).not.toHaveBeenCalled()
    unsubscribe()
  })
})

describe('setRecoFromCustomEvent 归一化', () => {
  it('对象型 value → 序列化为 JSON 文本，可直接 JSON.parse 回同一对象', () => {
    setRecoFromCustomEvent(RECO_B)
    const snapshot = getRecoSnapshot()
    expect(snapshot).toBe(JSON.stringify(RECO_B))
    expect(JSON.parse(snapshot as string)).toEqual(RECO_B)
  })

  it('字符串型 value → 原样保留（不解析再序列化，逐字节可逆）', () => {
    // 刻意给一个「键序 / 空白与 JSON.stringify 默认输出不同」的文本，原样保留才可能逐字节相等。
    const text = '{ "products" : [],\n  "message" : "C" }'
    setRecoFromCustomEvent(text)
    expect(getRecoSnapshot()).toBe(text)
  })

  it('null / 数字等非法 value → 归一化为其 JSON 文本且不抛（面板解析失败保留上一次）', () => {
    expect(() => setRecoFromCustomEvent(null)).not.toThrow()
    expect(getRecoSnapshot()).toBe('null')

    expect(() => setRecoFromCustomEvent(42)).not.toThrow()
    expect(getRecoSnapshot()).toBe('42')
  })

  it('结构非法但仍是合法 JSON 的对象 → 快照是它的 JSON 文本，由 parseRecommendation 判不渲染', () => {
    setRecoFromCustomEvent({ message: '无 products 字段' })
    expect(getRecoSnapshot()).toBe('{"message":"无 products 字段"}')
  })
})

describe('CUSTOM value 不套多编码解码（R7 第 2 段）', () => {
  it('以引号开头的字符串 value → 原文不变（未被剥掉一层）', () => {
    // 宿主把工具结果字符串又序列化一次的形态：JSON.stringify(JSON 文本) = 带外层引号的 JSON 文本。
    const inner = '{"products":[],"message":"D"}'
    const doubleEncoded = JSON.stringify(inner)
    expect(doubleEncoded.startsWith('"')).toBe(true)

    setRecoFromCustomEvent(doubleEncoded)

    // 原文逐字保留 ⇒ 再 parse 一次得到的是**内层字符串**，而不是内层对象。
    expect(getRecoSnapshot()).toBe(doubleEncoded)
    expect(JSON.parse(getRecoSnapshot() as string)).toBe(inner)
    // 反证锚点：若本模块误套解码，快照会变成内层文本 —— 与上面两条断言互斥。
    expect(decodeToolResultContent(doubleEncoded)).toBe(inner)
  })

  it('对象型 value 不会被「解码」改形（JSON.stringify 结果以 { 开头，解码对它是 no-op）', () => {
    setRecoFromCustomEvent(RECO_B)
    const snapshot = getRecoSnapshot() as string
    expect(snapshot.startsWith('{')).toBe(true)
    expect(decodeToolResultContent(snapshot)).toBe(snapshot)
  })
})

describe('到达顺序覆盖（无优先级规则，R9-1）', () => {
  it('工具结果先到、CUSTOM 后到 → 快照为 CUSTOM', () => {
    setRecoFromToolResult(TOOL_A)
    setRecoFromCustomEvent(RECO_B)
    expect(getRecoSnapshot()).toBe(JSON.stringify(RECO_B))
  })

  it('CUSTOM 先到、工具结果后到 → 快照为工具结果（反向顺序也以后到者胜）', () => {
    setRecoFromCustomEvent(RECO_B)
    setRecoFromToolResult(TOOL_A)
    expect(getRecoSnapshot()).toBe(TOOL_A)
  })
})

describe('订阅语义', () => {
  it('每次有效写入通知监听器恰好一次', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeReco(listener)

    setRecoFromToolResult(TOOL_A)
    expect(listener).toHaveBeenCalledTimes(1)

    setRecoFromCustomEvent(RECO_B)
    expect(listener).toHaveBeenCalledTimes(2)

    resetRecoContent()
    expect(listener).toHaveBeenCalledTimes(3)

    unsubscribe()
  })

  it('退订后不再收到通知，且退订函数可重复调用', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeReco(listener)

    unsubscribe()
    unsubscribe()

    setRecoFromCustomEvent(RECO_B)
    expect(listener).not.toHaveBeenCalled()
  })

  it('resetRecoContent() 丢弃内容（快照为 null），传初值则以该值为初值', () => {
    setRecoFromCustomEvent(RECO_B)

    resetRecoContent()
    expect(getRecoSnapshot()).toBeNull()

    resetRecoContent(TOOL_A)
    expect(getRecoSnapshot()).toBe(TOOL_A)
  })

  it('useRecoContent 随写入更新（useSyncExternalStore 的快照比较成立）', () => {
    const { result } = renderHook(() => useRecoContent())
    expect(result.current).toBeNull()

    act(() => {
      setRecoFromCustomEvent(RECO_B)
    })
    expect(result.current).toBe(JSON.stringify(RECO_B))

    act(() => {
      resetRecoContent()
    })
    expect(result.current).toBeNull()
  })
})

/**
 * T18：store 与持久化对**任意文本**的边界。
 *
 * 盘点 T18 原文：「`reco.ts` store 允许任意文本（`'null'`/`'42'`），而 `readReco` 要求合法 JSON
 * → 一次无害告警 + 刷新回占位」—— 这条**跨两个模块**（store 写入 ↔ session 读出）的边界无用例。
 *
 * 行为本身是**对的**（容错、不崩），但此前没人钉住它：store 侧「字符串型 value 原样保留」是
 * 既有口径（`CUSTOM` 的 value 可能是合法 JSON 文本，解析再序列化会破坏逐字节可逆），
 * 而 session 侧 `readReco` 要求**合法 JSON** —— 两侧规则不同，交界处必须明确降级而非崩溃。
 */
describe('T18：任意文本进出 store 与持久化的边界', () => {
  it('非 JSON 文本写入 store → 原样保留；持久化后读出降级为 null（不崩）', () => {
    // 一个用户名只属于本用例，避免与其它用例的 localStorage 串味
    const username = 't18-user'

    // store 侧：字符串型 value 原样保留（不解析、不序列化）
    setRecoFromCustomEvent('这不是 JSON')
    expect(getRecoSnapshot()).toBe('这不是 JSON')

    // 持久化边界：原样写进去
    writeReco(username, getRecoSnapshot() ?? '')
    expect(globalThis.localStorage.getItem(`agui.reco.${username}`)).toBe('这不是 JSON')

    // 读出侧：readReco 要求合法 JSON → 解析失败 → 降级为 null（面板回占位），**不抛**
    expect(readReco(username)).toBeNull()
  })
})
