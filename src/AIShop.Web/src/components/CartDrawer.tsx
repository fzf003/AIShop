/**
 * 购物车抽屉（spec R10「购物车抽屉走 REST 读写并与 AI 工具共写同一份服务端状态」/ design §7、§9.4）。
 *
 * - **读**：打开时 `GET /cart`（经 `state/cart.ts#refreshCart`），条目与合计**直接用响应字段**
 *   （`productName` / `productPrice` / `productEmoji` / `quantity` / `totalPrice`），
 *   **不**按 `productId` 去商品数据里查、**不**解析任何工具结果文本（R10-1）；
 * - **写**：`+` / `−` / `🗑` / 清空直连 REST，点击立即生效，**不发消息给 Agent**（R10-3）；
 *   数量为 1 时 `−` 走移除（避免依赖服务端对 ≤0 的 400）；
 * - **快捷加购 chips**：`GET /products` 中尚未在车内的商品、最多 6 个，点击 = 加购（R10-8）；
 *   门控**同时**要求「车非空」与「仍有未加购商品」，不满足时整区不渲染（R10-9 / R10-10）；
 * - **空态**只有一段纯文案（R10-9 / 原型 v3：空态内没有任何引导入口，也不是 chips 的位置）。
 */
import { useEffect, useMemo, useState } from 'react'

import type { CartItem } from '../api/cart'
import { formatPrice, getCachedProducts, loadProducts, type Product } from '../api/products'
import {
  addToCart,
  clearCart,
  decreaseQuantity,
  increaseQuantity,
  refreshCart,
  removeCartItem,
  useCart,
} from '../state/cart'
import './CartDrawer.css'

/** 稳定空数组：`useMemo` 的依赖不应每次渲染都换引用。 */
const NO_PRODUCTS: readonly Product[] = []

export interface CartDrawerProps {
  open: boolean
  onClose: () => void
}

export default function CartDrawer({ open, onClose }: CartDrawerProps) {
  const { cart, loading, failed } = useCart()
  const [products, setProducts] = useState<readonly Product[] | null>(getCachedProducts)

  // 打开抽屉 = 拉一次权威态（design §9.4 的三个刷新来源之一）。
  useEffect(() => {
    if (!open) return
    void refreshCart()
  }, [open])

  const hasItems = cart !== null && cart.items.length > 0

  // 快捷加购要用商品目录：**复用 C9 的模块级缓存**（同一个页面会话里不会再打一次 `GET /products`）。
  // 空车时该区不显示，因此也不去取目录。取不到就只是不展示 chips（增强项，不打断购物车读写）。
  useEffect(() => {
    if (!open || !hasItems) return
    let cancelled = false
    loadProducts()
      .then((list) => {
        if (!cancelled) setProducts(list)
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [open, hasItems])

  const inCart = useMemo(
    () => new Set((cart?.items ?? []).map((item) => item.productId)),
    [cart],
  )

  // 未加购的商品、最多 6 个；「车非空」由 `hasItems` 保证，「仍有未加购商品」由 `length > 0` 保证。
  // 两个条件任一不满足 → 整区不渲染（不会留下空容器，也不会残留上一次的 chips）。
  const candidates = useMemo(() => {
    if (!hasItems || products === null) return NO_PRODUCTS
    return products.filter((product) => !inCart.has(product.id)).slice(0, 6)
  }, [hasItems, products, inCart])

  if (!open) return null

  const onIncrease = (item: CartItem): void => {
    void increaseQuantity(item.id, item.quantity + 1)
  }

  const onDecrease = (item: CartItem): void => {
    // 减到 0 的语义由客户端定：数量为 1 时 `−` 等价于移除，不发 `quantity: 0` 的请求。
    if (item.quantity <= 1) {
      void removeCartItem(item.id)
      return
    }
    void decreaseQuantity(item.id, item.quantity - 1)
  }

  return (
    <>
      <div className="ov on" onClick={onClose} />

      <aside className="drawer on" aria-label="购物车">
        <div className="dhd">
          <h3>购物车</h3>
          <button type="button" className="x" title="关闭" onClick={onClose}>
            ✕
          </button>
        </div>

        <div className="dbody">
          {cart === null && loading && <div className="empty">正在加载购物车…</div>}

          {/* 拉取失败：不把「未知」渲染成「空车」（那是一个服务端并未确认的状态）。 */}
          {cart === null && !loading && failed && (
            <div className="empty" role="alert">
              <div>购物车加载失败</div>
              <button type="button" className="retry" onClick={() => void refreshCart()}>
                重试
              </button>
            </div>
          )}

          {cart !== null && cart.items.length === 0 && <div className="empty">购物车是空的</div>}

          {cart?.items.map((item) => (
            <div className="citem" key={item.id}>
              <div className="pthumb">{item.productEmoji}</div>
              <div className="pinfo">
                <div className="pname">{item.productName}</div>
                <div className="pprice">{formatPrice(item.productPrice)}</div>
              </div>
              <div className="qty">
                <button
                  type="button"
                  aria-label={`减少 ${item.productName} 数量`}
                  onClick={() => onDecrease(item)}
                >
                  −
                </button>
                <span>{item.quantity}</span>
                <button
                  type="button"
                  aria-label={`增加 ${item.productName} 数量`}
                  onClick={() => onIncrease(item)}
                >
                  +
                </button>
              </div>
              <button
                type="button"
                className="del"
                aria-label={`移除 ${item.productName}`}
                onClick={() => void removeCartItem(item.id)}
              >
                🗑
              </button>
            </div>
          ))}
        </div>

        {candidates.length > 0 && (
          <div className="qa">
            <div className="qa-lbl">快捷加购</div>
            <div className="qa-chips">
              {candidates.map((product) => (
                <button
                  key={product.id}
                  type="button"
                  className="qa-chip"
                  onClick={() => void addToCart(product.id)}
                >
                  {product.emoji} {product.name} {formatPrice(product.price)}
                </button>
              ))}
            </div>
          </div>
        )}

        <div className="dfoot">
          <div className="ctot">
            <span>合计</span>
            <span className="amt">{formatPrice(cart?.totalPrice ?? 0)}</span>
          </div>
          <div className="dacts">
            <button type="button" className="clear" onClick={() => void clearCart()}>
              🗑 清空
            </button>
            {/* 结算不在本期范围（design §12）：保留位置但不可点。 */}
            <button type="button" className="checkout" disabled>
              去结算
            </button>
          </div>
        </div>
      </aside>
    </>
  )
}
