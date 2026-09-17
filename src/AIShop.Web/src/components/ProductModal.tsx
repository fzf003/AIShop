/**
 * 全部商品模态（spec R11「商品目录一律来自 GET /products」/ design §7、§9.3）。
 *
 * - 数据唯一来源 = `GET /products`（`api/products.ts` 的页面会话级缓存，与快捷加购 chips 共用）；
 * - 网格布局 `auto-fill, minmax(196px, 1fr)`；默认 `id` 升序；
 * - 工具栏搜索 + 排序；类别 chips 由响应 `category` 去重生成；三条件可叠加；
 * - 无匹配 → 渲染「没有匹配的商品」空态（整体清除上一次的商品卡，件数计数归零）；
 * - 拉取失败（网络 / 5xx / 形状非法）→ 错误态 + 重试入口，**不**回落到任何本地清单；
 * - 点缩略图或名称 → `ProductDetail`（详情不渲染响应中不存在的字段）。
 */
import { useEffect, useMemo, useRef, useState } from 'react'

import {
  ALL_CATEGORIES,
  categoriesOf,
  formatPrice,
  getCachedProducts,
  loadProducts,
  selectProducts,
  type Product,
  type ProductSort,
} from '../api/products'
import ProductDetail from './ProductDetail'
import './ProductModal.css'

/** 稳定的空数组：`useMemo` 的依赖不应每次渲染都换引用。 */
const NO_PRODUCTS: readonly Product[] = []

export interface ProductModalProps {
  open: boolean
  onClose: () => void
  /** 加购入口（商品卡 / 详情）/ 由上层（C10 / C11）接到 `POST /cart/items`。 */
  onAdd: (productId: number) => void
  /**
   * 拉取失败时的外部通知。
   *
   * `/products` 的 404 也是 REST 面契约（`{"detail":"User not found"}`），必须与 AG-UI 面
   * 走**同一个分派函数**（spec R4 末段）；本组件只负责上报，分派由上层注入
   * （C11 接 `dispatchApiFailure`），错误态与重试入口仍由本组件自己渲染。
   */
  onFailure?: (error: unknown) => void
}

export default function ProductModal({ open, onClose, onAdd, onFailure }: ProductModalProps) {
  const [products, setProducts] = useState<readonly Product[] | null>(getCachedProducts)
  const [failed, setFailed] = useState(false)
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState<string>(ALL_CATEGORIES)
  const [sort, setSort] = useState<ProductSort>('default')
  const [detailId, setDetailId] = useState<number | null>(null)
  const [reloadToken, setReloadToken] = useState(0)

  // `onFailure` 用 ref 透传：避免上层传内联函数时把拉取 effect 变成「每次渲染都重跑」。
  const onFailureRef = useRef(onFailure)
  useEffect(() => {
    onFailureRef.current = onFailure
  })

  useEffect(() => {
    if (!open) {
      // 关闭模态时一并收起详情（对齐原型 v3 的 closeProducts → closeDetail）。
      setDetailId(null)
      return
    }

    let cancelled = false
    loadProducts()
      .then((list) => {
        if (cancelled) return
        setProducts(list)
        setFailed(false)
      })
      .catch((error: unknown) => {
        if (cancelled) return
        setFailed(true)
        onFailureRef.current?.(error)
      })

    return () => {
      cancelled = true
    }
  }, [open, reloadToken])

  const all = products ?? NO_PRODUCTS
  const categories = useMemo(() => categoriesOf(all), [all])
  const visible = useMemo(
    () => selectProducts(all, { query, category, sort }),
    [all, query, category, sort],
  )
  const detail = detailId === null ? null : (all.find((item) => item.id === detailId) ?? null)

  if (!open) return null

  return (
    <>
      <div className="modal" role="dialog" aria-modal="true" aria-label="全部商品">
        <div className="mbox">
          <div className="mhd">
            <h3>📦 全部商品</h3>
            <span className="pc">共 {visible.length} 件</span>
            <button type="button" className="x" onClick={onClose} title="关闭">
              ✕
            </button>
          </div>

          <div className="toolbar">
            <input
              type="search"
              value={query}
              aria-label="搜索商品"
              placeholder="🔍 搜索商品名称、类别或标签…"
              onChange={(event) => setQuery(event.target.value)}
            />
            <select
              aria-label="排序"
              value={sort}
              onChange={(event) => setSort(event.target.value as ProductSort)}
            >
              <option value="default">默认排序</option>
              <option value="price_asc">价格 ↑</option>
              <option value="price_desc">价格 ↓</option>
            </select>
          </div>

          <div className="cats">
            {categories.map((item) => (
              <button
                key={item}
                type="button"
                className={`cat${item === category ? ' on' : ''}`}
                onClick={() => setCategory(item)}
              >
                {item}
              </button>
            ))}
          </div>

          <div className="pgrid">
            {products === null && !failed && <div className="empty">正在加载商品…</div>}

            {failed && (
              <div className="empty" role="alert">
                <div>商品加载失败</div>
                <button
                  type="button"
                  className="retry"
                  onClick={() => {
                    setFailed(false)
                    setReloadToken((token) => token + 1)
                  }}
                >
                  重试
                </button>
              </div>
            )}

            {products !== null && !failed && visible.length === 0 && (
              <div className="empty">没有匹配的商品</div>
            )}

            {products !== null &&
              !failed &&
              visible.map((product) => (
                <div className="pcard" key={product.id}>
                  <button
                    type="button"
                    className="pthumb"
                    title={`查看 ${product.name} 详情`}
                    onClick={() => setDetailId(product.id)}
                  >
                    {product.emoji}
                  </button>
                  <button
                    type="button"
                    className="pname"
                    onClick={() => setDetailId(product.id)}
                  >
                    {product.name}
                  </button>
                  <div className="pprice">{formatPrice(product.price)}</div>
                  <button
                    type="button"
                    className="addbtn"
                    title="加入购物车"
                    onClick={() => onAdd(product.id)}
                  >
                    🛒
                  </button>
                </div>
              ))}
          </div>
        </div>
      </div>

      {detail !== null && (
        <ProductDetail
          product={detail}
          onClose={() => setDetailId(null)}
          onAdd={(productId) => {
            onAdd(productId)
            setDetailId(null)
          }}
        />
      )}
    </>
  )
}
