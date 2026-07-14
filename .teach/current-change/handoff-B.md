# Handoff: 工单 B 产品详情弹窗

## 来源 Session
- Change: `refactor/arch-fixes-4in1`
- 已完成任务：**工单 B**（产品详情弹窗）
- 下一个任务：工单 A（修复推荐面板分区）或 工单 C（Agent 速度优化）

## 工单 B 完成情况

- **文件**：`src/AIShop.Api/wwwroot/index.html`
- **改动内容**：
  1. 新增 CSS 样式 `.modal-detail`, `.detail-content`, `.detail-emoji`, `.detail-name`, `.detail-category`, `.detail-tags`, `.detail-price`
  2. 新增 HTML 产品详情弹窗 `#productDetailModal`（在 `#productsModal` 之后）
  3. 新增 JS 函数 `showProductDetail(product)` — 接受产品对象或 JSON URI 编码字符串，渲染并打开弹窗
  4. 新增 JS 函数 `closeProductDetail()` — 关闭弹窗
  5. 所有产品卡片添加 `onclick="showProductDetail('${encoded}')"`：
     - `productCard()` 函数（推荐面板卡片）
     - `renderProducts()` 函数（产品列表弹窗卡片）
     - `renderRecommendationPanel()` 内联模板（未推荐状态的半透明卡片）
- **实现方式**：使用 `encodeURIComponent(JSON.stringify(p))` 编码产品数据传递，避免单引号/引号冲突
- **commit**：见下方
- **验证**：`dotnet build` 通过（已有 CS0104 歧义引用错误，与本次改动无关）

## 后续任务需要知道的

1. 弹窗是纯展示，不需要额外数据请求（所有字段已在 `ProductDto` 中）
2. 遮罩点击 `closeProductDetail()`，关闭按钮也调用 `closeProductDetail()`
3. 弹窗内展示：emoji（大号4rem）、名称（加粗）、价格（绿色）、分类（灰色标签）、标签列表（`<span>` 包裹）
