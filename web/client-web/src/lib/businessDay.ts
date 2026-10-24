/*
 * 业务日工具（PC 域与 Mobile 域共用口径，BusinessDay.cs）：
 * 业务日 D = [D 04:00, D+1 04:00)（Asia/Shanghai，UTC+8，左闭右开）。
 */

export interface BusinessDayRange {
  /** ISO-8601 UTC 字符串，窗口起点（D 04:00 CST） */
  startUtc: string
  /** ISO-8601 UTC 字符串，窗口终点（D+1 04:00 CST） */
  endUtc: string
}

const CST_OFFSET_MS = 8 * 3_600_000
const DAY_MS = 24 * 3_600_000

/** 任意时刻所属的业务日（yyyy-MM-dd；凌晨 0–4 点归前一天） */
export function businessDayOf(date: Date): string {
  const cst = new Date(date.getTime() + CST_OFFSET_MS)
  if (cst.getUTCHours() < 4) {
    cst.setTime(cst.getTime() - DAY_MS)
  }
  return cst.toISOString().slice(0, 10)
}

export function todayBusinessDay(now: Date = new Date()): string {
  return businessDayOf(now)
}

/** 业务日字符串 → 查询窗口（UTC）。'2026-09-26' → [09-25 20:00Z, 09-26 20:00Z) */
export function businessDayRange(day: string): BusinessDayRange {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day)
  if (!m) throw new Error(`非法业务日：${day}`)
  const [, y, mo, d] = m
  const startMs = Date.UTC(Number(y), Number(mo) - 1, Number(d) - 1, 20, 0, 0)
  return {
    startUtc: new Date(startMs).toISOString(),
    endUtc: new Date(startMs + DAY_MS).toISOString(),
  }
}

/** 便捷：业务日 N 天前（用于"近 7 天"等快捷范围） */
export function businessDayShift(day: string, days: number): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day)
  if (!m) throw new Error(`非法业务日：${day}`)
  const [, y, mo, d] = m
  const shifted = new Date(Date.UTC(Number(y), Number(mo) - 1, Number(d)))
  shifted.setUTCDate(shifted.getUTCDate() + days)
  return shifted.toISOString().slice(0, 10)
}
