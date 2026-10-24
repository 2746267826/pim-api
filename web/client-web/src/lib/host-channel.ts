/*
 * 宿主通道标记（desktop host / Web 嵌入页）。
 *
 * 唯一规格：`remediation/host-session/reviewed-spec.md`（sha256 8964935021f6…）。
 * §1：Windows 嵌入宿主在每次文档创建前安装一条**常量**脚本
 *     `window.__PIM_HOST_CHANNEL__={v:1,product:"pim-host-windows"};`
 *     页面启动时读一次；标记合法 → host 通道，否则 browser（= 今天的行为）。
 *
 * 本文件只负责**标记读取**这一个接缝（`detectHostChannel`）。标记合法 → host 通道，
 * 否则 browser（= 今天的行为）。host 通道上的握手 / `page.refresh` / `/auth/me` 守卫 /
 * 退出与代次在 `./host-session.ts` + `./host-wire.ts` + `features/auth/auth-context.tsx`
 * 里实现（R5 已落地）；本文件不判断登录态。
 */

export const HOST_PRODUCT = 'pim-host-windows'
export const HOST_CHANNEL_VERSION = 1

/** 宿主安装的常量脚本（不允许插值；里面没有令牌/URL/用户信息） */
export const HOST_CHANNEL_MARKER_SCRIPT =
  'window.__PIM_HOST_CHANNEL__={v:1,product:"pim-host-windows"};'

export interface HostMarker {
  v: number
  product: string
}

export type HostChannel = 'browser' | 'host'

/** 解析标记；`v` 必须是数字 1，`product` 必须是 pim-host-windows */
export function parseHostMarker(raw: unknown): HostMarker | null {
  if (raw === null || typeof raw !== 'object') return null
  const m = raw as { v?: unknown; product?: unknown }
  if (m.v !== HOST_CHANNEL_VERSION) return null
  if (m.product !== HOST_PRODUCT) return null
  return { v: m.v, product: m.product }
}

/** 页面启动时读一次标记；读不到/不合法 → browser（今天的全部行为） */
export function detectHostChannel(scope: unknown = globalThis): HostChannel {
  const g = scope as { __PIM_HOST_CHANNEL__?: unknown } | null
  if (!g) return 'browser'
  return parseHostMarker(g.__PIM_HOST_CHANNEL__) ? 'host' : 'browser'
}
