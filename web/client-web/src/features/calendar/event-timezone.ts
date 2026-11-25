export const CALENDAR_DISPLAY_TIME_ZONE = 'Asia/Shanghai'
export const CALENDAR_DISPLAY_LABEL = '北京'
export const CALENDAR_DISPLAY_OFFSET_MS = 8 * 60 * 60 * 1000

export interface WallTimeCandidate {
  instant: string
  offsetMinutes: number
}

export type WallTimeResolution =
  | { status: 'invalid'; candidates: [] }
  | { status: 'gap'; candidates: [] }
  | { status: 'valid'; candidates: [WallTimeCandidate] }
  | { status: 'ambiguous'; candidates: WallTimeCandidate[] }

const formatterCache = new Map<string, Intl.DateTimeFormat>()

function formatter(timeZoneId: string): Intl.DateTimeFormat {
  let value = formatterCache.get(timeZoneId)
  if (!value) {
    value = new Intl.DateTimeFormat('en-CA', {
      timeZone: timeZoneId,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hourCycle: 'h23',
    })
    formatterCache.set(timeZoneId, value)
  }
  return value
}

function partsAt(instant: Date, timeZoneId: string): Record<string, string> {
  return Object.fromEntries(formatter(timeZoneId).formatToParts(instant).map((part) => [part.type, part.value]))
}

function formatParts(parts: Record<string, string>): string {
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`
}

export function isValidTimeZoneId(timeZoneId: string): boolean {
  if (!timeZoneId.trim()) return false
  try {
    formatter(timeZoneId.trim()).format(0)
    return true
  } catch {
    return false
  }
}

export function formatInstantInZone(instantIso: string, timeZoneId: string): string {
  const instant = new Date(instantIso)
  if (Number.isNaN(instant.getTime()) || !isValidTimeZoneId(timeZoneId)) return ''
  return formatParts(partsAt(instant, timeZoneId.trim()))
}

function parseWallValue(value: string): { year: number; month: number; day: number; hour: number; minute: number; utcMs: number } | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value)
  if (!match) return null
  const [, yearText, monthText, dayText, hourText, minuteText] = match
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  const hour = Number(hourText)
  const minute = Number(minuteText)
  const utcMs = Date.UTC(year, month - 1, day, hour, minute)
  const check = new Date(utcMs)
  if (check.getUTCFullYear() !== year || check.getUTCMonth() + 1 !== month || check.getUTCDate() !== day || hour > 23 || minute > 59)
    return null
  return { year, month, day, hour, minute, utcMs }
}

function offsetAt(instantMs: number, timeZoneId: string): number {
  const instant = new Date(instantMs)
  const parts = partsAt(instant, timeZoneId)
  const wallAsUtc = Date.UTC(
    Number(parts.year),
    Number(parts.month) - 1,
    Number(parts.day),
    Number(parts.hour),
    Number(parts.minute),
    Number(parts.second),
  )
  return Math.round((wallAsUtc - Math.floor(instantMs / 1000) * 1000) / 60_000)
}

export function resolveWallTime(value: string, timeZoneId: string): WallTimeResolution {
  const wall = parseWallValue(value)
  if (!wall || !isValidTimeZoneId(timeZoneId)) return { status: 'invalid', candidates: [] }

  const offsets = new Set<number>()
  for (let hours = -48; hours <= 48; hours += 3) {
    offsets.add(offsetAt(wall.utcMs + hours * 60 * 60_000, timeZoneId.trim()))
  }

  const candidates = [...offsets]
    .map((offsetMinutes) => ({
      instant: new Date(wall.utcMs - offsetMinutes * 60_000).toISOString(),
      offsetMinutes,
    }))
    .filter((candidate) => formatInstantInZone(candidate.instant, timeZoneId) === value)
    .sort((left, right) => Date.parse(left.instant) - Date.parse(right.instant))
  const unique = candidates.filter((candidate, index) => candidates.findIndex((other) => other.instant === candidate.instant) === index)

  if (unique.length === 0) return { status: 'gap', candidates: [] }
  if (unique.length === 1) return { status: 'valid', candidates: [unique[0]] }
  return { status: 'ambiguous', candidates: unique }
}

export function findFoldChoice(value: string, timeZoneId: string, instant: string): string | null {
  const resolution = resolveWallTime(value, timeZoneId)
  if (resolution.status !== 'ambiguous') return null
  const instantMs = Date.parse(instant)
  return resolution.candidates.find((candidate) => Date.parse(candidate.instant) === instantMs)?.instant ?? null
}

export function chooseWallTimeCandidate(
  resolution: WallTimeResolution,
  selectedInstant: string | null,
): WallTimeCandidate | null {
  if (resolution.status === 'valid') return resolution.candidates[0]
  if (resolution.status !== 'ambiguous' || !selectedInstant) return null
  return resolution.candidates.find((candidate) => candidate.instant === selectedInstant) ?? null
}

export function formatAbsoluteInstant(instant: string): string {
  const date = new Date(instant)
  return Number.isNaN(date.getTime()) ? '' : date.toISOString()
}

export function offsetLabel(offsetMinutes: number): string {
  const sign = offsetMinutes < 0 ? '-' : '+'
  const absolute = Math.abs(offsetMinutes)
  return `UTC${sign}${String(Math.floor(absolute / 60)).padStart(2, '0')}:${String(absolute % 60).padStart(2, '0')}`
}

export function toCalendarAxisValue(instantIso: string): string {
  const local = formatInstantInZone(instantIso, CALENDAR_DISPLAY_TIME_ZONE)
  if (!local) return instantIso
  const date = new Date(instantIso)
  const seconds = String(date.getUTCSeconds()).padStart(2, '0')
  const milliseconds = String(date.getUTCMilliseconds()).padStart(3, '0')
  return `${local}:${seconds}.${milliseconds}Z`
}

export function calendarAxisToInstant(date: Date): Date {
  return new Date(date.getTime() - CALENDAR_DISPLAY_OFFSET_MS)
}

export function calendarAxisRangeForToday(nowMs = Date.now()): { start: string; end: string } {
  const displayNow = new Date(nowMs + CALENDAR_DISPLAY_OFFSET_MS)
  const date = displayNow.toISOString().slice(0, 10)
  const startMs = Date.parse(`${date}T00:00:00.000Z`) - CALENDAR_DISPLAY_OFFSET_MS
  return {
    start: new Date(startMs).toISOString(),
    end: new Date(startMs + 24 * 60 * 60_000).toISOString(),
  }
}

export function addDateDays(value: string, days: number): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return ''
  const date = new Date(`${value}T00:00:00.000Z`)
  if (Number.isNaN(date.getTime())
    || date.toISOString().slice(0, 10) !== value
    || !Number.isInteger(days)) return ''
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

export function formatDateInCalendarZone(instantIso: string): string {
  return formatInstantInZone(instantIso, CALENDAR_DISPLAY_TIME_ZONE).slice(0, 10)
}
