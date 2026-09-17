/**
 * 模型选择器（spec R6 / design §7、§9.2）：**登录页网格** + **顶栏徽标下拉**，共用同一份清单。
 *
 * 两个导出：
 * - `ModelPicker`：登录页的横排卡片网格（emoji + `name` + `model` 副标题 + 「默认」角标），
 *   选中态为靛蓝描边 + 浅底；`id` 只随 `onSelect` 回传，**不作为展示文案**；
 * - `ModelBadge`：主界面顶栏的模型徽标 + 下拉（当前项带 ✓）。对话进行中可切换，
 *   切换只改「当前选中项」，**下一轮**请求才生效（在途本轮的请求体已发出、不可变）。
 *
 * 清单与顺序一律以 `GET /models` 的响应为准（不排序、不补项、不硬编码）。
 */
import { useState } from 'react'

import { modelEmoji, type ModelInfo } from '../data/models'
import './ModelPicker.css'

export interface ModelPickerProps {
  /** 清单（来自 `GET /models`，顺序即响应顺序）。 */
  models: readonly ModelInfo[]
  /** 当前选中项的 `id`（节键）；无可用模型时为 `null`。 */
  currentId: string | null
  /** 选中某一项；参数是该模型的 `id`（**不是** `model` 字段）。 */
  onSelect: (id: string) => void
}

/** 登录页的模型卡片网格。 */
export function ModelPicker({ models, currentId, onSelect }: ModelPickerProps) {
  return (
    <div className="mgrid">
      {models.map((model) => {
        const selected = model.id === currentId
        return (
          <button
            key={model.id}
            type="button"
            className={`mcard${selected ? ' on' : ''}`}
            data-model-id={model.id}
            aria-pressed={selected}
            onClick={() => onSelect(model.id)}
          >
            <span className="e">{modelEmoji(model.id)}</span>
            <span className="n">{model.name}</span>
            <span className="d">{model.model}</span>
            {model.isDefault && <span className="b">默认</span>}
          </button>
        )
      })}
    </div>
  )
}

/** 主界面顶栏的模型徽标 + 下拉。 */
export function ModelBadge({ models, currentId, onSelect }: ModelPickerProps) {
  const [open, setOpen] = useState(false)
  const current = models.find((model) => model.id === currentId) ?? null

  return (
    <div className="mwrap">
      <button
        type="button"
        className="mbadge"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
      >
        {current === null ? '选择模型' : `${modelEmoji(current.id)} ${current.name}`}
        <span className="caret">▼</span>
      </button>

      {/* 选项仅在展开时渲染（关闭态不留在可访问树里），`.mdd.on` 的显示规则与原型一致。 */}
      <div className={open ? 'mdd on' : 'mdd'}>
        {open &&
          models.map((model) => (
            <button
              key={model.id}
              type="button"
              data-model-id={model.id}
              onClick={() => {
                onSelect(model.id)
                setOpen(false)
              }}
            >
              {modelEmoji(model.id)} {model.name}
              {model.id === currentId && <span className="c">✓</span>}
            </button>
          ))}
      </div>
    </div>
  )
}
