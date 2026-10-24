/*
 * 轮询注册表（03 §2）：所有页面的 refetchInterval 一律从这里取，禁止散写数字。
 * 本项目无 WebSocket，实时性全部由轮询实现。
 */

const DAY_START_HOUR = 6
const DAY_END_HOUR = 24

/** 标准轮询：白天（06:00–23:59）5 分钟 / 夜间 30 分钟 */
export function standardIntervalMs(now: Date = new Date()): number {
  const h = now.getHours()
  return h >= DAY_START_HOUR && h < DAY_END_HOUR ? 5 * 60_000 : 30 * 60_000
}

/* 滚动活动跟踪：用户滚动结束后的 1 秒内强制立即刷新，随后回落标准间隔 */
let lastScrollAt = 0

export function markScrollActivity(now: number = Date.now()): void {
  lastScrollAt = now
}

export function timeSinceLastScroll(now: number = Date.now()): number {
  return now - lastScrollAt
}

const SCROLL_REFRESH_WINDOW_MS = 1_000

/** 延迟轮询（作为 refetchInterval 函数使用） */
export function deferredIntervalMs(): number {
  if (timeSinceLastScroll() < SCROLL_REFRESH_WINDOW_MS) return 1_000
  return standardIntervalMs()
}

/** 固定轮询（不随昼夜变化） */
export function fixedIntervalMs(ms: number): () => number {
  return () => ms
}

/** 条件轮询：仅当谓词成立时以给定间隔轮询（如文件同步 syncing 时 2s） */
export function conditionalInterval(ms: number, predicate: () => boolean): () => number | false {
  return () => (predicate() ? ms : false)
}

/** 常量间隔（03 §2 表） */
export const POLL = {
  /** MCP 客户端列表 / 调用流水 */
  mcp: fixedIntervalMs(10_000),
  /** OneDrive 绑定状态（向导弹窗打开期间） */
  onedriveBinding: fixedIntervalMs(5_000),
  /** Outlook 设备码授权（客户端 setTimeout 链） */
  outlookDeviceCode: 3_000,
  /** 状态页每设备移动质量 */
  deviceQuality: 60_000,
  /** Android 内嵌原生采集状态 */
  embedNativeState: 30_000,
  /** 内嵌页日期重键 */
  embedDateRekey: 45_000,
  /** 状态页摘要（侧边栏状态点） */
  statusSummary: 60_000,
} as const

/** 安装全局滚动活动跟踪（AppShell 挂载时调用一次） */
export function installScrollTracking(): () => void {
  if (typeof window === 'undefined') return () => {}
  const handler = () => markScrollActivity()
  window.addEventListener('scroll', handler, { passive: true, capture: true })
  return () => window.removeEventListener('scroll', handler, { capture: true })
}
