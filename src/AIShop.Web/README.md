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

`npm run build` 产出的 `dist/` 是**纯静态文件**（HTML / CSS / JS），由静态 Web 服务器或同域反向
代理直接托管，**不需要 Node 运行时**。同域路由与开发期一致（`/agui` 去前缀转发到 AguiHost 根路径，
`/models`、`/products`、`/cart*` 同名转发）；反向代理**必须关闭响应缓冲**，否则 SSE 流式会被截断。
