/*
 * 全局通知（sonner 封装）：实现 03 §1 的错误上浮规则——
 * 按 key 去重；5xx 在"后台轮询中"或"已有缓存数据"时静音。
 */
import { toast } from 'sonner'

const DEDUPE_WINDOW_MS = 3_000
const lastShownByKey = new Map<string, number>()

export interface ErrorNotifyOptions {
  /** 去重键（默认用 message） */
  key?: string
  /** HTTP 状态码（5xx 静音判定） */
  status?: number
  /** 该查询是否已有缓存数据 */
  hasCache?: boolean
  /** 是否处于后台轮询刷新 */
  backgroundPolling?: boolean
  description?: string
}

/** 判定该错误是否应当静音（查询错误 toast 的统一闸门，供 QueryCache onError 复用） */
export function shouldMuteError(opts: ErrorNotifyOptions): boolean {
  if (opts.status != null && opts.status >= 500 && (opts.backgroundPolling || opts.hasCache)) {
    return true
  }
  return false
}

export function notifyError(message: string, opts: ErrorNotifyOptions = {}): void {
  if (shouldMuteError(opts)) return
  const key = opts.key ?? message
  const now = Date.now()
  const last = lastShownByKey.get(key)
  if (last != null && now - last < DEDUPE_WINDOW_MS) return
  lastShownByKey.set(key, now)
  toast.error(message, opts.description ? { description: opts.description } : undefined)
}

export function notifySuccess(message: string, description?: string): void {
  toast.success(message, description ? { description } : undefined)
}

export function notifyInfo(message: string, description?: string): void {
  toast.info(message, description ? { description } : undefined)
}

export function notifyWarning(message: string, description?: string): void {
  toast.warning(message, description ? { description } : undefined)
}
