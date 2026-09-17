/**
 * 商品模态 + 详情用例（tasks.md C9「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` R11（商品目录来自 `GET /products`）与 R13-1（加购入口形态）：
 * - R11-1 数据来自响应：项数 / 顺序 / 字段；
 * - R11-2 不内置镜像的**行为面**：渲染项数随替身响应变化；
 * - R11-4 不渲染响应中不存在的字段（替身刻意多带一个服务端不会返回的 `desc`）；
 * - R11-5 拉取失败 → 错误态 + 重试入口（含重试真的能恢复）；
 * - R11-6 / R11-7 / R11-8 / R11-10 / R11-11 搜索 / 类别 / 排序 / 空态 / 不残留；
 * - R11-9 点缩略图或名称打开详情并展示商品字段；
 * - R4 末段：`/products` 的 404 上报上层做统一分派（本组件只渲染错误态）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { clearProductsCache } from '../api/products'
import { writeUsername } from '../state/session'
import { installFetchStub, jsonResponse } from '../test/fetch-stub'
import type { RouteSpec } from '../test/fetch-stub'
import { render, screen, userEvent, within } from '../test/render'
import ProductModal from './ProductModal'

/**
 * 替身商品数据（**测试夹具**）。
 *
 * 刻意多带一个服务端不会返回的 `desc` 字段：若实现渲染了商品简介（响应中不存在的字段），
 * R11-4 的断言会立刻变红。
 */
const FIXTURE = [
  {
    id: 1,
    name: '专业跑鞋',
    category: '鞋类',
    tags: ['跑步', '训练'],
    price: 129.99,
    emoji: '👟',
    desc: '轻量缓震中底，适合日常训练与长距离慢跑。',
  },
  {
    id: 2,
    name: '轻量跑鞋',
    category: '鞋类',
    tags: ['跑步', '轻量'],
    price: 199,
    emoji: '👟',
    desc: '超轻网面，透气性好，适合夏季与速度训练。',
  },
  {
    id: 3,
    name: '无线降噪耳机',
    category: '配饰',
    tags: ['音频'],
    price: 249.99,
    emoji: '🎧',
    desc: '主动降噪，续航 30 小时。',
  },
  {
    id: 4,
    name: '速干跑步背心',
    category: '服装',
    tags: ['跑步'],
    price: 159,
    emoji: '🎽',
    desc: '单向导湿面料，反光条设计。',
  },
]

/** 响应中不存在的字段值（任何一处被渲染出来都算违背 R11-4）。 */
const DESCRIPTION_TEXTS = FIXTURE.map((product) => product.desc)

const ALL_NAMES = ['专业跑鞋', '轻量跑鞋', '无线降噪耳机', '速干跑步背心']

let restoreFetch: (() => void) | null = null

beforeEach(() => {
  localStorage.clear()
  // `apiFetch` 无条件追加 `?username=`，未选定账户会 fail-fast（见 handoff-C3 遗留 4）。
  writeUsername('marla')
  clearProductsCache()
})

afterEach(() => {
  restoreFetch?.()
  restoreFetch = null
  clearProductsCache()
})

function install(routes: Record<string, RouteSpec>): ReturnType<typeof installFetchStub> {
  // 同一用例里换替身时先把上一个还原（否则 restore 链会指回上一个 stub）。
  restoreFetch?.()
  const stub = installFetchStub(routes)
  restoreFetch = stub.restore
  return stub
}

function renderModal(options: { open?: boolean } = {}) {
  const onClose = vi.fn()
  const onAdd = vi.fn()
  const onFailure = vi.fn()
  const view = render(
    <ProductModal
      open={options.open ?? true}
      onClose={onClose}
      onAdd={onAdd}
      onFailure={onFailure}
    />,
  )
  return { ...view, onClose, onAdd, onFailure }
}

/** 网格里的商品名顺序（= 当前渲染顺序）。 */
function cardNames(container: HTMLElement): string[] {
  return [...container.querySelectorAll('.pcard .pname')].map((element) => element.textContent ?? '')
}

