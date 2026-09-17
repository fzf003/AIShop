/**
 * 商品详情弹窗（spec R11-9「点击缩略图或名称打开详情并展示商品字段」/ design §7）。
 *
 * 只渲染 `GET /products` 响应里**存在**的字段：名称 / 价格 / 类别 / 图标 `emoji` / 标签 `tags`；
 * 原型里的「简介段落」在服务端数据模型里不存在，**不得**渲染（也不用 tags 现编文案）。
 */
import { formatPrice, type Product } from '../api/products'
import './ProductDetail.css'

export interface ProductDetailProps {
  product: Product
  onClose: () => void
  /** 加购入口：由上层接到 `POST /cart/items`（点击后详情关闭，对齐原型 v3）。 */
  onAdd: (productId: number) => void
}

export default function ProductDetail({ product, onClose, onAdd }: ProductDetailProps) {
  return (
    <div
      className="modal"
      role="dialog"
      aria-modal="true"
      aria-label="商品详情"
      onClick={(event) => {
        // 点遮罩关闭（原型 `onclick="if(event.target===this)closeDetail()"`）。
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div className="dbox">
        <div className="dbd">
          <div className="big" aria-hidden="true">
            {product.emoji}
          </div>
          <div className="dinfo">
            <h2>{product.name}</h2>
            <div className="pprice">{formatPrice(product.price)}</div>
            <div className="dmeta">分类：{product.category}</div>
            {product.tags.length > 0 && (
              <div className="dtags">
                {product.tags.map((tag) => (
                  <span className="tag" key={tag}>
                    {tag}
                  </span>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="dft">
          <button type="button" className="addbtn" title="加入购物车" onClick={() => onAdd(product.id)}>
            🛒
          </button>
        </div>
      </div>
    </div>
  )
}
