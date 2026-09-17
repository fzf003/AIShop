/**
 * 商品目录用例（tasks.md C9「验收 / 测试」）。
 *
 * 覆盖 spec `agui-client` R11：
 * - **R11-3「响应形状被契约测试锁定」**：18 项六字段类型正确、`id` 唯一、两次请求顺序一致；
 *   形状不符时**显式失败**（不静默跳过、不把坏响应渲染成空清单）；
 * - **R11-2「不内置镜像」的静态面**：非测试源码中不得出现商品字面量 / `ProductSeedData`；
 * - **R11-6 / R11-7 / R11-8**：搜索 / 类别 / 排序，三者可叠加；
 * - 缓存：一个页面会话内复用同一份数据（模态、详情与快捷加购 chips 共用）。
 *
 * 所有请求走 `src/test/fetch-stub.ts`（未注册路由会显式抛错，不会静默 404）。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { writeUsername } from '../state/session'
import { installFetchStub, jsonResponse } from '../test/fetch-stub'
import type { RouteSpec } from '../test/fetch-stub'
import {
  ALL_CATEGORIES,
  categoriesOf,
  clearProductsCache,
  formatPrice,
  getCachedProducts,
  loadProducts,
  parseProducts,
  selectProducts,
} from './products'
import type { Product } from './products'

/**
 * 契约测试的替身数据：18 项，六字段齐备（**纯测试夹具**，与客户端源码里的任何清单无关）。
 *
 * 刻意**不**标注 `: Product[]`：`Product` 是 interface，没有隐式索引签名，标注后就不再能直接
 * 当作 `fetch-stub` 的 `JsonValue` 路由值（`RouteSpec`）使用。字段类型由
 * `selectProducts(PRODUCTS_18)` 的入参校验兜住。
 */
const CATEGORY_CYCLE = ['鞋类', '配饰', '服装'] as const

const PRODUCTS_18 = Array.from({ length: 18 }, (_, index) => ({
  id: index + 1,
  name: `测试商品 ${index + 1}`,
  category: CATEGORY_CYCLE[index % CATEGORY_CYCLE.length],
  tags: [`标签${(index % 4) + 1}`],
  price: 100 + index * 10,
  emoji: '📦',
}))

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

describe('R11-3 契约：/products 响应形状（六字段 / 类型 / id 唯一 / 顺序可复现）', () => {
  it('18 项均含六字段且类型正确，id 唯一', async () => {
    install({ '/products': PRODUCTS_18 })

    const products = await loadProducts()

    expect(products).toHaveLength(18)
    for (const product of products) {
      expect(typeof product.id).toBe('number')
      expect(typeof product.name).toBe('string')
      expect(typeof product.category).toBe('string')
      expect(Array.isArray(product.tags)).toBe(true)
      expect(product.tags.every((tag) => typeof tag === 'string')).toBe(true)
      expect(typeof product.price).toBe('number')
      expect(typeof product.emoji).toBe('string')
    }
    expect(new Set(products.map((product) => product.id)).size).toBe(18)
  })

  it('两次请求返回的元素顺序一致（响应顺序稳定，客户端不重排）', async () => {
    const stub = install({ '/products': PRODUCTS_18 })

    const first = await loadProducts({ force: true })
    const second = await loadProducts({ force: true })

    expect(first.map((product) => product.id)).toEqual(PRODUCTS_18.map((product) => product.id))
    expect(second.map((product) => product.id)).toEqual(first.map((product) => product.id))
    // 确实打了两次请求（不是缓存冒充的"一致"）。
    expect(stub.callsTo('/products')).toHaveLength(2)
  })

  it('响应形状不符时显式失败（不静默跳过、不当作空清单）', async () => {
    // ① 非数组
    expect(() => parseProducts({ items: [] })).toThrow(/不是数组/)
    // ② 缺字段（tags 不是一个数组）
    expect(() => parseProducts([{ id: 1, name: 'x', category: 'y', tags: 1, price: 2, emoji: 'z' }])).toThrow(
      /第 0 项缺少必需字段/,
    )
    // ③ 经 loadProducts 走一遍：坏响应必须让调用方拿到 reject（而不是空数组）
    install({ '/products': () => jsonResponse({ detail: 'boom' }, 200) })
    await expect(loadProducts()).rejects.toThrow(/不是数组/)
    expect(getCachedProducts()).toBeNull()
  })

  it('拉取失败（5xx / 网络）如实抛出，且不污染缓存（R11-5 的数据面）', async () => {
    install({ '/products': () => jsonResponse({ detail: 'boom' }, 500) })
    await expect(loadProducts()).rejects.toMatchObject({ status: 500 })
    expect(getCachedProducts()).toBeNull()

    install({
      '/products': () => {
        throw new TypeError('Failed to fetch')
      },
    })
    await expect(loadProducts()).rejects.toThrow('Failed to fetch')
    expect(getCachedProducts()).toBeNull()
  })
})

