/**
 * 视觉规格核对（tasks.md C12「验收 / 测试」的可断言项）。
 *
 * 依据 = **原型 v3**（`docs/prototypes/agui-client-v3.html`，spec R13 指定的视觉唯一权威，
 * C1 已把它纳入版本控制）+ spec `agui-client` R13 的两段要求：
 *  - R13-2 令牌与尺寸：页面底色 `#f6f7fb`、主色 `#4f46e5`、价格色 `#e8590c`、大圆角 20px、
 *    推荐栏 360px、抽屉 410px；R13 第 3 段追加的 Toast 形态（顶部居中 / 绿底白字小圆角 /
 *    约 3 秒 / 不拦截点击）；
 *  - R13-1 加购入口形态：全站 MUST NOT 出现「加入购物车」字样的文字按钮。
 *
 * 断言写法约定（handoff-C11 遗留 2）：**先定位规则体再断言其内容**，找不到规则即 `throw`。
 * 反例是 `expect(cssText).toContain(...)` —— Vitest 若把 `.css` 换成空串，这种写法会**假绿**。
 *
 * 本工单只新增本文件，不改任何产品代码（tasks.md C12「涉及文件」）。
 */
import { describe, expect, it } from 'vitest'

import cartDrawerCss from '../components/CartDrawer.css?raw'
import recoPanelCss from '../components/RecoPanel.css?raw'
import toastCss from '../components/Toast.css?raw'
import { TOAST_DURATION_MS } from '../components/Toast'
import tokensCss from './tokens.css?raw'

// ---------------------------------------------------------------------------
// CSS 源码读取工具（找不到目标即抛错，杜绝「读到空串却断言通过」）
// ---------------------------------------------------------------------------

/** 取某个选择器规则体（首个 `{` 到其后首个 `}`）。找不到即抛错。 */
function cssRule(cssText: string, selector: string): string {
  const start = cssText.indexOf(`${selector} {`)
  if (start === -1) throw new Error(`未找到 CSS 规则 ${selector}`)
  const open = cssText.indexOf('{', start)
  const close = cssText.indexOf('}', open)
  if (close === -1) throw new Error(`CSS 规则 ${selector} 没有闭合花括号`)
  return cssText.slice(open + 1, close)
}

/** 取自定义属性值（`--name: value;`）。找不到即抛错。 */
function token(cssText: string, name: string): string {
  const body = cssRule(cssText, ':root')
  const match = new RegExp(`--${name}\\s*:\\s*([^;]+);`).exec(body)
  if (match === null) throw new Error(`tokens.css 的 :root 缺少 --${name}`)
  return match[1].trim()
}

