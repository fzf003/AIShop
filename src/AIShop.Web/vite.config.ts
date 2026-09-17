import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

/**
 * AguiHost 的目标地址（开发期代理目标）。
 *
 * 端口来源（见 README「AguiHost 端口」）：
 * - `src/AIShop.AguiHost/Properties/launchSettings.json` 会覆盖端口（历史实测 64321/64322）；
 * - Aspire 运行时会另分配端口（以面板显示为准）。
 * 因此这里给一个可预期的缺省值，起 AguiHost 时用
 * `dotnet run --project src/AIShop.AguiHost -- --urls http://localhost:5299` 对齐即可。
 */
const AGUI_HOST = 'http://localhost:5299'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      /**
       * AG-UI 端点：AguiHost 把它挂在**根路径 `/`**，而 Vite dev server 自己也要用 `/`
       * 提供页面与 HMR 资源。因此**必须**用专用前缀 `/agui` + `rewrite` 去前缀转发。
       *
       * 反证（spec R14「裸根路径代理不可用」）：若把 key 改成裸 `'/'`，
       * 页面请求与 HMR 资源会被一并代理到 AguiHost，dev server 直接不可用。
       */
      '/agui': {
        target: AGUI_HOST,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/agui/, ''),
      },
      // 客户端支撑端点（由变更 agui-client-support 交付）：与 SPA 路径不冲突，原路径转发。
      '/models': { target: AGUI_HOST, changeOrigin: true },
      '/products': { target: AGUI_HOST, changeOrigin: true },
      '/cart': { target: AGUI_HOST, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
