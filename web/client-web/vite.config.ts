import { existsSync } from 'node:fs'
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// 本地 .env 提供 PIM_API_TARGET / PIM_DEV_PORT（已 gitignore；模板见 .env.example）。
// loadEnvFile 不覆盖已有的 shell 环境变量，故命令行显式传入的值始终优先。
const envPath = path.resolve(__dirname, '.env')
if (existsSync(envPath)) process.loadEnvFile(envPath)

// 开发环境将 /api 代理到 PIM API。
// 目标地址来自 PIM_API_TARGET；未设置时回落到本机后端。
// 也可不依赖代理：在应用内「设置 → 服务器」填写 API 地址（跨域需后端 CORS 白名单放行）。
const API_TARGET = process.env.PIM_API_TARGET ?? 'http://localhost:5858'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  /*
   * 重依赖启动即预打包：否则首次访问对应页面时才会「发现新依赖 → 重新优化 →
   * 整页重载」，表现为冷加载秒级白屏/卡死（历史上位置页的 Leaflet 即此症状）。
   */
  optimizeDeps: {
    include: [
      'leaflet',
      'react-leaflet',
      '@fullcalendar/react',
      '@fullcalendar/daygrid',
      '@fullcalendar/timegrid',
      '@fullcalendar/list',
      '@fullcalendar/interaction',
      'motion',
      'echarts/core',
    ],
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, 'src'),
    },
  },
  server: {
    /*
     * 端口 5173 落进 Windows 保留区间 5141–5240（见 netsh excludedportrange），
     * 绑定会 EACCES；该区间内 5174/5175… 同样不可用，故自动递增也救不回来。
     * 改用未保留的 3000，strictPort 固定以免静默漂移；需要时用 PIM_DEV_PORT 覆盖。
     */
    port: Number(process.env.PIM_DEV_PORT ?? 3000),
    strictPort: true,
    /*
     * 显式绑定 127.0.0.1：Vite 默认只监听 ::1（IPv6），
     * 导致 http://127.0.0.1:3000 及部分工具连不上。
     */
    host: '127.0.0.1',
    proxy: {
      '/api': {
        target: API_TARGET,
        changeOrigin: true,
        secure: false, // 测试 API 使用自签/非常规证书
      },
    },
  },
})

