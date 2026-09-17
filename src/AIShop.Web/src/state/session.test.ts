/**
 * 会话持久化用例（tasks.md C2「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` 的：
 * - R2 场景 1「不同账户互不覆盖」（+ 切回后历史完整）
 * - R2 场景 2「持久化数据损坏时安全降级」（非法 JSON / 非数组结构，各一次）
 * - R2 场景 3「工具调用消息被完整保留」（toolCalls 与配对 tool 消息同时存回，不成孤儿）
 * - R2 第 3 段 + R12 第 3 段「退出只清当前账户的本地数据」
 * - R12 第 1 段「threadId 本地生成并持久化」
 *
 * 每条断言都是对具体输入输出关系的验证；`console.warn` 的「告警」用 spy 观测。
 */
import type { Message, ToolMessage } from '@ag-ui/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  clearModelId,
  clearSession,
  clearUsername,
  ensureThreadId,
  readMessages,
  readModelId,
  readThreadId,
  readUsername,
  writeMessages,
  writeModelId,
  writeUsername,
} from './session'

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

const MARLA_HISTORY: Message[] = [
  { id: 'm-u1', role: 'user', content: '你好' },
  { id: 'm-a1', role: 'assistant', content: '你好，我是 AIShop 购物助手' },
]

let warnSpy: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  localStorage.clear()
  warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
})

afterEach(() => {
  warnSpy.mockRestore()
})

describe('键控与隔离（R2 场景 1）', () => {
  it('marla 写入消息后切 steve，各自键互不覆盖；切回 marla 历史仍完整', () => {
    const marlaThread = ensureThreadId('marla')
    writeMessages('marla', MARLA_HISTORY)
    writeUsername('marla')

    // 切到 steve：读到的是空历史（不是 marla 的），且拿到自己的 threadId
    const steveThread = ensureThreadId('steve')
    expect(readMessages('steve')).toEqual([])
    expect(steveThread).not.toBe(marlaThread)

    // steve 自己产生一段对话 —— 不得覆盖 marla 的键
    const steveHistory: Message[] = [{ id: 's-u1', role: 'user', content: '有什么推荐' }]
    writeMessages('steve', steveHistory)

    expect(localStorage.getItem('agui.messages.marla')).toBe(JSON.stringify(MARLA_HISTORY))
    expect(localStorage.getItem('agui.messages.steve')).toBe(JSON.stringify(steveHistory))

    // 切回 marla：历史与 threadId 都还在
    expect(readMessages('marla')).toEqual(MARLA_HISTORY)
    expect(readThreadId('marla')).toBe(marlaThread)
    expect(readThreadId('steve')).toBe(steveThread)
    expect(readUsername()).toBe('marla')
  })

  it('threadId 首次生成且持久化，重复取用不重新生成（R12 第 1 段）', () => {
    const first = ensureThreadId('marla')

    expect(first).toMatch(UUID_PATTERN)
    expect(localStorage.getItem('agui.threadId.marla')).toBe(first)
    expect(ensureThreadId('marla')).toBe(first)

    // 不同账户的 threadId 互不相同、互不覆盖
    expect(ensureThreadId('steve')).not.toBe(first)
    expect(localStorage.getItem('agui.threadId.marla')).toBe(first)
  })

  it('未写过的账户读到空历史且**不产生告警**（无历史不是异常）', () => {
    expect(readMessages('nobody')).toEqual([])
    expect(readThreadId('nobody')).toBeNull()
    expect(readModelId()).toBeNull()
    expect(readUsername()).toBeNull()
    expect(warnSpy).not.toHaveBeenCalled()
  })
})