describe('R11-2 静态面：客户端源码不含商品镜像', () => {
  it('非测试源码中不出现商品字面量，也不引用 ProductSeedData', () => {
    // 用 Vite 的 `import.meta.glob`（相对本测试文件解析）取源码原文：
    // 不依赖 CWD，也不引入额外依赖；两条 glob 分别覆盖 `src/*.ts(x)` 与 `src/**/*.ts(x)`。
    // 注意：glob 的选项**必须是静态字面量**（Vite 在转换期解析 AST，引用变量会直接报错）。
    const sources: Record<string, string> = {
      ...(import.meta.glob('../*.{ts,tsx}', {
        query: '?raw',
        import: 'default',
        eager: true,
      }) as Record<string, string>),
      ...(import.meta.glob('../**/*.{ts,tsx}', {
        query: '?raw',
        import: 'default',
        eager: true,
      }) as Record<string, string>),
    }

    const shipping = Object.entries(sources)
      .filter(([path]) => !/\.test\.tsx?$/.test(path))
      .map(([path, source]) => [srcRelative(path), source] as const)
    const paths = shipping.map(([path]) => path)

    // 「期望文件缺失即显式失败」：glob 若解析不到源码，本用例必须标红而不是空跑通过。
    expect(paths).toContain('App.tsx')
    expect(paths).toContain('api/products.ts')
    expect(paths).toContain('components/ProductModal.tsx')
    expect(shipping.length).toBeGreaterThan(5)

    /** 商品字面量：形如 `{ id: 1, name: … }` 的对象字面量（客户端若内置清单必然长这样）。 */
    const PRODUCT_LITERAL = /\{\s*id\s*:\s*\d+\s*,[^}]*\b(?:name|price|emoji)\s*:/
    /** 服务端种子源码（客户端不得依赖任何服务端源码文件作为数据来源）。 */
    const SEED_SOURCE = /ProductSeedData/

    for (const [path, source] of shipping) {
      expect(PRODUCT_LITERAL.test(source), `${path} 出现商品字面量`).toBe(false)
      expect(SEED_SOURCE.test(source), `${path} 引用了 ProductSeedData`).toBe(false)
    }
  })
})