/** 在规则体里取某条声明的值（先去掉注释再按 `;` 切）。未声明返回 `null`。 */
function declaration(ruleBody: string, property: string): string | null {
  const withoutComments = ruleBody.replace(/\/\*[\s\S]*?\*\//g, '')
  for (const part of withoutComments.split(';')) {
    const colon = part.indexOf(':')
    if (colon === -1) continue
    if (part.slice(0, colon).trim() === property) return part.slice(colon + 1).trim()
  }
  return null
}

// ---------------------------------------------------------------------------
// 源码扫描工具（R13-1）
// ---------------------------------------------------------------------------

/** 一个 JSX `<button>` 的「开标签原文 + 子节点原文」。 */
interface JsxButton {
  openTag: string
  children: string
}

/**
 * 扫出源码里的全部 `<button>…</button>`。
 *
 * 开标签**不能**用 `/<button[^>]*>/` 匹配：属性里的箭头函数（`onClick={() => …}`）自带 `>`，
 * 会在 `=>` 处提前截断。这里逐字符走到「不在引号内、且花括号平衡处」的第一个 `>`。
 */
function buttons(source: string): JsxButton[] {
  const found: JsxButton[] = []
  const opener = /<button\b/g
  let match: RegExpExecArray | null

  while ((match = opener.exec(source)) !== null) {
    let index = match.index + match[0].length
    let depth = 0
    let quote: string | null = null
    for (; index < source.length; index++) {
      const char = source[index]
      if (quote !== null) {
        if (char === quote) quote = null
        continue
      }
      if (char === '"' || char === "'" || char === '`') {
        quote = char
        continue
      }
      if (char === '{') depth++
      else if (char === '}') depth--
      else if (char === '>' && depth === 0) break
    }

    const close = source.indexOf('</button>', index)
    if (close === -1) continue
    found.push({
      openTag: source.slice(match.index, index + 1),
      children: source.slice(index + 1, close),
    })
    opener.lastIndex = close
  }

  return found
}

/** 子节点里**渲染出来的可见文本**：丢掉 `{…}` 表达式与标签语法（只留文本节点）。 */
function visibleText(children: string): string {
  let text = ''
  let index = 0
  while (index < children.length) {
    const char = children[index]
    if (char === '{') {
      let depth = 0
      for (; index < children.length; index++) {
        if (children[index] === '{') depth++
        else if (children[index] === '}') {
          depth--
          if (depth === 0) break
        }
      }
      index++
      continue
    }
    if (char === '<') {
      const end = children.indexOf('>', index)
      if (end === -1) break
      index = end + 1
      continue
    }
    text += char
    index++
  }
  return text.trim()
}

/**
 * 全站「文字版加购按钮」的可见文案（空数组 = 合规）。
 *
 * 判定口径按 handoff-C9 遗留 4：**看渲染出的可见文本，不看源码字面量** —— 加购入口带
 * `title="加入购物车"`（与原型逐字一致），那是**提示属性**不是文字按钮，按字面量判定会把
 * 原型自己的写法判为违规。
 */
function textAddButtonTexts(source: string): string[] {
  return buttons(source)
    .map((button) => visibleText(button.children))
    .filter((text) => /加入购物车|加购/.test(text))
}

/** 把 `import.meta.glob` 的键归一成「相对 `src/`」的路径（键格式不统一，同 C9 经验 2）。 */
function srcRelative(globKey: string): string {
  const segments = ['styles']
  for (const segment of globKey.split('/')) {
    if (segment === '' || segment === '.') continue
    if (segment === '..') segments.pop()
    else segments.push(segment)
  }
  return segments.join('/')
}

// ---------------------------------------------------------------------------

describe('R13-2 / R13 第 3 段：设计令牌、关键尺寸与 Toast 形态', () => {
  it('tokens.css 的令牌、推荐栏/抽屉宽度与 Toast 形态逐项符合原型 v3', () => {
    // --- 令牌（原型 v3 的 :root 逐字，值不得自行「优化」）---
    expect(token(tokensCss, 'bg')).toBe('#f6f7fb')
    expect(token(tokensCss, 'brand')).toBe('#4f46e5')
    expect(token(tokensCss, 'price')).toBe('#e8590c')
    expect(token(tokensCss, 'r-lg')).toBe('20px')
    // 成功色 = Toast 绿底所用的令牌值（R13 第 3 段）
    expect(token(tokensCss, 'ok')).toBe('#12a150')

    // --- 尺寸：推荐栏 360px / 抽屉 410px ---
    // 这两条是**组件级布局尺寸**、不是自定义属性，故不在 tokens.css 而在各自组件 CSS 里
    // （原型同样把 `.reco` / `.drawer` 的宽度写在规则体内）。断言各自规则体，效果等价。
    expect(declaration(cssRule(recoPanelCss, '.reco'), 'width')).toBe('360px')
    expect(declaration(cssRule(cartDrawerCss, '.drawer'), 'width')).toBe('410px')

    // --- Toast 形态（R13 第 3 段 / 场景 R13-3）---
    const toast = cssRule(toastCss, '.toast')
    // 顶部居中：固定定位 + 贴着顶部 + 水平居中（left 50% 配 translateX(-50%)）
    expect(declaration(toast, 'position')).toBe('fixed')
    expect(declaration(toast, 'top')).toBe('22px')
    expect(declaration(toast, 'left')).toBe('50%')
    expect(declaration(toast, 'transform')).toContain('translateX(-50%)')
    // 绿底白字：底色走 `--ok`（上面已钉死 #12a150），文字纯白
    expect(declaration(toast, 'background')).toBe('var(--ok)')
    expect(declaration(toast, 'color')).toBe('#fff')
    // 小圆角：走 `--r-sm`（原型 10px）
    expect(declaration(toast, 'border-radius')).toBe('var(--r-sm)')
    expect(token(tokensCss, 'r-sm')).toBe('10px')
    // 不拦截点击（R13-3 第 3 段：提示层不阻塞用户继续操作）
    expect(declaration(toast, 'pointer-events')).toBe('none')
    // 可见态：`.toast.show` 才完全不透明（无提示时元素仍在 DOM 里，只是透明）
    expect(declaration(cssRule(toastCss, '.toast.show'), 'opacity')).toBe('1')

    // 约 3 秒自动消失：时长常量（行为侧由 CartDrawer.test.tsx「约 3 秒后自动消失」覆盖）
    expect(TOAST_DURATION_MS).toBe(3000)
  })
})

describe('R13-1：全站不存在文字版加购按钮', () => {
  it('三处加购入口都是无文字的 🛒 图标按钮，且没有任何「加入购物车 / 加购」文字按钮', () => {
    // 先自检检测器本身（防「永远不命中」的空转断言）：
    // 合成的文字按钮必须被抓到，而带 `title` 属性的图标按钮必须**不**被抓到。
    expect(textAddButtonTexts('<button className="addbtn">加入购物车</button>')).toEqual(['加入购物车'])
    expect(textAddButtonTexts('<button className="addbtn">加购</button>')).toEqual(['加购'])
    expect(textAddButtonTexts('<button className="addbtn" title="加入购物车">🛒</button>')).toEqual([])

    const sources: Record<string, string> = {
      ...(import.meta.glob('../*.tsx', {
        query: '?raw',
        import: 'default',
        eager: true,
      }) as Record<string, string>),
      ...(import.meta.glob('../**/*.tsx', {
        query: '?raw',
        import: 'default',
        eager: true,
      }) as Record<string, string>),
    }

    const shipping = Object.entries(sources)
      .filter(([path]) => !/\.test\.tsx$/.test(path))
      .map(([path, source]) => [srcRelative(path), source] as const)

    // 「期望文件缺失即显式失败」：glob 解析不到源码时本用例必须标红，不能空跑通过。
    expect(shipping.map(([path]) => path)).toEqual(
      expect.arrayContaining([
        'App.tsx',
        'components/ProductModal.tsx',
        'components/ProductDetail.tsx',
        'components/RecoPanel.tsx',
        'components/CartDrawer.tsx',
      ]),
    )

    /** 加购入口的标记属性（与原型 v3 逐字一致，handoff-C9 遗留 4）。 */
    const ADD_ENTRY = 'title="加入购物车"'
    /** 加购入口允许的 class（商品卡 / 详情 = `addbtn`，推荐卡 = `radd`）。 */
    const ADD_ENTRY_CLASS = /className="(?:addbtn|radd)"/

    const entries = shipping.flatMap(([path, source]) =>
      buttons(source)
        .filter((button) => button.openTag.includes(ADD_ENTRY))
        .map((button) => ({ path, ...button })),
    )

    // 正向锚点：商品卡 / 详情 / 推荐卡三处入口都被扫到，否则下面的循环会退化成空跑。
    expect(entries.map((entry) => entry.path)).toEqual(
      expect.arrayContaining([
        'components/ProductModal.tsx',
        'components/ProductDetail.tsx',
        'components/RecoPanel.tsx',
      ]),
    )
    expect(entries.length).toBeGreaterThanOrEqual(3)

    for (const entry of entries) {
      expect(entry.openTag, `${entry.path} 的加购入口 class 不合原型`).toMatch(ADD_ENTRY_CLASS)
      // 「无文字标签」：可见文本只有 🛒 图标（文字按钮会在这里被抓住）。
      expect(visibleText(entry.children), `${entry.path} 的加购入口带文字标签`).toBe('🛒')
    }

    // 全量兜底：不论用哪个 class，任何按钮的可见文本都不得是「加入购物车 / 加购」。
    for (const [path, source] of shipping) {
      expect(textAddButtonTexts(source), `${path} 出现文字版加购按钮`).toEqual([])
    }
  })
})
