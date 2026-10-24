/*
 * 「分类时间线」纯计算层（无 React 依赖，便于单测与复用）。
 *
 * 关键约束（交接规格）：所有时刻一律按 +08:00 墙钟解释，禁止使用浏览器本地时区；
 * 否则非 UTC+8 环境下整张图会平移。因此这里全部用「UTC 毫秒 + 固定 8 小时偏移」
 * 手工换算，不调用任何依赖本地时区的 API（getHours/toLocaleString 等）。
 */

/** 业务日墙钟时区偏移（分钟）：Asia/Shanghai 恒为 +08:00，无夏令时 */
export const WALL_CLOCK_OFFSET_MINUTES = 8 * 60

const MS_PER_MINUTE = 60_000
const MINUTES_PER_DAY = 24 * 60

export interface TimelineRecordInput {
  start?: string | null
  end?: string | null
  durationMinutes?: number | string | null
  appName?: string | null
  windowTitle?: string | null
  categoryName?: string | null
  categoryColor?: string | null
}

/** 归一化后的有效记录（墙钟分钟均相对当天 00:00，范围 0..1440） */
export interface NormalizedRecord {
  /** 唯一键（原索引，用于 React key 与稳定排序） */
  key: string
  appName: string
  windowTitle: string | null
  categoryName: string
  categoryColor: string
  /** 墙钟起止分钟（相对该记录所属业务日 00:00） */
  startMinutes: number
  endMinutes: number
  /** 时长（分钟，可能为小数） */
  durationMinutes: number
  /** 便于弹窗直接展示的时刻文案 */
  startLabel: string
  endLabel: string
  /** 用于清单排序的原始 UTC 毫秒 */
  startMs: number
}

/** 跨小时切分后的横条 */
export interface TimelineBar {
  key: string
  recordKey: string
  hour: number
  /** 段在该小时内的起止分钟（0..60） */
  offsetMinutes: number
  spanMinutes: number
  categoryColor: string
  /** 该段是否与相邻小时的段首尾相接（用于渲染时拼接圆角） */
  continuesFromPrevHour: boolean
  continuesToNextHour: boolean
  record: NormalizedRecord
}

export interface CategoryStat {
  categoryName: string
  color: string
  minutes: number
  /** 小时数，保留 1 位小数由展示层处理；此处给原始值 */
  hours: number
}

export interface TimelineModel {
  /** 有效记录数（原始条数，非拆分后横条数） */
  recordCount: number
  totalMinutes: number
  focusMinutes: number
  /** 专注率整数百分比（总时长为 0 时为 0） */
  focusRate: number
  categories: CategoryStat[]
  /** hour(0..23) → 该小时的横条 */
  barsByHour: Map<number, TimelineBar[]>
  /** 有数据的小时（升序） */
  activeHours: number[]
  /** 升序记录清单（弹窗用） */
  records: NormalizedRecord[]
}

/**
 * 解析后端时间戳为 UTC 毫秒。
 * 后端形如 2026-09-27T03:03:37.0400000+00:00（7 位小数）——JS Date 只接受 3 位毫秒，
 * 故先规整小数位再解析。无法解析返回 null（调用方据此跳过该记录）。
 */
export function parseUtcMs(value: string | null | undefined): number | null {
  if (typeof value !== 'string' || value.trim() === '') return null
  const raw = value.trim()
  // 已带时区（Z 或 ±HH:MM）；无时区的按 UTC 解释（后端契约恒为 UTC）
  const matched = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})?$/.exec(raw)
  if (matched) {
    const [, date, hh, mm, ss, frac, zone] = matched
    const ms = frac ? Number(`0.${frac}`) * 1000 : 0
    const iso = `${date}T${hh}:${mm}:${ss}.${String(Math.round(ms)).padStart(3, '0')}${zone ?? 'Z'}`
    const parsed = Date.parse(iso)
    return Number.isNaN(parsed) ? null : parsed
  }
  const fallback = Date.parse(raw)
  return Number.isNaN(fallback) ? null : fallback
}

/** UTC 毫秒 → 墙钟（+08:00）当天的第几分钟；支持跨日（可能 <0 或 >1440） */
export function toWallClockMinutes(utcMs: number): number {
  const shifted = utcMs + WALL_CLOCK_OFFSET_MINUTES * MS_PER_MINUTE
  const dayMs = 24 * 60 * MS_PER_MINUTE
  const withinDay = ((shifted % dayMs) + dayMs) % dayMs
  return withinDay / MS_PER_MINUTE
}

