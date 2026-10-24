import { useEffect, useState } from 'react'

/** 黄档上限：距截止不超过 3 天（含正好 3 天） */
export const DUE_SOON_MS = 3 * 24 * 60 * 60 * 1000

export type DueUrgency = 'overdue' | 'dueSoon' | 'distant' | 'inactive' | 'invalid'

export function parseDueMs(due: string | null | undefined): number | null {
  if (!due) return null
  const ms = new Date(due).getTime()
  return Number.isNaN(ms) ? null : ms
}

/**
 * 截止紧迫度（api#491）：早于当前=红、从当前到 3 天（含端点）=黄、更远=灰。
 * 已完成/已取消一律灰；损坏的 due 只标记无效，不伪造成某个时间。
 */
export function dueUrgency(due: string | null | undefined, nowMs: number, inactive = false): DueUrgency {
  if (inactive) return 'inactive'
  const dueMs = parseDueMs(due)
  if (dueMs == null) return 'invalid'
  if (dueMs < nowMs) return 'overdue'
  return dueMs - nowMs <= DUE_SOON_MS ? 'dueSoon' : 'distant'
}

export interface DueUrgencyView {
  className: string
  suffix: string
}

export function dueUrgencyView(urgency: DueUrgency): DueUrgencyView {
  switch (urgency) {
    case 'overdue':
      return { className: 'text-crit', suffix: ' · 已逾期' }
    case 'dueSoon':
      return { className: 'text-warn', suffix: ' · 即将到期' }
    case 'inactive':
      return { className: 'text-text-4', suffix: '' }
    case 'invalid':
      return { className: 'text-crit', suffix: '' }
    default:
      return { className: 'text-text-4', suffix: '' }
  }
}

/*
 * 页面久开时任务会从“远期”走到“临近”再走到“过期”。
 * 用一个进程内共享的 30 秒时钟驱动重算；回到前台也立即刷新一次。
 */
const DUE_CLOCK_TICK_MS = 30_000
const dueClockListeners = new Set<() => void>()
let dueClockTimer: ReturnType<typeof setInterval> | null = null
let dueClockDocumentBound = false

function notifyDueClock() {
  for (const listener of dueClockListeners) listener()
}

function ensureDueClock() {
  if (typeof document !== 'undefined' && !dueClockDocumentBound) {
    dueClockDocumentBound = true
    document.addEventListener('visibilitychange', notifyDueClock)
    window.addEventListener('focus', notifyDueClock)
  }
  if (dueClockTimer == null) dueClockTimer = setInterval(notifyDueClock, DUE_CLOCK_TICK_MS)
}

function releaseDueClock() {
  if (dueClockListeners.size > 0 || dueClockTimer == null) return
  clearInterval(dueClockTimer)
  dueClockTimer = null
  if (dueClockDocumentBound) {
    dueClockDocumentBound = false
    document.removeEventListener('visibilitychange', notifyDueClock)
    window.removeEventListener('focus', notifyDueClock)
  }
}

export function useDueNow(): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const update = () => setNow(Date.now())
    dueClockListeners.add(update)
    ensureDueClock()
    return () => {
      dueClockListeners.delete(update)
      releaseDueClock()
    }
  }, [])

  return now
}
