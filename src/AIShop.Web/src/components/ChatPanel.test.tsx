/**
 * 聊天面板用例（tasks.md C11 的组件级验收）。
 *
 * 覆盖 spec `agui-client`：
 * - R7-1 增量渲染（组件只渲染 `messages` 原样快照，实现「首增即出气泡随增量增长」）；
 * - R7-2 运行中发送入口禁用、本轮结束后恢复；
 * - R17-1 空列表 → 消息区**顶部**渲染欢迎语胶囊，且样式为「弱文本色 `#8a8aa3` 文字 + `--bg` 底色 +
 *   圆角 20px」（jsdom 不应用外部样式表，故样式部分读 `ChatPanel.css` 与 `tokens.css` 的原文断言）；
 * - R17-2 首条用户消息入列的瞬间欢迎语消失；
 * - R8/§9.6 工具胶囊插在 assistant 消息的位置（`findToolCall` 注入，不注入则跳过而不抛错）。
 */
import type { AssistantMessage, Message, ToolMessage, UserMessage } from '@ag-ui/client'
import { describe, expect, it, vi } from 'vitest'

import type { ToolCallEntry } from '../agui/tools'
import { render, screen, userEvent } from '../test/render'
import ChatPanel from './ChatPanel'
import chatPanelCss from './ChatPanel.css?raw'
import tokensCss from '../styles/tokens.css?raw'

const WELCOME_TEXT = '👋 开始聊天，告诉我您的喜好'

const USER: UserMessage = { id: 'u-1', role: 'user', content: '帮我找双跑鞋' }
const ASSISTANT: AssistantMessage = { id: 'a-1', role: 'assistant', content: '为您找到 3 款跑鞋' }
const TOOL_RESULT: ToolMessage = {
  id: 't-1',
  role: 'tool',
  toolCallId: 'call-1',
  content: '找到 3 个商品',
}
const ASSISTANT_WITH_CALL: AssistantMessage = {
  id: 'a-2',
  role: 'assistant',
  content: '',
  toolCalls: [
    {
      id: 'call-1',
      type: 'function',
      function: { name: 'search_product', arguments: '{"query":"跑鞋"}' },
    },
  ],
}

const SEARCH_ENTRY: ToolCallEntry = {
  tool: {
    toolCallId: 'call-1',
    name: 'search_product',
    argsText: '{"query":"跑鞋"}',
    args: { query: '跑鞋' },
    result: '找到 3 个商品',
    status: 'success',
    durationMs: 320,
  },
  usage: null,
}

/** 取出某条选择器的规则体（`selector { ... }`），找不到即失败（防「断言空转」）。 */
function ruleBody(css: string, selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const match = new RegExp(`${escaped}\\s*\\{([^}]*)\\}`).exec(css)
  if (match === null) throw new Error(`样式里没有 ${selector} 规则`)
  return match[1]
}

describe('空对话欢迎语（R17）', () => {
  it('空列表 → 消息区顶部渲染欢迎语胶囊，样式为弱文本色 + 页面底色 + 圆角 20px（R17-1）', () => {
    const { container } = render(
      <ChatPanel messages={[]} isRunning={false} onSend={vi.fn()} />,
    )

    const msgs = container.querySelector('.msgs')
    const welcome = container.querySelector('.welcome')
    expect(welcome).not.toBeNull()
    expect(welcome?.textContent).toBe(WELCOME_TEXT)
    // 「位于消息区顶部（首条消息之前）」= 消息区容器的**首个**子元素就是它
    expect(msgs?.firstElementChild).toBe(welcome)

    // 样式：jsdom 不应用外部样式表，断言 CSS 原文（`.welcome` 规则 + 令牌映射）。
    const rule = ruleBody(chatPanelCss, '.welcome')
    expect(rule).toContain('align-self: center')
    expect(rule).toContain('var(--ink-3)')
    expect(rule).toContain('var(--bg)')
    expect(rule).toContain('border-radius: 20px')
    expect(rule).toContain('padding: 9px 20px')
    expect(rule).toContain('font-size: 0.88rem')
    // 令牌映射：`--ink-3` = `#8a8aa3`、`--bg` = `#f6f7fb`（非白卡底 `#fff`）
    expect(tokensCss).toContain('--ink-3: #8a8aa3;')
    expect(tokensCss).toContain('--bg: #f6f7fb;')
  })

  it('首条用户消息入列后欢迎语立即消失（R17-2）', () => {
    const { container, rerender } = render(
      <ChatPanel messages={[]} isRunning={false} onSend={vi.fn()} />,
    )
    expect(container.querySelector('.welcome')).not.toBeNull()

    rerender(<ChatPanel messages={[USER]} isRunning={false} onSend={vi.fn()} />)

    expect(container.querySelector('.welcome')).toBeNull()
    expect(container.querySelector('.row.u .bub')?.textContent).toBe('帮我找双跑鞋')
  })

  it('欢迎语不是消息：不占序号，首条用户消息仍是列表首项（R17-4 的视图侧）', () => {
    const { container } = render(
      <ChatPanel messages={[USER]} isRunning={false} onSend={vi.fn()} />,
    )
    const msgs = container.querySelector('.msgs')
    // 首个子元素是用户消息气泡所在的行，而不是欢迎语
    expect(msgs?.firstElementChild?.className).toBe('row u')
  })
})