/** 分钟 → HH:mm（跨日时对 24h 取模，保证落在 00:00–23:59） */
export function formatWallClock(minutes: number): string {
  const total = Math.round(minutes)
  const normalized = ((total % MINUTES_PER_DAY) + MINUTES_PER_DAY) % MINUTES_PER_DAY
  const h = Math.floor(normalized / 60)
  const m = normalized % 60
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`
}

/** 专注类判定（当前口径）：分类名包含下列关键词之一 */
export const FOCUS_KEYWORDS = ['工作', '编程', '文档', '学习', '邮件', '终端', '办公'] as const

export function isFocusCategory(categoryName: string): boolean {
  return FOCUS_KEYWORDS.some((k) => categoryName.includes(k))
}

const FALLBACK_COLOR = '#64748B'

/**
 * 记录 → 模型。无效记录（缺 start/end、不可解析、end <= start）直接忽略。
 * 时长为 0 / 缺失时回退为 (end - start)。
 */
export function buildTimelineModel(input: readonly TimelineRecordInput[] | null | undefined): TimelineModel {
  const records: NormalizedRecord[] = []

  const list = Array.isArray(input) ? input : []
  list.forEach((raw, index) => {
    const startMs = parseUtcMs(raw?.start)
    const endMs = parseUtcMs(raw?.end)
    if (startMs == null || endMs == null || endMs <= startMs) return

    const declared = typeof raw.durationMinutes === 'number' ? raw.durationMinutes : Number(raw.durationMinutes)
    const derived = (endMs - startMs) / MS_PER_MINUTE
    const durationMinutes = Number.isFinite(declared) && declared > 0 ? declared : derived
    if (!Number.isFinite(durationMinutes) || durationMinutes <= 0) return

    const startMinutes = toWallClockMinutes(startMs)
    const endMinutes = toWallClockMinutes(endMs)
    records.push({
      key: `r${index}`,
      appName: (raw.appName ?? '').trim() || '未知应用',
      windowTitle: raw.windowTitle ? String(raw.windowTitle) : null,
      categoryName: (raw.categoryName ?? '').trim() || '其他',
      categoryColor: raw.categoryColor || FALLBACK_COLOR,
      startMinutes,
      endMinutes,
      durationMinutes,
      startLabel: formatWallClock(startMinutes),
      endLabel: formatWallClock(endMinutes),
      startMs,
    })
  })

  /* 升序清单（弹窗用） */
  const sorted = [...records].sort((a, b) => a.startMs - b.startMs)

  /* 分类聚合（缺失算「其他」），按时长降序 */
  const agg = new Map<string, CategoryStat>()
  for (const r of records) {
    const prev = agg.get(r.categoryName)
    if (prev) prev.minutes += r.durationMinutes
    else agg.set(r.categoryName, { categoryName: r.categoryName, color: r.categoryColor, minutes: r.durationMinutes, hours: 0 })
  }
  const categories = [...agg.values()]
    .map((c) => ({ ...c, hours: c.minutes / 60 }))
    .sort((a, b) => b.minutes - a.minutes || a.categoryName.localeCompare(b.categoryName))

  const totalMinutes = records.reduce((sum, r) => sum + r.durationMinutes, 0)
  const focusMinutes = records.reduce((sum, r) => (isFocusCategory(r.categoryName) ? sum + r.durationMinutes : sum), 0)
  const focusRate = totalMinutes > 0 ? Math.round((focusMinutes / totalMinutes) * 100) : 0

  /* 按小时切分 */
  const barsByHour = new Map<number, TimelineBar[]>()
  for (const r of records) {
    // 以墙钟区间切分；跨日（end < start）按 +24h 处理
    let s = r.startMinutes
    let e = r.endMinutes
    if (e <= s) e += MINUTES_PER_DAY

    let cursor = s
    let guard = 0
    while (cursor < e && guard < 48) {
      guard += 1
      const hour = Math.floor(cursor / 60) % 24
      const hourEnd = (Math.floor(cursor / 60) + 1) * 60
      const segEnd = Math.min(e, hourEnd)
      const offsetMinutes = cursor - Math.floor(cursor / 60) * 60
      const spanMinutes = segEnd - cursor
      if (spanMinutes > 0) {
        const list = barsByHour.get(hour) ?? []
        list.push({
          key: `${r.key}-h${hour}-${Math.round(cursor)}`,
          recordKey: r.key,
          hour,
          offsetMinutes,
          spanMinutes,
          categoryColor: r.categoryColor,
          continuesFromPrevHour: offsetMinutes <= 0.001,
          continuesToNextHour: segEnd >= hourEnd - 0.001,
          record: r,
        })
        barsByHour.set(hour, list)
      }
      cursor = segEnd
    }
  }

  const activeHours = [...barsByHour.keys()].sort((a, b) => a - b)

  return {
    recordCount: records.length,
    totalMinutes,
    focusMinutes,
    focusRate,
    categories,
    barsByHour,
    activeHours,
    records: sorted,
  }
}
