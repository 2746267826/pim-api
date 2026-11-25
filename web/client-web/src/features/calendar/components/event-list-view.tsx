import { useEffect, useMemo, useRef } from 'react'
import { Inbox } from 'lucide-react'
import { useLayers, useEventsInfinite } from '../queries'
import type { LayerToggles } from '../calendar-visibility'
import type { CalendarBook, EventResponse } from '../types'
import { summarizeLines } from '@/lib/text'
import { EmptyState, Skeleton } from '@/components/ui'
import { cn } from '@/lib/utils'
import { buildEventListSections, wallClockTime, type ListRowItem } from './event-list-model'
import { StaggerItem } from '@/components/motion/primitives'
import { addDateDays, CALENDAR_DISPLAY_LABEL } from '../event-timezone'

/*
 * 列表视图（自定义，替代 FullCalendar listWeek）：
 * - 数据 = 固定 366 天大窗口分页装填，totalCount 证明加载完全；
 * - 滚动容器自带，接近底部（sentinel 进入视口前 320px）自动 fetchNextPage；
 * - 分组/排序/文案由 event-list-model（纯函数）负责；
 * - 图层（任务段/习惯/可用时间/智能占位）只取前 60 天——layers 接口无分页，
 *   习惯类条目按日展开，窗口取满 366 天会显著放大响应。
 */
export const LIST_HORIZON_DAYS = 366
const LAYER_WINDOW_DAYS = 60

export function buildEventListHorizon(anchorStart: string) {
  return {
    start: anchorStart,
    end: addDaysIso(anchorStart, LIST_HORIZON_DAYS),
  }
}

export function EventListView({
  anchorStart,
  calendarId,
  calendars,
  layerToggles,
  outlookOnly,
  onOpenEvent,
}: {
  /** 列表起点（锚定周周一，来自隐藏 FC 的 datesSet） */
  anchorStart: string
  calendarId?: string
  calendars: CalendarBook[]
  layerToggles: LayerToggles
  outlookOnly: boolean
  onOpenEvent: (ev: EventResponse) => void
}) {
  const horizon = useMemo(() => buildEventListHorizon(anchorStart), [anchorStart])
  const eventsQ = useEventsInfinite(horizon.start, horizon.end)
  const { data: layersData } = useLayers(
    anchorStart,
    addDaysIso(anchorStart, LAYER_WINDOW_DAYS),
    'task-segments,habits,availability,ai-placeholders',
    outlookOnly,
  )

  const events = useMemo(() => eventsQ.data?.pages.flatMap((p) => p.items) ?? [], [eventsQ.data])
  const sections = useMemo(
    () => buildEventListSections(events, layersData?.items, { calendarId, layerToggles: { ...layerToggles } }),
    [events, layersData, calendarId, layerToggles],
  )
  const totalCount = eventsQ.data?.pages[0]?.totalCount ?? 0
  const loadedCount = eventsQ.data?.pages.reduce((a, p) => a + p.items.length, 0) ?? 0
  const hasMore = Boolean(eventsQ.hasNextPage)
  const initialLoading = eventsQ.isLoading

  /* 行入场 stagger 的全局序号（跨日分组连续计数，封顶逻辑在 StaggerItem 内） */
  const rowOffsets = useMemo(() => {
    let n = 0
    return sections.map((s) => {
      const offset = n
      n += s.items.length
      return offset
    })
  }, [sections])

  /* 无限下滑：sentinel 进入滚动容器视口前 320px → 请求下一页 */
  const scrollRef = useRef<HTMLDivElement>(null)
  const sentinelRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const scroller = scrollRef.current
    const sentinel = sentinelRef.current
    if (!scroller || !sentinel) return
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting) && eventsQ.hasNextPage && !eventsQ.isFetchingNextPage) {
          void eventsQ.fetchNextPage()
        }
      },
      { root: scroller, rootMargin: '320px' },
    )
    io.observe(sentinel)
    return () => io.disconnect()
  }, [eventsQ.hasNextPage, eventsQ.isFetchingNextPage, eventsQ.fetchNextPage])

  return (
    <div ref={scrollRef} className="skin-gcal min-w-0 flex-1 overflow-y-auto select-none">
      <div className="overflow-hidden rounded-card border border-border bg-bg shadow-card">
        {initialLoading ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 6 }, (_, i) => (
              <Skeleton key={i} className="h-16" />
            ))}
          </div>
        ) : sections.length === 0 ? (
          <div className="py-16">
            <EmptyState
              icon={Inbox}
              title="没有日程"
              description="未来 366 天内没有可显示的日程。"
            />
          </div>
        ) : (
          sections.map((s, si) => (
            <section key={s.day}>
              <header className="flex items-baseline justify-between border-b border-border bg-surface px-4 py-2">
                <span className="text-[13px] font-semibold text-text-1">{s.dateLabel}</span>
                <span className="tnum text-xs text-text-3">{s.weekdayLabel}</span>
              </header>
              <div className="divide-y divide-border/70">
                {s.items.map((item, ii) => (
                  <StaggerItem key={item.key} index={rowOffsets[si] + ii}>
                    <ListRow item={item} calendars={calendars} onOpenEvent={onOpenEvent} />
                  </StaggerItem>
                ))}
              </div>
            </section>
          ))
        )}

        {!initialLoading && sections.length > 0 && (
          <div
            ref={sentinelRef}
            className="flex items-center justify-center gap-2 px-4 py-4 text-xs text-text-4"
          >
            {eventsQ.isFetchingNextPage ? (
              <span>正在加载更多日程…</span>
            ) : hasMore ? (
              <span>下滑自动加载（已装 {loadedCount} / {totalCount} 条）</span>
            ) : (
              <span>已全部加载 · 共 {totalCount} 条日程</span>
            )}
          </div>
        )}
      </div>
      {eventsQ.isError && (
        <p className="px-4 py-3 text-xs text-crit">
          加载日程失败：{eventsQ.error instanceof Error ? eventsQ.error.message : '网络异常'}
        </p>
      )}
    </div>
  )
}

