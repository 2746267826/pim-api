import type { CapacitorConfig } from '@capacitor/cli'

/**
 * Capacitor（Android 壳）配置。
 * 双壳策略（v0.3 计划）：壳加载 API 同源伺服的 SPA——
 * - 开发：`npm run dev` 后壳内访问 http://<局域网IP>:5173（或 npx cap run android 直连 dev server）
 * - 生产：构建产物由 PIM API 伺服；壳首次启动进入 /setup 填写 API 地址（needsServerSetup），
 *   或在打包时把 server.url 设为固定部署地址（构建期注入，见 P5）。
 */
const config: CapacitorConfig = {
  appId: 'dev.pim.shell',
  appName: 'PIM',
  webDir: 'dist',
  server: {
    // 允许壳内访问局域网 API（/setup 中配置的任意地址）
    cleartext: true,
    // 桥全链路验证时可临时指向远端内嵌页（地址取自 .env 的 PIM_API_TARGET）：
    //   url: '<PIM_API_TARGET>/embed/android/today', allowNavigation: ['*']
    // 默认本地 dist + /setup 引导（远程加载架构：登录后 apiFetch 走配置的服务器）。
  },
}

export default config