describe('商品网格（R11-1 / R11-2 / R11-3 的组件面）', () => {
  it('数据来自响应：项数 / 字段 / 计数，且不渲染响应里没有的简介', async () => {
    const stub = install({ '/products': FIXTURE })
    const { container } = renderModal()

    await screen.findByText('专业跑鞋')

    expect(cardNames(container)).toEqual(ALL_NAMES)
    expect(screen.getByText('共 4 件')).toBeDefined()
    expect(container.querySelector('.pcard .pprice')?.textContent).toBe('¥129.99')
    expect(container.querySelector('.pcard .pthumb')?.textContent).toBe('👟')
    expect(stub.callsTo('/products')).toHaveLength(1)
    for (const description of DESCRIPTION_TEXTS) {
      expect(screen.queryByText(description)).toBeNull()
    }
  })

  it('默认顺序 = id 升序（响应顺序被打乱也一致）', async () => {
    install({ '/products': [FIXTURE[2], FIXTURE[0], FIXTURE[3], FIXTURE[1]] })
    const { container } = renderModal()

    await screen.findByText('专业跑鞋')

    expect(cardNames(container)).toEqual(ALL_NAMES)
  })

  it('R11-2 反证的行为面：渲染项数随响应项数变化（不是源码内硬编码清单）', async () => {
    install({ '/products': FIXTURE })
    const first = renderModal()
    await screen.findByText('专业跑鞋')
    expect(first.container.querySelectorAll('.pcard')).toHaveLength(4)
    first.unmount()

    clearProductsCache()
    install({ '/products': FIXTURE.slice(0, 2) })
    const second = renderModal()
    await screen.findByText('专业跑鞋')

    expect(second.container.querySelectorAll('.pcard')).toHaveLength(2)
    expect(screen.queryByText('无线降噪耳机')).toBeNull()
    expect(screen.getByText('共 2 件')).toBeDefined()
  })

  it('未打开时不渲染内容、也不发起请求', () => {
    const stub = install({ '/products': FIXTURE })
    const { container } = renderModal({ open: false })

    expect(container.querySelector('.mbox')).toBeNull()
    expect(container.textContent).toBe('')
    expect(stub.callsTo('/products')).toHaveLength(0)
  })
})

describe('搜索 / 类别 / 排序（R11-6 / R11-7 / R11-8）', () => {
  it('搜索按响应文本字段过滤，计数同步', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    await userEvent.type(screen.getByLabelText('搜索商品'), '跑鞋')

    expect(cardNames(container)).toEqual(['专业跑鞋', '轻量跑鞋'])
    expect(screen.getByText('共 2 件')).toBeDefined()

    await userEvent.clear(screen.getByLabelText('搜索商品'))
    await userEvent.type(screen.getByLabelText('搜索商品'), '音频')

    // 标签也是响应字段：命中「音频」的只有耳机。
    expect(cardNames(container)).toEqual(['无线降噪耳机'])
  })

  it('类别 chips 由响应 category 去重生成，点「全部」恢复', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    expect([...container.querySelectorAll('.cat')].map((el) => el.textContent)).toEqual([
      '全部',
      '鞋类',
      '配饰',
      '服装',
    ])

    await userEvent.click(screen.getByRole('button', { name: '配饰' }))
    expect(cardNames(container)).toEqual(['无线降噪耳机'])

    await userEvent.click(screen.getByRole('button', { name: '全部' }))
    expect(cardNames(container)).toEqual(ALL_NAMES)
  })

  it('价格升序 / 降序，切回默认恢复 id 升序', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    await userEvent.selectOptions(screen.getByLabelText('排序'), 'price_asc')
    expect(cardNames(container)).toEqual([
      '专业跑鞋',
      '速干跑步背心',
      '轻量跑鞋',
      '无线降噪耳机',
    ])

    await userEvent.selectOptions(screen.getByLabelText('排序'), 'price_desc')
    expect(cardNames(container)).toEqual([
      '无线降噪耳机',
      '轻量跑鞋',
      '速干跑步背心',
      '专业跑鞋',
    ])

    await userEvent.selectOptions(screen.getByLabelText('排序'), 'default')
    expect(cardNames(container)).toEqual(ALL_NAMES)
  })

  it('搜索 + 类别 + 排序可叠加', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    await userEvent.type(screen.getByLabelText('搜索商品'), '跑')
    await userEvent.click(screen.getByRole('button', { name: '鞋类' }))
    await userEvent.selectOptions(screen.getByLabelText('排序'), 'price_desc')

    expect(cardNames(container)).toEqual(['轻量跑鞋', '专业跑鞋'])
  })

  it('R11-10 / R11-11 无匹配 → 空态 + 计数 0 + 上一次的商品卡被整体清除', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    await userEvent.click(screen.getByRole('button', { name: '鞋类' }))
    expect(container.querySelectorAll('.pcard')).toHaveLength(2)

    await userEvent.type(screen.getByLabelText('搜索商品'), 'zzz')

    expect(container.querySelectorAll('.pcard')).toHaveLength(0)
    expect(screen.getByText('没有匹配的商品')).toBeDefined()
    expect(screen.getByText('共 0 件')).toBeDefined()
    expect(screen.queryByText('专业跑鞋')).toBeNull()
  })
})

