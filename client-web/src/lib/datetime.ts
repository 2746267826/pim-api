import { format, parseISO } from 'date-fns'

/** ISO → datetime-local 输入值（本地时区）'yyyy-MM-ddTHH:mm' */
export function toLocalInputValue(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return format(parseISO(iso), "yyyy-MM-dd'T'HH:mm")
  } catch {
    return ''
  }
}

/** datetime-local 输入值 → ISO（UTC，带 Z） */
export function fromLocalInputValue(value: string): string | null {
  if (!value) return null
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? null : d.toISOString()
}

/** 显示时间：同日 HH:mm，跨日 MM-dd HH:mm（相对 now） */
export function formatTime(iso: string, now: Date = new Date()): string {
  const d = parseISO(iso)
  return format(d, sameDay(d, now) ? 'HH:mm' : 'MM-dd HH:mm')
}

/** 显示日期范围：09-26 09:00 ~ 11:00 或跨日 09-26 09:00 ~ 09-27 11:00 */
export function formatRange(startIso: string, endIso: string): string {
  const s = parseISO(startIso)
  const e = parseISO(endIso)
  return sameDay(s, e)
    ? `${format(s, 'MM-dd HH:mm')} ~ ${format(e, 'HH:mm')}`
    : `${format(s, 'MM-dd HH:mm')} ~ ${format(e, 'MM-dd HH:mm')}`
}

/**
 * 只有一个起点的时间段显示（例如「正在计时」的开放段）：
 * 左端与 {@link formatRange} 完全同格式（MM-dd HH:mm），右端用调用方给的文案（如「进行中」）。
 * 这样开放段与已闭合段在列表里看起来是同一套格式，不会出现 ISO 原样串。
 */
export function formatOpenRange(startIso: string, endLabel: string): string {
  return `${format(parseISO(startIso), 'MM-dd HH:mm')} ~ ${endLabel}`
}

function sameDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  )
}

/** TimeSpan "c" 格式（01:30:00）→ 中文时长（1 小时 30 分） */
export function formatDurationC(value: string | null | undefined): string {
  if (!value) return ''
  const m = /^(\d+):(\d{2})(?::(\d{2}))?$/.exec(value)
  if (!m) return value
  const [, h, min] = m
  const hours = Number(h)
  const minutes = Number(min)
  if (hours && minutes) return `${hours} 小时 ${minutes} 分`
  if (hours) return `${hours} 小时`
  return `${minutes} 分钟`
}

/** 分钟 → hh:mm:ss（后端 estimatedDuration 接受 TimeSpan "c" 格式） */
export function minutesToDurationC(minutes: number): string {
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:00`
}

/** ISO8601 时长（PT1H30M）或 hh:mm:ss → 分钟（解析失败返回 null） */
export function durationToMinutes(value: string | null | undefined): number | null {
  if (!value) return null
  const iso = /^PT(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?$/.exec(value)
  if (iso) return Number(iso[1] ?? 0) * 60 + Math.round(Number(iso[2] ?? 0))
  const c = /^(\d+):(\d{2})(?::(\d{2}))?$/.exec(value)
  if (c) return Number(c[1]) * 60 + Number(c[2])
  return null
}

/**
 * 任意 ISO（含本地偏移 +08:00）→ UTC ISO（带 Z）。
 * 后端 DateTime 解析不接受 "+08:00" 形式（返回 500），查询参数一律用 Z 形式。
 */
export function toUtcIso(value: string | null | undefined): string | null {
  if (!value) return null
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? null : d.toISOString()
}

/** 日期 → 该日业务日口径的 ISO 窗口参数（日历查询用本地零点即可，业务日仅 PC/移动域） */
export function dayStartIso(d: Date): string {
  const x = new Date(d)
  x.setHours(0, 0, 0, 0)
  return x.toISOString()
}

export function dayEndIso(d: Date): string {
  const x = new Date(d)
  x.setHours(23, 59, 59, 999)
  return x.toISOString()
}

/** 秒数 → 中文时长（"2 小时 5 分"/"48 分钟"） */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || seconds <= 0) return '0 分钟'
  const total = Math.round(seconds)
  if (total < 60) return `${total} 秒`
  // 秒按四舍五入并入分；天/时/分从最高非零段起连续显示，尾随零段省略
  const totalMin = Math.round(total / 60)
  const d = Math.floor(totalMin / 1440)
  const h = Math.floor((totalMin % 1440) / 60)
  const m = totalMin % 60
  if (d > 0) return m > 0 ? `${d} 天 ${h} 时 ${m} 分` : `${d} 天 ${h} 时`
  if (h > 0) return m > 0 ? `${h} 小时 ${m} 分` : `${h} 小时`
  return `${m} 分钟`
}
