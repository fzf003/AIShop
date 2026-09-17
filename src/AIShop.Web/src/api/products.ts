/**
 * 商品目录（spec R11「商品目录一律来自 GET /products（客户端不得内置镜像）」/ design §9.3）。
 *
 * 唯一数据来源 = AguiHost 的 `GET /products`（裸数组、camelCase，元素含六个字段：
 * `id` / `name` / `category` / `tags` / `price` / `emoji`）。
 *
 * **客户端不持有商品数据**：本模块不内置任何商品数组，也不引用服务端的商品种子源码文件
 * （`src/AIShop.Core` 里的静态种子数据）——商品在服务端增删后，客户端零代码改动即随响应变化。
 * 该约束由 `products.test.ts` 的两条断言把守：静态扫描（非测试源码中不得出现商品字面量、
 * 不得出现种子源码的类型名）与行为断言（渲染项数 = 响应项数）。
 *
 * 排序 / 筛选全部在客户端完成（服务端只返回全量、不分页不筛选）：
 * 默认 `id` 升序、价格升序 / 降序；搜索对响应中**存在**的文本字段做不区分大小写的子串匹配；
 * 类别选项由响应 `category` 去重生成（不硬编码类别清单）；三条件可叠加。
 */
import { apiFetch } from './http'

/** `GET /products` 响应元素（六个字段即契约，客户端不消费任何其它字段）。 */
export interface Product {
  id: number
  name: string
  category: string
  tags: string[]
  price: number
  emoji: string
}

/** 类别 chips 里代表「不筛选」的那一项（对齐原型 v3 的 `curCat='全部'`）。 */
export const ALL_CATEGORIES = '全部'

/** 三种排序（原型 v3 的 `<select id="sort">` 选项，服务端不支持其它维度）。 */
export type ProductSort = 'default' | 'price_asc' | 'price_desc'

export interface ProductFilter {
  /** 搜索关键词：对 `name` / `category` / `tags` 做子串匹配（大小写不敏感）。 */
  query?: string
  /** 类别；缺省或 `ALL_CATEGORIES` 表示不筛选。 */
  category?: string
  /** 排序；缺省 = `id` 升序。 */
  sort?: ProductSort
}

// ---------------------------------------------------------------------------
// 响应形状校验与解析
// ---------------------------------------------------------------------------

function isProduct(value: unknown): value is Product {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Record<string, unknown>
  return (
    typeof candidate.id === 'number' &&
    typeof candidate.name === 'string' &&
    typeof candidate.category === 'string' &&
    Array.isArray(candidate.tags) &&
    candidate.tags.every((tag) => typeof tag === 'string') &&
    typeof candidate.price === 'number' &&
    typeof candidate.emoji === 'string'
  )
}

/**
 * 校验并归一 `GET /products` 的响应体。
 *
 * 形状不符时**显式抛错**（不静默跳过、不把看不懂的响应渲染成「没有商品」）——
 * 调用方据此渲染错误态与重试入口（spec R11 第 6 段）。
 */
export function parseProducts(payload: unknown): readonly Product[] {
  if (!Array.isArray(payload)) {
    throw new Error('[AIShop] /products 响应不是数组，无法作为商品清单使用')
  }

  const products: Product[] = []
  payload.forEach((item, index) => {
    if (!isProduct(item)) {
      throw new Error(
        `[AIShop] /products 响应第 ${index} 项缺少必需字段（id / name / category / tags / price / emoji）`,
      )
    }
    products.push(item)
  })
  return products
}

// ---------------------------------------------------------------------------
// 拉取 + 页面会话级内存缓存
// ---------------------------------------------------------------------------

/**
 * 缓存与在途请求都放在模块级：商品模态、商品详情与购物车的快捷加购 chips 读的是**同一份**，
 * 因此一个页面会话里最多真发一次请求（design §9.3「缓存生命周期 = 页面会话」）。
 */
let cached: readonly Product[] | null = null
let pending: Promise<readonly Product[]> | null = null

/** 已缓存的商品（尚未拉取成功时为 `null`）。 */
export function getCachedProducts(): readonly Product[] | null {
  return cached
}

/** 清空缓存（仅供测试与「强制重取」使用；业务路径不需要主动清）。 */
export function clearProductsCache(): void {
  cached = null
  pending = null
}

/**
 * 取商品清单：命中缓存直接返回，否则 `GET /products` 并校验形状。
 *
 * - `{ force: true }` 跳过缓存（重试入口用；失败不写缓存，故普通重试即可）；
 * - 并发调用共享同一个在途请求，不会打出多个 `GET /products`；
 * - 失败（网络 / 5xx / 形状非法）**原样抛出**，且不污染缓存。
 */
export function loadProducts(options?: { force?: boolean }): Promise<readonly Product[]> {
  if (options?.force !== true && cached !== null) return Promise.resolve(cached)
  if (pending !== null) return pending

  const request = apiFetch<unknown>('/products')
    .then((payload) => {
      const products = parseProducts(payload)
      cached = products
      return products
    })
    .finally(() => {
      pending = null
    })

  pending = request
  return request
}

// ---------------------------------------------------------------------------
// 展示与筛选（纯函数，便于单测）
// ---------------------------------------------------------------------------

/** 价格展示：`¥` + 两位小数（原型 v3 的 `¥${price.toFixed(2)}`）。 */
export function formatPrice(price: number): string {
  return `¥${price.toFixed(2)}`
}

/**
 * 类别 chips 选项 = 「全部」+ 响应 `category` 去重。
 *
 * 顺序按 **`id` 升序下的首次出现序**（而非响应原始顺序）：筛选选项与渲染顺序都不随
 * 服务端返回次序抖动，结果可复现。
 */
export function categoriesOf(products: readonly Product[]): readonly string[] {
  const seen = new Set<string>()
  for (const product of [...products].sort((a, b) => a.id - b.id)) {
    seen.add(product.category)
  }
  return [ALL_CATEGORIES, ...seen]
}

/**
 * 搜索 / 类别过滤 + 排序（先过滤再排序，三条件可叠加）。
 *
 * 搜索目标**只**取响应中存在的文本字段（`name` / `category` / `tags`）；
 * 原型里的商品简介在响应中不存在，不得作为搜索目标（spec R11 第 4 段 + R11-4）。
 */
export function selectProducts(
  products: readonly Product[],
  filter: ProductFilter = {},
): readonly Product[] {
  const query = (filter.query ?? '').trim().toLowerCase()
  const category = filter.category ?? ALL_CATEGORIES

  const matched = products.filter((product) => {
    if (category !== ALL_CATEGORIES && product.category !== category) return false
    if (query === '') return true
    return matchesQuery(product, query)
  })

  return sortProducts(matched, filter.sort ?? 'default')
}

function matchesQuery(product: Product, query: string): boolean {
  return (
    product.name.toLowerCase().includes(query) ||
    product.category.toLowerCase().includes(query) ||
    product.tags.some((tag) => tag.toLowerCase().includes(query))
  )
}

/** `Array.prototype.sort` 是稳定排序：价格并列时保持 `id` 升序。 */
function sortProducts(products: readonly Product[], sort: ProductSort): readonly Product[] {
  const copy = [...products]
  switch (sort) {
    case 'price_asc':
      return copy.sort((a, b) => a.price - b.price)
    case 'price_desc':
      return copy.sort((a, b) => b.price - a.price)
    default:
      return copy.sort((a, b) => a.id - b.id)
  }
}
