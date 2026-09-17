# AIShop.Web — AG-UI 前端客户端

面向 `AIShop.AguiHost` 的 AG-UI 前端（React 19 + TypeScript + Vite + `@ag-ui/client`）。

**独立 npm 工程**：不进 `AIShop.sln`、不被任何 `.csproj` 引用、不进 Aspire AppHost 编排。
构建/测试入口是 `npm run build` / `npm run test`（Vitest），**不是** `dotnet build` / `dotnet test`。

## 常用命令

```bash
npm install          # 安装依赖
npm run dev          # 启动开发服务器（默认 http://localhost:5173）
npm run build        # 类型检查（strict）+ 产出静态产物到 dist/
npm run preview      # 本地预览 dist/ 产物
npm run test         # 跑 Vitest（jsdom）
```

## AguiHost 端口

开发期代理目标写死在 `vite.config.ts` 顶部的 `AGUI_HOST`（默认 `http://localhost:5299`）。
AguiHost 的实际端口有**两个来源**，都不等于 5299：

1. `src/AIShop.AguiHost/Properties/launchSettings.json` **会覆盖端口**（历史实测 64321/64322）；
2. Aspire 运行时会另行分配端口（以 Dashboard 上 `agui` 资源显示的地址为准）。

因此起 AguiHost 时二选一：

```bash
# 方式一：显式指定端口，与 vite.config.ts 的 AGUI_HOST 对齐（推荐）
dotnet run --project src/AIShop.AguiHost -- --urls http://localhost:5299

# 方式二：用 Aspire 面板显示的实际端口，同步改 vite.config.ts 的 AGUI_HOST
```

## 代理前缀约定（开发期同源，不依赖 CORS）

`vite.config.ts` 的 `server.proxy`：

| 前端路径 | 转发到 AguiHost | 说明 |
|---|---|---|
| `/agui` | `/`（**去前缀**） | AG-UI 端点在 AguiHost 的**根路径** |
| `/models` | `/models` | 原路径转发 |
| `/products` | `/products` | 原路径转发 |
| `/cart` | `/cart*` | 原路径转发 |

**必须用专用前缀 `/agui`**：Vite dev server 自身要用 `/` 提供页面与 HMR 资源，若把裸 `/`
配成代理前缀，页面与热更新请求会被一并转发到 AguiHost，dev server 直接不可用。

AguiHost 默认**不注册 CORS**（`Cors:AllowedOrigins` 未配置），开发期通过上面的代理做到浏览器视角
同源，因此不需要 CORS。

## 部署（生产）

### 产物 = 纯静态文件

`npm run build` 产出 `src/AIShop.Web/dist/`，由静态 Web 服务器直接托管：

| 文件 | 说明 |
|---|---|
| `index.html` | 入口页面 |
| `assets/index-*.css` | 样式（含设计令牌） |
| `assets/index-*.js` | 应用脚本（React + 官方 `@ag-ui/client`） |

生产形态**不需要 Node.js 运行时**：不以 `node` 启动任何进程、无 SSR、无自管服务端。
Node.js 只作**开发期与构建期**工具（Vite dev server、`npm run build`、Vitest）。
客户端**不自建任何 HTTP 端点**、**不引入中间层代理服务**（BFF / 自管 agent 端点）——
全部服务端能力来自既有的 `AIShop.AguiHost`。

### 同域反向代理

部署拓扑 = 「静态站点 + 到 AguiHost 的同域反代」，路由规则与开发期的 `server.proxy` **逐条一致**：

| 反代路径 | 转发到 AguiHost | 说明 |
|---|---|---|
| `/agui` | `/`（**去前缀**） | AG-UI 端点（SSE，`POST`） |
| `/models` | `/models` | 模型清单 |
| `/products` | `/products` | 商品目录 |
| `/cart*` | `/cart*` | 购物车读写 |

静态站点与 AguiHost 部署在**同一域名**下 ⇒ 浏览器视角同源 ⇒ **不依赖 CORS**
（`Cors:AllowedOrigins` 默认不配置，AguiHost 不注册 CORS，这是设计前提而非疏漏）。

### 必须关闭响应缓冲（`/agui`）

`/agui` 是**流式（SSE）**端点。反向代理**必须关闭响应缓冲**，否则模型输出会被代理整段缓冲，
逐字流式与工具胶囊的实时出现全部失效。以 Nginx 为例：

```nginx
location /agui {
    proxy_pass http://agui-host:5299/;   # 末尾的 / 即「去前缀」
    proxy_http_version 1.1;
    proxy_set_header Connection '';
    proxy_buffering off;                 # 关键
    proxy_cache off;                     # 关键
    proxy_read_timeout 3600s;
}
```

### AguiHost 地址来源

反代指向的 AguiHost 地址由部署方决定，端口**两个来源**见上文「AguiHost 端口」一节
（`launchSettings.json` 或 Aspire 面板分配的地址）。`vite.config.ts` 的 `AGUI_HOST`
**只作用于开发期**，不参与生产。