describe('消息渲染与工具胶囊', () => {
  it('用户消息右气泡、assistant 左气泡；tool 结果消息不单独出气泡', () => {
    const { container } = render(
      <ChatPanel
        messages={[USER, ASSISTANT, TOOL_RESULT] as Message[]}
        isRunning={false}
        onSend={vi.fn()}
      />,
    )

    expect(container.querySelector('.row.u .bub')?.textContent).toBe('帮我找双跑鞋')
    expect(container.querySelector('.row.a .bub')?.textContent).toBe('为您找到 3 款跑鞋')
    // tool 结果由胶囊承载，不重复渲染成第三条气泡
    expect(container.querySelectorAll('.row')).toHaveLength(2)
    expect(screen.queryByText('找到 3 个商品')).toBeNull()
  })

  it('assistant 的 toolCalls 经 findToolCall 渲染为胶囊；未注入时跳过而不抛错', () => {
    const findToolCall = vi.fn((id: string) => (id === 'call-1' ? SEARCH_ENTRY : null))
    const { container, rerender } = render(
      <ChatPanel
        messages={[ASSISTANT_WITH_CALL]}
        isRunning={false}
        findToolCall={findToolCall}
        onSend={vi.fn()}
      />,
    )

    expect(findToolCall).toHaveBeenCalledWith('call-1')
    const chip = container.querySelector('.tool')
    expect(chip).not.toBeNull()
    expect(chip?.querySelector('.nm')?.textContent).toBe('search_product')
    // 折叠态：展开体不在 DOM 里（C7 的条件渲染约定）
    expect(container.querySelector('.tool-body')).toBeNull()

    rerender(<ChatPanel messages={[ASSISTANT_WITH_CALL]} isRunning={false} onSend={vi.fn()} />)
    expect(container.querySelector('.tool')).toBeNull()
  })

  it('流式光标只出现在运行中的末条 assistant 气泡上', () => {
    const { container, rerender } = render(
      <ChatPanel messages={[ASSISTANT]} isRunning={true} onSend={vi.fn()} />,
    )
    expect(container.querySelector('.row.a .bub')?.className).toBe('bub cur')

    rerender(<ChatPanel messages={[ASSISTANT]} isRunning={false} onSend={vi.fn()} />)
    expect(container.querySelector('.row.a .bub')?.className).toBe('bub')
  })
})

describe('输入区（R7-2）', () => {
  it('运行中发送入口禁用，本轮结束后恢复可用', () => {
    const onSend = vi.fn()
    const { rerender } = render(<ChatPanel messages={[]} isRunning={true} onSend={onSend} />)

    const running = screen.getByRole('button', { name: '发送' }) as HTMLButtonElement
    expect(running.disabled).toBe(true)

    rerender(<ChatPanel messages={[]} isRunning={false} onSend={onSend} />)
    const idle = screen.getByRole('button', { name: '发送' }) as HTMLButtonElement
    expect(idle.disabled).toBe(false)
  })

  it('运行中即便点发送也不派发（防空轮次）', async () => {
    const onSend = vi.fn()
    render(<ChatPanel messages={[USER]} isRunning={true} onSend={onSend} />)

    await userEvent.type(screen.getByLabelText('消息'), '再来一双')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))

    expect(onSend).not.toHaveBeenCalled()
  })

  it('点击发送或回车 → 派发去除首尾空白的文本并清空输入框', async () => {
    const onSend = vi.fn()
    render(<ChatPanel messages={[USER]} isRunning={false} onSend={onSend} />)

    const input = screen.getByLabelText('消息')
    await userEvent.type(input, '  再来一双  ')
    await userEvent.click(screen.getByRole('button', { name: '发送' }))

    expect(onSend).toHaveBeenCalledWith('再来一双')
    expect((input as HTMLInputElement).value).toBe('')

    await userEvent.type(input, '还有别的吗{Enter}')
    expect(onSend).toHaveBeenLastCalledWith('还有别的吗')
    expect((input as HTMLInputElement).value).toBe('')
  })

  it('空输入不派发', async () => {
    const onSend = vi.fn()
    render(<ChatPanel messages={[USER]} isRunning={false} onSend={onSend} />)

    await userEvent.type(screen.getByLabelText('消息'), '   {Enter}')

    expect(onSend).not.toHaveBeenCalled()
  })
})