describe('商品详情（R11-9 / R11-4）', () => {
  it('点名称打开详情并展示名称 / 价格 / 类别 / 图标 / 标签，不渲染简介段落', async () => {
    install({ '/products': FIXTURE })
    const { container, onAdd } = renderModal()
    await screen.findByText('专业跑鞋')

    await userEvent.click(screen.getByRole('button', { name: '专业跑鞋' }))

    const detail = container.querySelector<HTMLElement>('.dbox')
    expect(detail).not.toBeNull()
    const view = within(detail as HTMLElement)

    expect(view.getByText('专业跑鞋')).toBeDefined()
    expect(view.getByText('¥129.99')).toBeDefined()
    expect(view.getByText(/鞋类/)).toBeDefined()
    expect(view.getByText('👟')).toBeDefined()
    expect(view.getByText('跑步')).toBeDefined()
    expect(view.getByText('训练')).toBeDefined()

    for (const description of DESCRIPTION_TEXTS) {
      expect(view.queryByText(description)).toBeNull()
    }
    expect((detail as HTMLElement).querySelector('.dsc')).toBeNull()

    // 详情里的加购入口与商品卡同一条路径，点击后详情关闭（对齐原型 v3）。
    await userEvent.click(view.getByTitle('加入购物车'))
    expect(onAdd).toHaveBeenCalledWith(1)
    expect(container.querySelector('.dbox')).toBeNull()
  })

  it('点缩略图同样打开详情', async () => {
    install({ '/products': FIXTURE })
    const { container } = renderModal()
    await screen.findByText('专业跑鞋')

    const thumbs = container.querySelectorAll<HTMLButtonElement>('.pcard .pthumb')
    await userEvent.click(thumbs[2] as HTMLButtonElement)

    const view = within(container.querySelector('.dbox') as HTMLElement)
    expect(view.getByText('无线降噪耳机')).toBeDefined()
    expect(view.getByText('¥249.99')).toBeDefined()
    expect(view.getByText(/配饰/)).toBeDefined()
  })
})

describe('拉取失败（R11-5 + R4 末段的上报接缝）', () => {
  it('500 → 错误态 + 重试入口；重试成功后回到清单', async () => {
    let attempt = 0
    install({
      '/products': () => {
        attempt += 1
        return attempt === 1 ? jsonResponse({ detail: 'boom' }, 500) : jsonResponse(FIXTURE)
      },
    })
    const { container, onFailure } = renderModal()

    const alert = await screen.findByRole('alert')
    expect(within(alert).getByText('商品加载失败')).toBeDefined()
    expect(container.querySelectorAll('.pcard')).toHaveLength(0)
    for (const name of ALL_NAMES) {
      expect(screen.queryByText(name)).toBeNull()
    }
    expect(onFailure).toHaveBeenCalledTimes(1)
    expect((onFailure.mock.calls[0]?.[0] as { status?: number }).status).toBe(500)

    await userEvent.click(screen.getByRole('button', { name: '重试' }))

    await screen.findByText('专业跑鞋')
    expect(container.querySelectorAll('.pcard')).toHaveLength(4)
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('网络失败 → 错误态 + 重试入口，且不展示任何本地商品', async () => {
    install({
      '/products': () => {
        throw new TypeError('Failed to fetch')
      },
    })
    const { container } = renderModal()

    await screen.findByRole('alert')

    expect(container.querySelectorAll('.pcard')).toHaveLength(0)
    for (const name of ALL_NAMES) {
      expect(screen.queryByText(name)).toBeNull()
    }
    expect(screen.getByRole('button', { name: '重试' })).toBeDefined()
  })

  it('404 User not found → 上报给上层做统一分派（R4 末段，不在此自行吞掉）', async () => {
    install({ '/products': () => jsonResponse({ detail: 'User not found' }, 404) })
    const { onFailure } = renderModal()

    await screen.findByRole('alert')

    expect(onFailure).toHaveBeenCalledTimes(1)
    const reported = onFailure.mock.calls[0]?.[0] as { status?: number; payload?: unknown }
    expect(reported.status).toBe(404)
    expect(reported.payload).toEqual({ detail: 'User not found' })
  })
})

describe('加购入口形态（R13-1）', () => {
  it('商品卡加购 = 透明底 🛒 图标按钮（无文字标签），点击回传 productId', async () => {
    install({ '/products': FIXTURE })
    const { container, onAdd } = renderModal()
    await screen.findByText('专业跑鞋')

    const addButtons = [...container.querySelectorAll<HTMLButtonElement>('.pcard .addbtn')]

    expect(addButtons).toHaveLength(4)
    for (const button of addButtons) {
      expect(button.textContent).toBe('🛒')
    }

    await userEvent.click(addButtons[1] as HTMLButtonElement)
    expect(onAdd).toHaveBeenCalledWith(2)
  })
})