describe('容错：损坏数据安全降级（R2 场景 2）', () => {
  it('非法 JSON → 返回空历史、不抛异常、恰好一条告警', () => {
    localStorage.setItem('agui.messages.marla', '{这不是 JSON')

    let result: Message[] | undefined
    expect(() => {
      result = readMessages('marla')
    }).not.toThrow()

    expect(result).toEqual([])
    expect(warnSpy).toHaveBeenCalledTimes(1)
    expect(warnSpy.mock.calls[0]?.[0]).toContain('agui.messages.marla')
  })

  it('非数组结构（对象）→ 返回空历史、不抛异常、恰好一条告警', () => {
    localStorage.setItem('agui.messages.marla', JSON.stringify({ messages: [] }))

    let result: Message[] | undefined
    expect(() => {
      result = readMessages('marla')
    }).not.toThrow()

    expect(result).toEqual([])
    expect(warnSpy).toHaveBeenCalledTimes(1)
    expect(warnSpy.mock.calls[0]?.[0]).toContain('结构非法')
  })

  it('存储读取抛异常（localStorage 不可用 / 被禁用）→ 空历史 + 告警，不抛异常', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError: localStorage 不可用')
    })

    try {
      expect(readMessages('marla')).toEqual([])
      expect(warnSpy).toHaveBeenCalled()
    } finally {
      getItem.mockRestore()
    }
  })

  it('存储写入抛异常（配额溢出）→ 不抛异常，只告警（当前会话可继续）', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })

    try {
      expect(() => writeMessages('marla', MARLA_HISTORY)).not.toThrow()
      expect(warnSpy).toHaveBeenCalledTimes(1)
      expect(warnSpy.mock.calls[0]?.[0]).toContain('写入')
    } finally {
      setItem.mockRestore()
    }
  })
})

describe('完整性：工具调用消息整体存回（R2 场景 3）', () => {
  it('toolCalls 与配对的 tool 结果消息同时存在，不出现孤儿消息', () => {
    const history: Message[] = [
      { id: 'u-1', role: 'user', content: '帮我把跑鞋加进购物车' },
      {
        id: 'a-1',
        role: 'assistant',
        content: '好的，正在为您加入',
        toolCalls: [
          {
            id: 'call-1',
            type: 'function',
            function: { name: 'add_to_cart', arguments: '{"productId":3,"quantity":1}' },
          },
        ],
      },
      { id: 't-1', role: 'tool', content: '已加入购物车：专业跑鞋', toolCallId: 'call-1' },
    ]

    writeMessages('marla', history)
    const restored = readMessages('marla')

    // 整体往返（含 toolCalls 与 tool 消息），没有被裁成纯文本
    expect(restored).toEqual(history)
    expect(restored).toHaveLength(3)

    const toolCallIds = restored.flatMap((message) =>
      message.role === 'assistant' ? (message.toolCalls?.map((toolCall) => toolCall.id) ?? []) : [],
    )
    const toolResultIds = restored
      .filter((message): message is ToolMessage => message.role === 'tool')
      .map((message) => message.toolCallId)

    expect(toolCallIds).toEqual(['call-1'])
    expect(toolResultIds).toEqual(toolCallIds)
  })
})

describe('退出只清当前账户的本地数据（R2 第 3 段 + R12 第 3 段）', () => {
  it('clearSession("marla") 后 steve 的 messages/threadId 键仍在，其它全局键保留', () => {
    ensureThreadId('marla')
    writeMessages('marla', MARLA_HISTORY)

    const steveThread = ensureThreadId('steve')
    const steveHistory: Message[] = [{ id: 's-u1', role: 'user', content: '嘿嘿' }]
    writeMessages('steve', steveHistory)

    writeModelId('gpt-4.1')
    writeUsername('marla')

    clearSession('marla')

    // 当前账户的两个键被删
    expect(readMessages('marla')).toEqual([])
    expect(readThreadId('marla')).toBeNull()
    expect(localStorage.getItem('agui.messages.marla')).toBeNull()
    expect(localStorage.getItem('agui.threadId.marla')).toBeNull()

    // 其它账户原样保留
    expect(readMessages('steve')).toEqual(steveHistory)
    expect(readThreadId('steve')).toBe(steveThread)

    // 与「账户会话」无关的全局选择态不被顺手清掉
    expect(readModelId()).toBe('gpt-4.1')
    expect(readUsername()).toBe('marla')
    expect(warnSpy).not.toHaveBeenCalled()
  })

  it('全局键有独立的写 / 读 / 删语义（agui.username / agui.model）', () => {
    writeUsername('fzf003')
    writeModelId('deepseek')
    expect(readUsername()).toBe('fzf003')
    expect(readModelId()).toBe('deepseek')

    clearUsername()
    clearModelId()
    expect(readUsername()).toBeNull()
    expect(readModelId()).toBeNull()
  })
})
