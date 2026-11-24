const formatterCache = new Map<string, Intl.DateTimeFormat>()

export function isIanaTimeZone(timeZoneId: string | null | undefined): boolean {
  const zone = timeZoneId?.trim()
  if (!zone) return false
  try {
    formatterFor(zone)
    return true
  } catch {
    return false
  }
}

function requireTimeZone(timeZoneId: string | null | undefined): string {
  const zone = timeZoneId?.trim()
  if (!zone) throw new Error('请先填写有效的 IANA 时区')
  if (!isIanaTimeZone(zone)) throw new Error('时区必须是有效的 IANA 时区')
  return zone
}

function formatterFor(timeZoneId: string): Intl.DateTimeFormat {
  const cached = formatterCache.get(timeZoneId)
  if (cached) return cached
  const formatter = new Intl.DateTimeFormat('en-CA', {
    timeZone: timeZoneId,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
  })
  formatterCache.set(timeZoneId, formatter)
  return formatter
}

interface ZonedParts {
  date: string
  time: string
  year: number
  month: number
  day: number
  hour: number
  minute: number
  second: number
}

function zonedParts(instantMs: number, timeZoneId: string): ZonedParts {
  const formatter = formatterFor(timeZoneId)
  const parts = Object.fromEntries(
    formatter.formatToParts(new Date(instantMs))
      .filter((part) => part.type !== 'literal')
      .map((part) => [part.type, Number(part.value)]),
  ) as Record<string, number>
  const date = `${String(parts.year).padStart(4, '0')}-${String(parts.month).padStart(2, '0')}-` +
    String(parts.day).padStart(2, '0')
  const time = `${String(parts.hour).padStart(2, '0')}:${String(parts.minute).padStart(2, '0')}`
  return {
    date,
    time,
    year: parts.year,
    month: parts.month,
    day: parts.day,
    hour: parts.hour,
    minute: parts.minute,
    second: parts.second,
  }
}

function offsetAt(instantMs: number, timeZoneId: string): number {
  const parts = zonedParts(instantMs, timeZoneId)
  const asUtc = Date.UTC(
    parts.year, parts.month - 1, parts.day,
    parts.hour, parts.minute, parts.second,
  )
  return asUtc - Math.floor(instantMs / 1000) * 1000
}

function wallClockAt(instantMs: number, timeZoneId: string): string {
  const parts = zonedParts(instantMs, timeZoneId)
  return `${parts.date}T${parts.time}`
}

/** ISO instant → event timezone 的 datetime-local 输入值。 */
export function toEventTimeZoneInput(
  iso: string | null | undefined,
  timeZoneId: string | null | undefined,
): string {
  const zone = timeZoneId?.trim()
  if (!iso || !zone || !isIanaTimeZone(zone)) return ''
  try {
    const instant = new Date(iso)
    if (Number.isNaN(instant.getTime())) return ''
    return wallClockAt(instant.getTime(), zone)
  } catch {
    return ''
  }
}

/**
 * Event timezone 的 datetime-local 输入值 → UTC ISO。
 * 不存在的本地时间和夏令时回拨造成的重复本地时间都 fail closed。
 */
export function fromEventTimeZoneInput(
  value: string,
  timeZoneId: string | null | undefined,
): string | null {
  if (!value) return null
  const zone = timeZoneId?.trim()
  if (!zone) throw new Error('请先填写有效的 IANA 时区')
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value)
  if (!match) throw new Error('时间格式必须是 YYYY-MM-DDTHH:mm')
  const [, yearText, monthText, dayText, hourText, minuteText] = match
  const wall = {
    year: Number(yearText),
    month: Number(monthText),
    day: Number(dayText),
    hour: Number(hourText),
    minute: Number(minuteText),
  }
  const wallMs = Date.UTC(
    wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, 0,
  )
  const checked = new Date(wallMs)
  if (
    checked.getUTCFullYear() !== wall.year ||
    checked.getUTCMonth() + 1 !== wall.month ||
    checked.getUTCDate() !== wall.day ||
    checked.getUTCHours() !== wall.hour ||
    checked.getUTCMinutes() !== wall.minute
  ) {
    throw new Error('时间不是有效的日期时间')
  }

  if (!isIanaTimeZone(zone)) {
    throw new Error('时区必须是有效的 IANA 时区')
  }

  const candidates = new Set<number>()
  const probes = [-172_800_000, -86_400_000, -43_200_000, 0, 43_200_000, 86_400_000, 172_800_000]
  const offsets = new Set(probes.map((delta) => offsetAt(wallMs + delta, zone)))
  for (const offset of offsets) {
    const candidate = wallMs - offset
    if (wallClockAt(candidate, zone) === value && offsetAt(candidate, zone) === offset)
      candidates.add(candidate)
  }
  if (candidates.size === 0)
    throw new Error('该时间在所选时区不存在，请避开夏令时跳变')
  if (candidates.size > 1)
    throw new Error('该时间在所选时区重复，无法唯一换算')
  return new Date([...candidates][0]!).toISOString()
}

export function formatEventRange(
  start: string | null | undefined,
  end: string | null | undefined,
  timeZoneId: string | null | undefined,
): string {
  if (!start || !end) {
    return `${start ? '待补结束时间' : '待补开始时间'}${start || end ? '（时间未完整）' : ''}`
  }
  const zone = timeZoneId?.trim()
  if (!zone) return '时区未知：请补全 IANA 时区后再确认创建日程'
  if (!isIanaTimeZone(zone)) return '时区无效：请核对 IANA 时区后再确认创建日程'
  try {
    requireTimeZone(zone)
    const startParts = zonedParts(new Date(start).getTime(), zone)
    const endParts = zonedParts(new Date(end).getTime(), zone)
    const range = startParts.date === endParts.date
      ? `${startParts.date} ${startParts.time}–${endParts.time}`
      : `${startParts.date} ${startParts.time} – ${endParts.date} ${endParts.time}`
    return `${range} · ${zone}`
  } catch {
    return '日程时间无效：请重新选择开始和结束时间'
  }
}
