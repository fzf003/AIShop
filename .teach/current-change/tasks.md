# 任务清单：产品推荐面板修复 + 详情弹窗 + Agent 速度优化

> 生成时间：2026-07-13
> 来源：grilling 产出

## 功能描述

### 功能 1：恢复推荐面板分区
登录后右侧推荐面板 / 聊天后的推荐面板都应有分层：
- **🎯 为您推荐** — 本次聊天关键词匹配的产品
- **📋 其他商品** — 不匹配的产品
当前 `/api/recommendations` 登录后推荐是平板列表，需要修复。

### 功能 2：产品详情弹窗
点击产品卡片弹出模态窗，显示产品名、价格、分类、标签、emoji。

### 功能 3：Agent 速度优化
Agent 调用有时 20 多秒，需优化到 2 秒以内。先诊断瓶颈再优化。

---

## 工单

### 工单 A：修复推荐面板分区

- [x] **文件**：`src/AIShop.Api/Features/Chat/ChatEndpoints.cs`、`src/AIShop.Api/wwwroot/index.html`
- [x] **描述**：
  1. 修改 `/api/recommendations` 端点，使其返回类似 `/api/chat` 的分层结构（推荐区 + 其他区）
  2. 登录后推荐面板使用分区渲染（🎯 为您推荐 / 📋 其他商品）
  3. 前端 `renderRecommendations()` 改为分区模式（products[0]→为您推荐, products[1..]→其他商品）
- **blocking**：无
- **状态**：✅

### 工单 B：产品详情弹窗

- [x] **文件**：`src/AIShop.Api/wwwroot/index.html`
- [x] **描述**：
  1. 新增产品详情模态窗（可复用现有 modal 容器样式）
  2. 点击产品卡片（推荐面板和产品列表中的）弹出模态窗
  3. 展示：emoji、名称、价格、分类、标签
- **blocking**：无
- **状态**：✅

### 工单 C：Agent 速度优化

#### C1 — 诊断瓶颈

- [ ] **文件**：`src/AIShop.Api/Agents/ShoppingAssistantAgent.cs`、Agent 调用链
- [ ] **描述**：在关键节点打日志，记录耗时（历史加载、LLM 响应、商品匹配）
- **blocking**：无
- **状态**：⬜

#### C2 — 实施优化

- [x] **文件**：`src/AIShop.Api/Features/Chat/ChatEndpoints.cs`
- [x] **描述**：缓存 /chat 的 AgentChatResult 到 IMemoryCache，/recommendations 优先重用该缓存消除重复 LLM 调用
- **blocking**：C1
- **状态**：✅

---

## 执行顺序（Blocking Edges）

```
工单 A（推荐面板）          blocking: 无
工单 B（详情弹窗）          blocking: 无
工单 C1（诊断瓶颈）         blocking: 无
                          ↑ A、B、C1 可并行
工单 C2（实施优化）         blocking: C1
```

| 工单 | blocking edges |
|------|---------------|
| A | 无 |
| B | 无 |
| C1 | 无 |
| C2 | C1 |