describe('展示与筛选（纯函数）', () => {
  it('formatPrice 固定两位小数', () => {
    expect(formatPrice(129.99)).toBe('¥129.99')
    expect(formatPrice(199)).toBe('¥199.00')
  })

  it('categoriesOf = 「全部」+ 响应 category 去重（不硬编码类别清单）', () => {
    expect(categoriesOf(PRODUCTS_18)).toEqual([ALL_CATEGORIES, '鞋类', '配饰', '服装'])
    // 类别集合随响应变化：删掉一个类别后选项随之减少（若硬编码则本条必红）。
    const withoutClothing = PRODUCTS_18.filter((product) => product.category !== '服装')
    expect(categoriesOf(withoutClothing)).toEqual([ALL_CATEGORIES, '鞋类', '配饰'])
    expect(categoriesOf([])).toEqual([ALL_CATEGORIES])
  })

  it('默认顺序 = id 升序（与响应原始顺序无关）', () => {
    const shuffled = [...PRODUCTS_18].reverse()
    const sorted = selectProducts(shuffled)
    expect(sorted.map((product) => product.id)).toEqual(
      Array.from({ length: 18 }, (_, index) => index + 1),
    )
  })

  it('R11-6 搜索按响应文本字段过滤（名称 / 类别 / 标签，大小写不敏感）', () => {
    const fixture: Product[] = [
      { id: 1, name: '专业跑鞋', category: '鞋类', tags: ['跑步'], price: 129.99, emoji: '👟' },
      { id: 2, name: '轻量跑鞋', category: '鞋类', tags: ['跑步', '轻量'], price: 199, emoji: '👟' },
      { id: 3, name: '无线降噪耳机', category: '配饰', tags: ['音频'], price: 249.99, emoji: '🎧' },
      { id: 4, name: '速干背心', category: '服装', tags: ['Running'], price: 159, emoji: '🎽' },
    ]

    expect(selectProducts(fixture, { query: '跑鞋' }).map((p) => p.id)).toEqual([1, 2])
    expect(selectProducts(fixture, { query: '配饰' }).map((p) => p.id)).toEqual([3])
    expect(selectProducts(fixture, { query: '音频' }).map((p) => p.id)).toEqual([3])
    // 大小写不敏感（标签是响应字段，一并匹配）。
    expect(selectProducts(fixture, { query: 'running' }).map((p) => p.id)).toEqual([4])
    expect(selectProducts(fixture, { query: '不存在的词' })).toEqual([])
  })

  it('R11-7 类别筛选 + R11-8 价格排序，且三者可叠加', () => {
    const fixture: Product[] = [
      { id: 1, name: '专业跑鞋', category: '鞋类', tags: ['跑步'], price: 129.99, emoji: '👟' },
      { id: 2, name: '轻量跑鞋', category: '鞋类', tags: ['跑步'], price: 199, emoji: '👟' },
      { id: 3, name: '无线降噪耳机', category: '配饰', tags: ['音频'], price: 249.99, emoji: '🎧' },
      { id: 4, name: '速干跑步背心', category: '服装', tags: ['跑步'], price: 159, emoji: '🎽' },
    ]

    expect(selectProducts(fixture, { category: '鞋类' }).map((p) => p.id)).toEqual([1, 2])
    expect(selectProducts(fixture, { category: ALL_CATEGORIES }).map((p) => p.id)).toEqual([1, 2, 3, 4])
    expect(selectProducts(fixture, { sort: 'price_asc' }).map((p) => p.id)).toEqual([1, 4, 2, 3])
    expect(selectProducts(fixture, { sort: 'price_desc' }).map((p) => p.id)).toEqual([3, 2, 4, 1])
    // 叠加：类别 + 搜索 + 价格降序
    expect(
      selectProducts(fixture, { query: '跑鞋', category: '鞋类', sort: 'price_desc' }).map((p) => p.id),
    ).toEqual([2, 1])
  })
})

describe('页面会话级缓存', () => {
  it('第二次 loadProducts 命中缓存：只打一次请求，且返回同一份数据', async () => {
    const stub = install({ '/products': PRODUCTS_18 })

    const first = await loadProducts()
    const second = await loadProducts()

    expect(stub.callsTo('/products')).toHaveLength(1)
    expect(second).toBe(first)
    expect(getCachedProducts()).toBe(first)
  })

  it('清缓存后重新拉取（重试入口的数据面）', async () => {
    const stub = install({ '/products': PRODUCTS_18 })

    await loadProducts()
    clearProductsCache()
    await loadProducts()

    expect(stub.callsTo('/products')).toHaveLength(2)
  })
})

// ---------------------------------------------------------------------------

/**
 * 把 `import.meta.glob` 的键归一成「相对 `src/`」的路径。
 *
 * 键相对**本测试文件**（`src/api/`），且 Vite 会把同目录项写成 `./x.ts`、其余写成 `../x/y.ts`
 * （实测）；手工归一可避免引入 `node:path`（本工程未装 `@types/node`，改依赖属 C1 的边界）。
 */
function srcRelative(globKey: string): string {
  const segments = ['api']
  for (const segment of globKey.split('/')) {
    if (segment === '' || segment === '.') continue
    if (segment === '..') segments.pop()
    else segments.push(segment)
  }
  return segments.join('/')
}
