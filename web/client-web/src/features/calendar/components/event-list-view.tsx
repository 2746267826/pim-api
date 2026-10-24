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

/*
 * 列表视图（自定义，替代 FullCalendar listWeek）：
 * - 数据 = 固定大窗口（约两年）分页装填，totalCount 证明加载完全；
 * - 滚动容器自带，接近底部（sentinel 进入视口前 320px）自动 fetchNextPage；
 * - 分组/排序/文案由 event-list-model（纯函数）负责；
 * - 图层（任务段/习惯/可用时间/智能占位）只取前 60 天——layers 接口无分页，
 *   习惯类条目按日展开，窗口取整两年会显著放大响应。
 */
const LIST_HORIZON_DAYS = 730
const LAYER_WINDOW_DAYS = 60

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
  const horizonEnd = useMemo(() => addDaysIso(anchorStart, LIST_HORIZON_DAYS), [anchorStart])
  const eventsQ = useEventsInfinite(anchorStart, horizonEnd)
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
              description="该时间范围内没有可显示的日程，向后滚动会自动加载更远的内容。"
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
    ? '全天'
    : `${wallClockTime(ev?.dtStart)} - ${wallClockTime(ev?.dtEnd ?? undefined) || ''}`.trim()
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
      <span className="tnum w-[96px] shrink-0 pt-0.5 text-[13px] text-text-2">{when}</span>
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

/** ISO 时刻加 N 天（窗口推算；入参为完整 ISO 时间戳，返回完整 ISO） */
function addDaysIso(iso: string, days: number): string {
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return iso
  return new Date(ms + days * 86_400_000).toISOString()
}
