/*
 * 列表视图（自定义无限下滑列表）的数据模型（纯函数，无 React 依赖）。
 *
 * 为什么不用 FullCalendar 的 listWeek 做无限加载：
 * - listWeek 只渲染自身可见日期窗口内的事件，没有「向前增长窗口」的 API；
 * - 后端 /calendar/events 支持大窗口 + page/pageSize（PagedResult.totalCount 可靠），
 *   且实测不按开始时间排序 —— 正好由这里统一归一、排序、按 +08:00 业务日分组。
 *
 * 时刻一律按 +08:00 墙钟解释（UTC 毫秒 + 固定偏移），不使用浏览器本地时区。
 */
import type { EventResponse, LayerItem } from '../types'
import { isRepeatedEvent } from '../rrule'

/** 业务日墙钟时区偏移：+08:00（与时间线/日历页同一口径） */
export const CST_OFFSET_MS = 8 * 3600_000

export interface ListRowItem {
  key: string
  kind: 'event' | 'layer'
  layer: string | null
  title: string
  /** +08:00 墙钟毫秒（排序与分组依据） */
  startMs: number
  endMs: number | null
  allDay: boolean
  cancelled: boolean
  repeated: boolean
  color: string
  event?: EventResponse
}

export interface EventListSection {
  /** +08:00 业务日 YYYY-MM-DD */
  day: string
  /** 星期一 */
  weekdayLabel: string
  /** 2026年9月28日 */
  dateLabel: string
  items: ListRowItem[]
}

export interface BuildListOptions {
  /** 仅显示该日历本（URL calendarId 参数；不传 = 全部） */
  calendarId?: string
  /** 图层显隐（与网格视图同一套 chips） */
  layerToggles?: Record<string, boolean>
}

/** UTC 毫秒 → +08:00 墙钟毫秒偏移下的 Date（配合 timeZone:'UTC' 取墙钟字段） */
function wallDate(ms: number): Date {
  return new Date(ms + CST_OFFSET_MS)
}

/** 墙钟 HH:mm（时区无关：直接取偏移后的 UTC 字段） */
export function wallClockTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return ''
  return wallDate(ms).toISOString().slice(11, 16)
}

/** 业务日 → 星期X / 2026年9月28日（Intl UTC，避免浏览器时区漂移） */
export function dayLabels(day: string): { weekdayLabel: string; dateLabel: string } {
  const d = new Date(`${day}T00:00:00Z`)
  const weekdayLabel = Number.isNaN(d.getTime())
    ? ''
    : d.toLocaleDateString('zh-CN', { weekday: 'long', timeZone: 'UTC' })
  const dateLabel = Number.isNaN(d.getTime())
    ? day
    : d.toLocaleDateString('zh-CN', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' })
  return { weekdayLabel, dateLabel }
}

/**
 * 归一 + 过滤 + 按 +08:00 业务日分组（组内按开始时间升序，同刻按标题）。
 * events 传无限加载累计的分页结果；layerItems 传图层查询结果（按 toggles 过滤）。
 */
export function buildEventListSections(
  events: readonly EventResponse[] | undefined,
  layerItems: readonly LayerItem[] | undefined,
  opts: BuildListOptions = {},
): EventListSection[] {
  const rows: ListRowItem[] = []
  const toggles = opts.layerToggles

  for (const ev of events ?? []) {
    if (opts.calendarId && ev.calendarId !== opts.calendarId) continue
    const startMs = Date.parse(ev.dtStart)
    if (Number.isNaN(startMs)) continue
    rows.push({
      key: `event:${ev.id}:${ev.recurrenceId ?? ''}`,
      kind: 'event',
      layer: null,
      title: ev.title || '（无标题）',
      startMs,
      endMs: Date.parse(ev.dtEnd) || null,
      allDay: Boolean(ev.isAllDay),
      cancelled: Boolean(ev.isCancelled ?? ev.status === 'Cancelled'),
      repeated: isRepeatedEvent(ev),
      color: '#3B82F6',
      event: ev,
    })
  }

  for (const item of layerItems ?? []) {
    if (toggles && toggles[item.layer] === false) continue
    const startMs = Date.parse(item.startsAt)
    if (Number.isNaN(startMs)) continue
    rows.push({
      key: `layer:${item.id}`,
      kind: 'layer',
      layer: item.layer,
      title: item.title,
      startMs,
      endMs: Date.parse(item.endsAt) || null,
      allDay: false,
      cancelled: item.status === 'Cancelled',
      repeated: false,
      color: item.color || '#64748B',
    })
  }

  rows.sort((a, b) => a.startMs - b.startMs || a.title.localeCompare(b.title, 'zh-CN'))

  const sections = new Map<string, ListRowItem[]>()
  for (const row of rows) {
    const day = wallDate(row.startMs).toISOString().slice(0, 10)
    const bucket = sections.get(day)
    if (bucket) bucket.push(row)
    else sections.set(day, [row])
  }

  return [...sections.entries()].map(([day, items]) => ({ day, ...dayLabels(day), items }))
}
