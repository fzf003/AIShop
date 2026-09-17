/**
 * 渲染 helper：`@testing-library/react` 的薄封装 + 每个用例后自动 `cleanup`。
 *
 * 本变更所有组件用例都从这里导入 `render` / `screen` / `userEvent`，不直接引 testing-library
 * —— 让 `cleanup` 与 jsdom 环境约定只存在一处（见 rules/development-flow.md「测试资源清理」）。
 */
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(() => {
  cleanup()
})

export { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
export { default as userEvent } from '@testing-library/user-event'