function ListRow({
  item,
  calendars,
  onOpenEvent,
}: {
  item: ListRowItem
  calendars: CalendarBook[]
  onOpenEvent: (ev: EventResponse) => void
}) {
  const ev = item.event
  const lines = summarizeLines(ev?.description, { exclude: [ev?.location, ev?.title], maxLines: 5 })
  const when = item.allDay
    ? allDayRange(ev?.allDayStartDate, ev?.allDayEndDateExclusive)
    : `${CALENDAR_DISPLAY_LABEL} ${displayClock(ev?.displayLocalStart, ev?.dtStart)}–${displayClock(ev?.displayLocalEnd, ev?.dtEnd)}`
  const localTime = ev && !item.allDay
    ? ev.timeZoneResolution === 'resolved' && ev.localStart && ev.localEnd
      ? `${zoneLabel(ev.timeZoneId)} ${formatLocalClock(ev.localStart)}–${formatLocalClock(ev.localEnd)}`
      : `时区未知 · ${ev.displayTimeZoneId ?? 'Asia/Shanghai'} ${displayClock(ev.displayLocalStart, ev.dtStart)}–${displayClock(ev.displayLocalEnd, ev.dtEnd)}`
    : null
  const interactive = item.kind === 'event' && Boolean(ev)
  const color =
    item.kind === 'event'
      ? (calendars.find((c) => c.id === ev?.calendarId)?.color ?? item.color)
      : item.color

  return (
    <div
      className={cn(
        'flex items-start gap-4 px-4 py-3',
        interactive && 'cursor-pointer transition-colors hover:bg-surface',
      )}
      onClick={interactive && ev ? () => onOpenEvent(ev) : undefined}
      role={interactive ? 'button' : undefined}
      onKeyDown={
        interactive
          ? (e) => {
              if ((e.key === 'Enter' || e.key === ' ') && ev) {
                e.preventDefault()
                onOpenEvent(ev)
              }
            }
          : undefined
      }
      tabIndex={interactive ? 0 : undefined}
    >
      <span className="tnum w-[150px] shrink-0 pt-0.5 text-[12px] leading-5 text-text-2">
        <span className="block">{when}</span>
        {localTime && <span className="block text-[11px] leading-4 text-text-4">{localTime}</span>}
      </span>
      <div className="min-w-0 flex-1">
        <p
          className={cn(
            'flex items-center gap-1.5 text-[13px] font-semibold',
            item.cancelled ? 'text-text-4 line-through' : 'text-text-1',
          )}
        >
          <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: color }} aria-hidden />
          {item.repeated && <span className="font-normal text-text-3">↻</span>}
          <span className="truncate">{item.title}</span>
        </p>
        {lines.map((line, i) => (
          <p key={i} className="mt-0.5 truncate text-[12px] leading-4 text-text-4">
            {line}
          </p>
        ))}
      </div>
    </div>
  )
}

function displayClock(local: string | null | undefined, fallback: string | null | undefined): string {
  const value = local || fallback || ''
  return local ? value.slice(5, 16).replace('T', ' ') : wallClockTime(value)
}

function formatLocalClock(value: string): string {
  return value.slice(5, 16).replace('T', ' ')
}

function zoneLabel(timeZoneId: string | null): string {
  if (timeZoneId === 'Asia/Shanghai') return '北京'
  if (timeZoneId === 'America/New_York') return '纽约'
  if (timeZoneId === 'America/Los_Angeles') return '洛杉矶'
  return timeZoneId ?? '当地'
}

function allDayRange(start: string | null | undefined, endExclusive: string | null | undefined): string {
  if (!start || !endExclusive) return '全天（日期未知）'
  const lastDate = addDateDays(endExclusive, -1)
  return start === lastDate ? `${start} 全天` : `${start}–${lastDate} 全天`
}

/** ISO 时刻加 N 天（窗口推算；入参为完整 ISO 时间戳，返回完整 ISO） */
function addDaysIso(iso: string, days: number): string {
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return iso
  return new Date(ms + days * 86_400_000).toISOString()
}
