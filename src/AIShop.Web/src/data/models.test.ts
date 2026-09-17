/**
 * 模型清单模块用例（spec R6：来源、顺序、初始选中口径、失败不静默兜底、id 回传）。
 *
 * 断言对象是**行为**（响应驱动 / 回落规则），不是实现细节；R6-3 的反证见 handoff-C6。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { clearModelId, readModelId, writeModelId } from '../state/session'
import { installFetchStub, jsonResponse } from '../test/fetch-stub'
import { currentModelId, fetchModels, modelEmoji, resolveModelId } from './models'

/**
 * 服务端实测口径的替身：顺序为响应顺序（`[deepseek, gpt-4.1, qwen]`），仅 `gpt-4.1` 为默认。
 *
 * 不标注为 `ModelInfo[]`：`fetch-stub` 的路由值类型是 `JsonValue`，而 `interface` 没有隐式索引签名，
 * 标注后会类型不兼容（`resolveModelId` / `ModelPicker` 的参数是结构兼容的，无需标注）。
 */
const THREE = [
  { id: 'deepseek', name: 'DeepSeek', model: 'deepseek-v4-flash', isDefault: false },
  { id: 'gpt-4.1', name: 'MiMo', model: 'mimo-v2.5', isDefault: true },
  { id: 'qwen', name: 'Qwen 3.7', model: 'qwen3.8-flash', isDefault: false },
]

const ALL_FALSE = THREE.map((model) => ({ ...model, isDefault: false }))

beforeEach(() => {
  localStorage.clear()
})

afterEach(() => {
  localStorage.clear()
})

describe('fetchModels', () => {
  it('返回响应数组本身（顺序 = 响应顺序，不排序、不补项）', async () => {
    const stub = installFetchStub({ '/models': THREE })
    try {
      const models = await fetchModels()

      expect(models).toEqual(THREE)
      expect(models.map((model) => model.id)).toEqual(['deepseek', 'gpt-4.1', 'qwen'])
      // 清单端点公开可读：不带身份参数（REST 面的 ?username= 只约束 /products 与 /cart*）
      expect(stub.callsTo('/models')[0]?.search).toBe('')
    } finally {
      stub.restore()
    }
  })

  it('非 2xx 抛错（不静默回落硬编码清单）', async () => {
    const stub = installFetchStub({ '/models': () => jsonResponse({ detail: 'boom' }, 500) })
    try {
      await expect(fetchModels()).rejects.toThrow('模型清单拉取失败（HTTP 500）')
    } finally {
      stub.restore()
    }
  })

  it('网络失败原样抛出', async () => {
    const stub = installFetchStub({
      '/models': () => {
        throw new TypeError('Failed to fetch')
      },
    })
    try {
      await expect(fetchModels()).rejects.toThrow('Failed to fetch')
    } finally {
      stub.restore()
    }
  })

  it('响应不是数组时抛错（不把非法结构当成空清单）', async () => {
    const stub = installFetchStub({ '/models': { models: THREE } })
    try {
      await expect(fetchModels()).rejects.toThrow('模型清单响应结构非法')
    } finally {
      stub.restore()
    }
  })
})

describe('resolveModelId', () => {
  it('无持久化值时选 isDefault 项（R6-1）', () => {
    expect(resolveModelId(THREE, null)).toBe('gpt-4.1')
  })

  it('全部非默认时选响应首项（R6-2；顺序取响应顺序而非排序）', () => {
    expect(resolveModelId(ALL_FALSE, null)).toBe('deepseek')
  })

  it('持久化 id 仍在清单中时保持它（刷新保持同一项，R6-6）', () => {
    expect(resolveModelId(THREE, 'qwen')).toBe('qwen')
  })

  it('持久化 id 已不在清单中时按 isDefault 回落（R6-7）', () => {
    expect(resolveModelId(THREE, 'old-model')).toBe('gpt-4.1')
  })

  it('持久化 id 失效且全部非默认时回落响应首项（R6-7）', () => {
    expect(resolveModelId(ALL_FALSE, 'old-model')).toBe('deepseek')
  })

  it('空清单返回 null（不猜 id、不抛异常）', () => {
    expect(resolveModelId([], null)).toBeNull()
    expect(resolveModelId([], 'qwen')).toBeNull()
  })
})

describe('modelEmoji', () => {
  it('已收录 id 用原型的图标', () => {
    expect(modelEmoji('qwen')).toBe('⚡')
    expect(modelEmoji('deepseek')).toBe('🐋')
    expect(modelEmoji('gpt-4.1')).toBe('🤖')
  })

  it('未知 id 用通用占位图标（不与其他项重名）', () => {
    const placeholder = modelEmoji('gpt-5')
    expect(placeholder.length).toBeGreaterThan(0)
    expect(['⚡', '🐋', '🤖']).not.toContain(placeholder)
  })
})

describe('currentModelId', () => {
  it('按调用时读取持久化值（下一轮请求的取值点，R6-5 的数据面）', () => {
    expect(currentModelId()).toBeNull()

    writeModelId('gpt-4.1')
    expect(currentModelId()).toBe('gpt-4.1')
    expect(readModelId()).toBe('gpt-4.1')

    writeModelId('qwen')
    expect(currentModelId()).toBe('qwen')

    clearModelId()
    expect(currentModelId()).toBeNull()
  })
})
