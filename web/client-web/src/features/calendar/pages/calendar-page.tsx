import { useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/daygrid'
import timeGridPlugin from '@fullcalendar/timegrid'
import listPlugin from '@fullcalendar/list'
import interactionPlugin, { ThirdPartyDraggable } from '@fullcalendar/interaction'
import zhCnLocale from '@fullcalendar/core/locales/zh-cn'
import type { DatesSetArg, EventClickArg } from '@fullcalendar/core'
import type { EventReceiveArg } from '@fullcalendar/interaction'
import { CalendarPlus, ChevronLeft, ChevronRight, Inbox, Paintbrush } from 'lucide-react'
import { Link } from 'react-router'
import '../styles/calendar-skins.css'
import { useCalendarVisibility } from '../calendar-visibility'
import { useCalendars, useEvents, useInboxTasks, useLayers, usePlanTask, useTaskBooks } from '../queries'
import { EventEditorDialog } from '../components/event-editor-dialog'
import { isRepeatedEvent } from '../rrule'
import { EventDetailDialog } from '../components/entity-detail-dialogs'
import { TaskEditorDialog } from '../components/task-editor-dialog'
import { InboxPanel } from '../components/inbox-panel'
import { EventListView } from '../components/event-list-view'
import type { EventResponse, LayerItem } from '../types'
import { dayEndIso, dayStartIso, durationToMinutes, formatTime, toUtcIso } from '@/lib/datetime'
import { summarizeLines } from '@/lib/text'
import { Chip, Button, Drawer, DrawerContent, PageHeader, Segmented } from '@/components/ui'
import { cn } from '@/lib/utils'
import { notifyError, notifySuccess } from '@/lib/notify'

interface FcEvent {
  id: string
  title: string
  start?: string
  end?: string
  allDay?: boolean
  backgroundColor: string
  borderColor: string
  extendedProps: { kind: 'event' | 'layer'; event?: EventResponse; cancelled?: boolean; repeated?: boolean }
}

const LAYER_COLORS: Record<string, string> = {
  'task-segments': '#22C55E',
  habits: '#A855F7',
  availability: '#0EA5E9',
  'ai-placeholders': '#F97316',
}

/** 闲忙状态 → 中文徽标文案（仅非默认值显示，避免每卡都挂「忙碌」） */
const SHOW_AS_LABEL: Record<string, string> = {
  free: '空闲',
  tentative: '暂定',
  away: '离开',
  workingElsewhere: '异地工作',
}

/** 凌晨策略持久化键 */
const DAWN_STORAGE_KEY = 'pim.calendarDawn'

/** 业务日墙钟时区偏移：+08:00（与时间线组件同一口径，不依赖浏览器时区） */
const CST_OFFSET_MS = 8 * 3600_000
/** 时刻 → +08:00 墙钟分钟（0..1440） */
function wallMinutes(ms: number): number {
  const dayMs = 86_400_000
  return (((ms + CST_OFFSET_MS) % dayMs) + dayMs) % dayMs / 60_000
}

/** 墙钟分钟 → 'HH:MM:00'（FullCalendar scrollTime/slotTime 格式） */
function scrollTimeOf(minutes: number): string {
  const clamped = Math.min(Math.max(Math.round(minutes), 0), 23 * 60 + 59)
  const h = Math.floor(clamped / 60)
  const m = clamped % 60
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:00`
}

function showAsLabel(showAs: string | null | undefined): string | null {
  if (!showAs) return null
  return SHOW_AS_LABEL[showAs] ?? null
}

/** 日历视图：时间轴（单日）/ 周 / 月 / 列表 */
type CalView = 'timeline' | 'week' | 'month' | 'list'

const CAL_VIEW_TYPES: Record<CalView, string> = {
  timeline: 'timeGridDay',
  week: 'timeGridWeek',
  month: 'dayGridMonth',
  list: 'listWeek',
}

const CAL_VIEW_LABEL: Record<CalView, string> = {
  timeline: '时间轴',
  week: '周',
  month: '月',
  list: '列表',
}

/** 凌晨空档处理策略（时间轴/周视图） */
type DawnStrategy = 'smart' | 'crop' | 'strip'

const DAWN_LABEL: Record<DawnStrategy, string> = {
  smart: '智能滚动',
  crop: '裁剪空档',
  strip: '刻度标记',
}

/** 日历页（02 §日历：时间轴/周/月/列表视图 + 图层 chips + 拖选预填 + 收件箱拖入排期） */
export function CalendarPage() {
  const [params, setParams] = useSearchParams()
  const viewParam = params.get('view') ?? 'timeline'
  const view: CalView =
    viewParam === 'week' || viewParam === 'month' || viewParam === 'list' ? viewParam : 'timeline'
  const calendarId = params.get('calendarId') ?? undefined

  const { layerToggles, toggleLayer } = useCalendarVisibility()
  const { data: calendars = [] } = useCalendars('calendar')
  const { data: taskBooks = [] } = useTaskBooks()
  const { data: inboxData } = useInboxTasks()
  const plan = usePlanTask()

  const calendarRef = useRef<FullCalendar>(null)
  const [range, setRange] = useState(() => ({
    start: dayStartIso(new Date()),
    end: dayEndIso(new Date()),
    title: '',
  }))

  const eventsQuery = useEvents(range.start, range.end, layerToggles.events)
  const layersQuery = useLayers(
    range.start,
    range.end,
    'task-segments,habits,availability,ai-placeholders',
    layerToggles.outlookOnly,
    layerToggles['task-segments'] || layerToggles.habits || layerToggles.availability || layerToggles['ai-placeholders'],
  )

  /*
   * 凌晨空档处理策略（时间轴/周视图）：
   * - smart 智能滚动：渲染后自动滚到「当前时刻」（今天）或「首个日程前 30 分钟」，
   *   整天无日程滚到 08:00；凌晨行保持原样。
   * - crop 空档裁剪：没有日程的小时直接从坐标轴裁掉（slotMin/Max 按首末日程 ±1 小时），
   *   一屏装下全天；范围里无日程则保持完整 00:00–24:00。
   * - strip 刻度标记：保持完整 24 小时 + 顶部「小时分布条」，有日程的小时点亮，
   *   点击标记跳到该小时——即使滚动到别处也能看出凌晨有没有日程。
   */
  const [dawn, setDawn] = useState<DawnStrategy>(() => {
    const saved = localStorage.getItem(DAWN_STORAGE_KEY)
    return saved === 'crop' || saved === 'strip' || saved === 'smart' ? saved : 'smart'
  })
  const setDawnPersist = (next: DawnStrategy) => {
    setDawn(next)
    localStorage.setItem(DAWN_STORAGE_KEY, next)
  }

  const [eventEditor, setEventEditor] = useState<
    { mode: 'create'; initial: { start: Date; end: Date } } | { mode: 'edit'; event: EventResponse } | null
  >(null)
  /* 只读详情：点击卡片先看详情，再决定是否编辑 */
  const [detail, setDetail] = useState<EventResponse | null>(null)
  const [taskEditorOpen, setTaskEditorOpen] = useState(false)
  const [inboxOpen, setInboxOpen] = useState(false)

  /* 数据 → FullCalendar 事件源 */
  const fcEvents = useMemo<FcEvent[]>(() => {
    const list: FcEvent[] = []
    if (layerToggles.events) {
      for (const ev of eventsQuery.data?.items ?? []) {
        if (calendarId && ev.calendarId !== calendarId) continue
        const color = calendars.find((c) => c.id === ev.calendarId)?.color ?? '#3B82F6'
        list.push({
          id: `event:${ev.id}`,
          title: ev.title,
          start: ev.dtStart,
          end: ev.dtEnd,
          allDay: ev.isAllDay,
          backgroundColor: color,
          borderColor: 'transparent',
          extendedProps: { kind: 'event', event: ev, cancelled: ev.isCancelled, repeated: isRepeatedEvent(ev) },
        })
      }
    }
    const layerItems: LayerItem[] = layersQuery.data?.items ?? []
    for (const item of layerItems) {
      if (!layerToggles[item.layer]) continue
      const color = LAYER_COLORS[item.layer] ?? item.color
      list.push({
        id: item.id,
        title: item.title,
        start: item.startsAt,
        end: item.endsAt,
        backgroundColor: color,
        borderColor: color,
        extendedProps: { kind: 'layer' },
      })
    }
    return list
  }, [eventsQuery.data, layersQuery.data, layerToggles, calendars, calendarId])

  /* 视图是否带时间轴（凌晨策略仅对这类视图有意义） */
  const isTimeGrid = view === 'timeline' || view === 'week'

  /*
   * 策略 B（裁剪空档）：按当前范围日程的首末时刻裁剪坐标轴（± 1 小时，出界收紧）。
   * 仅 timeGrid 视图生效；范围内无日程（或全是全天日程）时保持完整 00:00–24:00。
   */
  const cropWindow = useMemo(() => {
    if (dawn !== 'crop' || !isTimeGrid) return null
    let min = Infinity
    let max = -Infinity
    for (const e of fcEvents) {
      if (e.allDay || !e.start) continue
      const s = Date.parse(e.start)
      const en = e.end ? Date.parse(e.end) : s + 3600_000
      if (!Number.isFinite(s) || !Number.isFinite(en) || en <= s) continue
      min = Math.min(min, s)
      max = Math.max(max, en)
    }
    if (!Number.isFinite(min) || !Number.isFinite(max)) return null
    const startMin = Math.max(0, Math.floor(wallMinutes(min) / 60) * 60 - 60)
    const endMin = Math.min(1440, Math.ceil(wallMinutes(max) / 60) * 60 + 60)
    if (endMin - startMin < 120) return { min: Math.max(0, startMin - 60), max: Math.min(1440, endMin + 60) }
    return { min: startMin, max: endMin }
  }, [dawn, isTimeGrid, fcEvents])

  /* 智能滚动的目标分钟（+08:00 墙钟）：今天 → 当前时刻前 30 分钟；非今天 → 首个日程前 30 分钟；无日程 → 08:00 */
  const smartMinutes = useMemo(() => {
    const nowMs = Date.now()
    const inRange =
      Date.parse(range.start) <= nowMs && nowMs <= Date.parse(range.end) + 86_399_000
    if (inRange) return wallMinutes(nowMs) - 30
    let first = Infinity
    for (const e of fcEvents) {
      if (e.allDay || !e.start) continue
      const s = Date.parse(e.start)
      if (Number.isFinite(s) && s >= Date.parse(range.start)) first = Math.min(first, s)
    }
    return Number.isFinite(first) ? wallMinutes(first) - 30 : 8 * 60
  }, [range.start, range.end, fcEvents])

  /*
   * 滚动执行：日历 height="auto"，时间网格由外层 .skin-gcal 容器整体滚动——
   * FullCalendar 自身的 scrollToTime/scrollTime 在这种配置下无效（内部无滚动条），
   * 必须直接滚动外层容器。
   * px/分钟 = timegrid-body 总高 ÷ 1440（body 纵向恰好覆盖 24 小时；
   * 不要用 .fc-timegrid-slot 计数——那会把标签格与通道格都算进去，差一倍）。
   */
  function scrollToWall(minutes: number) {
    const scroller = document.querySelector<HTMLElement>('.skin-gcal.overflow-y-auto')
    const body = document.querySelector<HTMLElement>('.fc-timegrid-body')
    if (!scroller || !body) return
    const scrollerRect = scroller.getBoundingClientRect()
    const bodyRect = body.getBoundingClientRect()
    const pxPerMin = bodyRect.height / 1440
    const bodyTop = bodyRect.top - scrollerRect.top + scroller.scrollTop
    scroller.scrollTo({ top: Math.max(0, bodyTop + minutes * pxPerMin - 14), behavior: 'smooth' })
  }

  /*
   * 策略 A 的滚动：仅在「视图/日期/策略」变化时执行一次，
   * 数据轮询（deferredInterval）不会重复触发，避免打断用户手动滚动。
   */
  const scrollSig = `${view}|${range.start}|${dawn}`
  const lastScrollSig = useRef<string | null>(null)
  useEffect(() => {
    if (dawn !== 'smart' || !isTimeGrid) {
      lastScrollSig.current = scrollSig
      return
    }
    if (lastScrollSig.current === scrollSig) return
    lastScrollSig.current = scrollSig
    const timer = window.setTimeout(() => scrollToWall(smartMinutes), 80)
    return () => window.clearTimeout(timer)
  }, [scrollSig, dawn, isTimeGrid, smartMinutes])

  /* 「定位」按钮：手动重新执行一次智能滚动 */
  function refocusSmart() {
    scrollToWall(smartMinutes)
  }

  /*
   * 策略 C（刻度标记）数据：24 小时占用足迹——
   * 每小时累计日程分钟数，颜色取该小时内占比最高的分类色（有日程即点亮）。
   * 时段切分逻辑与时间线一致（+08:00 墙钟、跨小时段拆分）。
   */
  const strip = useMemo(() => {
    const CST = 8 * 3600_000
    const DAY = 86_400_000
    const cells = Array.from({ length: 24 }, () => ({ minutes: 0, color: null as string | null }))
    const perHourColor = new Map<number, Map<string, number>>() // hour → (color → minutes)
    for (const e of fcEvents) {
      if (e.allDay || !e.start) continue
      const s = Date.parse(e.start)
      const en = e.end ? Date.parse(e.end) : s + 3600_000
      if (!Number.isFinite(s) || !Number.isFinite(en) || en <= s) continue
      const color = e.backgroundColor
      let cursor = Math.max(s, Date.parse(range.start))
      const cap = Math.min(en, Date.parse(range.end) + DAY)
      let guard = 0
      while (cursor < cap && guard < 200) {
        guard += 1
        const wallMs = ((cursor + CST) % DAY + DAY) % DAY
        const hour = Math.floor(wallMs / 3600_000)
        const hourEndMs = cursor + (3600_000 - (wallMs % 3600_000))
        const seg = Math.min(cap, hourEndMs) - cursor
        if (seg > 0) {
          cells[hour].minutes += seg / 60_000
          const byColor = perHourColor.get(hour) ?? new Map<string, number>()
          byColor.set(color, (byColor.get(color) ?? 0) + seg / 60_000)
          perHourColor.set(hour, byColor)
        }
        cursor = Math.min(cap, hourEndMs)
      }
    }
    // 每小时取占比最高的分类色
    for (const [hour, byColor] of perHourColor) {
      const top = [...byColor.entries()].sort((a, b) => b[1] - a[1])[0]
      if (top) cells[hour].color = top[0]
    }
    return cells
  }, [fcEvents, range.start, range.end])

  /** 点击标记 → 跳到该小时 */
  function jumpToHour(hour: number) {
    scrollToWall(hour * 60)
  }

  function setView(next: CalView) {
    setParams((p) => {
      const np = new URLSearchParams(p)
      np.set('view', next)
      return np
    })
    // initialView 仅初始化生效，切换必须走 api.changeView
    calendarRef.current?.getApi().changeView(CAL_VIEW_TYPES[next])
  }

  function onDatesSet(arg: DatesSetArg) {
    // FullCalendar 的 startStr/endStr 带本地偏移（+08:00），后端 DateTime 解析会 500 → 归一到 UTC
    const start = toUtcIso(arg.startStr) ?? dayStartIso(new Date())
    const end = toUtcIso(arg.endStr) ?? dayEndIso(new Date())
    setRange({ start, end, title: arg.view.title })
  }

  function onEventClick(arg: EventClickArg) {
    const event = arg.event.extendedProps.event as EventResponse | undefined
    if (event) setDetail(event)
  }

  /*
   * 月视图点击日期格 → 跳到该日时间轴（规格：月网格用于选日）。
   * 点日程本身仍走 onEventClick（详情），由 FullCalendar 自行区分事件与空白格。
   */
  function onDateClick(arg: { date: Date; dateStr: string; allDay: boolean; jsEvent: MouseEvent }) {
    if (view !== 'month') return
    // dateClick 在点中事件时也会触发（jsEvent.target 落在 .fc-event 内），此时交给 onEventClick
    const target = arg.jsEvent.target as HTMLElement | null
    if (target?.closest('.fc-event')) return
    calendarRef.current?.getApi().gotoDate(arg.date)
    setView('timeline')
  }

  /*
   * 卡片内容按可用高度逐级展示。行序号即行名：1=标题、2=pim-ev-when、3..7=描述行。
   * 两套排版：常规（py 8 + 标题 16 + 其余 12）与紧凑（py 1 + 标题 14 + 其余 11）。
   * 高度 < 48px 时用紧凑排版，可以多放一行（如 45min 能显示标题+时间+1 行描述）。
   * 实测：15min≈16px→1 行、30min≈31px→2 行、45min≈46px→3 行、1h≈60px→4 行、100min≈102px→7 行。
   *
   * 档位写入 data-density（应显示行数）与 data-compact（排版档位），由皮肤 CSS 控制
   * 行高与第 n+1 行起的隐藏。测量必须容忍「过渡值」：height="auto" 时 FullCalendar
   * 要先量容器宽再定槽高，eventDidMount 常发生在定稿前——首次进入（URL 直开周视图）
   * 会量到偏小的高度、只显示标题，切过周后才正常。因此挂载时先量一次（避免闪变），
   * 再用共享 ResizeObserver 兜底：布局定稿/窗口缩放引起的高度变化都会重测覆盖。
   */
  const densityObserverRef = useRef<ResizeObserver | null>(null)

  function applyEventDensity(el: HTMLElement) {
    const h = el.getBoundingClientRect().height
    const compact = h < 48
    const rows = compact
      ? Math.floor((h - 15) / 11) + 1 // 1 + 14 + 11(n-1)
      : Math.floor((h - 24) / 12) + 1 // 8 + 16 + 12(n-1)
    if (compact) el.dataset.compact = '1'
    else delete el.dataset.compact
    el.dataset.density = String(Math.max(1, Math.min(7, rows)))
  }

  function ensureDensityObserver(): ResizeObserver {
    densityObserverRef.current ??= new ResizeObserver((entries) => {
      for (const entry of entries) {
        const el = entry.target as HTMLElement
        if (!el.isConnected) {
          densityObserverRef.current?.unobserve(el)
          continue
        }
        applyEventDensity(el)
      }
    })
    return densityObserverRef.current
  }

  function onEventDidMount(arg: { el: HTMLElement; view: { type: string } }) {
    if (arg.view.type.startsWith('dayGrid')) return
    applyEventDensity(arg.el)
    ensureDensityObserver().observe(arg.el)
  }

  function onEventWillUnmount(arg: { el: HTMLElement }) {
    densityObserverRef.current?.unobserve(arg.el)
  }

  /* 拖选空白时段 → 预填日程编辑弹窗（仅时间轴视图） */
  function onSelect(info: { start: Date; end: Date; allDay: boolean }) {
    setEventEditor({ mode: 'create', initial: { start: info.start, end: info.end } })
  }

  /*
   * 收件箱任务拖入 → 排期（自动按时长估算）。
   * FullCalendar v6 外部拖入由 pointer events 驱动，必须用 ThirdPartyDraggable 包装拖拽源；
   * 同时必须提供 eventData —— 否则只有 drop 回调、没有镜像吸附与落位动画。
   * 这里把拖拽中的临时事件交给 FC 渲染（得到原生拖拽动画 + 时间槽吸附），
   * 在 eventReceive 里把它移除，改由服务端数据（排期后的任务段图层）呈现真实状态。
   */
  useEffect(() => {
    if (typeof document === 'undefined') return
    const draggable = new ThirdPartyDraggable(document.body, {
      itemSelector: '.fc-external-drag',
      eventData: (el) => {
        const duration = el.getAttribute('data-task-duration') ?? '01:00:00'
        const minutes = durationToMinutes(duration) ?? 60
        return {
          title: el.getAttribute('data-task-title') ?? '任务',
          duration: { minutes },
          create: true,
        }
      },
    })
    return () => draggable.destroy()
  }, [])

  /** 镜像落位：移除临时事件（真实呈现交给排期后的服务端图层），并发起排期 */
  function onEventReceive(info: EventReceiveArg) {
    const taskId = info.draggedEl.getAttribute('data-task-id')
    // 落位时间取 FC 计算好的事件起点（已吸附到时间槽）
    const start = info.event.start ?? undefined
    // 立即移除镜像事件：日历上的真实呈现由 /calendar/layers 的 task-segments 提供
    info.event.remove()
    if (!taskId) return

    const task = (inboxData ?? []).find((t) => t.id === taskId)
    const minutes = durationToMinutes(task?.estimatedDuration ?? null) ?? 60
    const end = start ? new Date(start.getTime() + minutes * 60_000) : undefined

    plan.mutate(
      {
        id: taskId,
        body: {
          plannedStart: (start ?? new Date()).toISOString(),
          ...(end ? { plannedEnd: end.toISOString() } : {}),
          ...(task?.estimatedDuration ? { estimatedDuration: task.estimatedDuration } : {}),
        },
      },
      {
        onSuccess: () => notifySuccess(`已排期：${task?.title ?? '任务'}`),
        onError: (e) => notifyError(e instanceof Error ? e.message : '排期失败'),
      },
    )
  }

  const truncated = (eventsQuery.data?.totalCount ?? 0) > (eventsQuery.data?.items.length ?? 0)

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader
        title="日历"
        subtitle={range.title}
        actions={
          <>
            <Link
              to="/calendar-styles"
              title="日历样式对比：选择一套视觉方案"
              className="inline-flex h-8 items-center gap-1.5 rounded-ctl px-3 text-[13px] font-medium text-text-2 transition-colors hover:bg-surface hover:text-text-1 outline-none"
            >
              <Paintbrush className="size-4" aria-hidden /> 皮肤
            </Link>
            <Button variant="secondary" size="sm" className="lg:hidden" onClick={() => setInboxOpen(true)}>
              <Inbox className="size-4" aria-hidden /> 收件箱
            </Button>
            <Button variant="secondary" size="sm" onClick={() => setTaskEditorOpen(true)}>
              新建任务
            </Button>
            <Button variant="primary" size="sm" onClick={() => setEventEditor({ mode: 'create', initial: { start: new Date(), end: new Date(Date.now() + 3600_000) } })}>
              <CalendarPlus className="size-4" aria-hidden /> 新建日程
            </Button>
          </>
        }
      />

      {/* 工具条：日期导航 + 视图切换 + 图层 chips */}
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="sm" aria-label="前一天" onClick={() => calendarRef.current?.getApi().prev()}>
            <ChevronLeft className="size-4" aria-hidden />
          </Button>
          <Button variant="secondary" size="sm" onClick={() => calendarRef.current?.getApi().today()}>
            今天
          </Button>
          <Button variant="ghost" size="sm" aria-label="后一天" onClick={() => calendarRef.current?.getApi().next()}>
            <ChevronRight className="size-4" aria-hidden />
          </Button>
        </div>
        <CalendarSegmented view={view} onChange={setView} />
        {isTimeGrid && (
          <div className="flex items-center gap-1.5">
            <span className="text-[11px] text-text-4">凌晨</span>
            <Segmented
              size="sm"
              value={dawn}
              onValueChange={(v) => setDawnPersist(v)}
              options={([
                { v: 'smart', label: DAWN_LABEL.smart },
                { v: 'crop', label: DAWN_LABEL.crop },
                { v: 'strip', label: DAWN_LABEL.strip },
              ] as { v: DawnStrategy; label: string }[]).map((o) => ({ value: o.v, label: o.label }))}
            />
            {dawn === 'smart' && (
              <Button variant="ghost" size="sm" onClick={refocusSmart} title={smartMinutes === 8 * 60 ? '滚动到 08:00' : '滚动到当前时刻 / 首个日程'}>
                定位
              </Button>
            )}
          </div>
        )}
        <div className="flex flex-wrap items-center gap-1.5 lg:ml-2">
          <Chip active={layerToggles.events} onClick={() => toggleLayer('events')}>日程</Chip>
          <Chip active={layerToggles['task-segments']} onClick={() => toggleLayer('task-segments')}>任务段</Chip>
          <Chip active={layerToggles.habits} onClick={() => toggleLayer('habits')}>习惯</Chip>
          <Chip active={layerToggles.availability} onClick={() => toggleLayer('availability')}>可用时间</Chip>
          <Chip active={layerToggles['ai-placeholders']} onClick={() => toggleLayer('ai-placeholders')}>智能占位</Chip>
          <Chip active={layerToggles.outlookOnly} onClick={() => toggleLayer('outlookOnly')}>仅 Outlook</Chip>
        </div>
      </div>

      {/* 截断横幅仅网格视图需要（列表视图走无限分页装填，无 100 条上限问题） */}
      {view !== 'list' && truncated && (
        <div className="mb-2 rounded-ctl border border-warn-border bg-warn-soft px-3 py-1.5 text-xs text-warn">
          当前窗口事件超过 100 条，仅显示前 100 条（可缩小时间范围查看更多）。
        </div>
      )}

      {/* 策略 C：小时分布条（有日程的小时点亮，点击跳到该小时） */}
      {isTimeGrid && dawn === 'strip' && (
        <div className="mb-2 flex flex-wrap items-center gap-2 rounded-ctl border border-border bg-surface px-3 py-2">
          <span className="text-[11px] text-text-4">小时分布（点击跳转）</span>
          <div className="flex min-w-0 flex-1 items-end gap-[3px]">
            {strip.map((c, hour) => {
              const minutes = Math.round(c.minutes)
              const has = minutes > 0
              const h = Math.min(18, 4 + Math.min(1, minutes / 60) * 14)
              return (
                <button
                  key={hour}
                  type="button"
                  title={has ? `${String(hour).padStart(2, '0')}:00 · ${minutes} 分钟` : `${String(hour).padStart(2, '0')}:00 · 无日程`}
                  onClick={() => jumpToHour(hour)}
                  className={cn(
                    'flex-1 rounded-t-[3px] outline-none transition-[filter] hover:brightness-110',
                    has ? '' : 'bg-surface-2',
                  )}
                  style={{ height: has ? h : 4, backgroundColor: has ? c.color ?? '#3B82F6' : undefined }}
                />
              )
            })}
          </div>
        </div>
      )}

      {/* 列表视图：自定义无限下滑列表（FC 仍挂载但隐藏——继续充当日期导航/数据窗口引擎） */}
      {view === 'list' && (
        <div className="flex min-h-0 flex-1 gap-4">
          <EventListView
            anchorStart={range.start}
            calendarId={calendarId}
            calendars={calendars}
            layerToggles={layerToggles}
            outlookOnly={layerToggles.outlookOnly}
            onOpenEvent={setDetail}
          />
          <aside className="hidden w-[280px] shrink-0 lg:block">
            <div className="h-full max-h-[calc(100dvh-8rem)] overflow-hidden rounded-card border border-border bg-bg shadow-card">
              <InboxPanel
                onNewTask={() => setTaskEditorOpen(true)}
                onNewEvent={() =>
                  setEventEditor({ mode: 'create', initial: { start: nextHour(), end: nextHourPlus() } })
                }
              />
            </div>
          </aside>
        </div>
      )}

      {/* 网格视图（时间轴/周/月）：列表模式下隐藏不卸载，prev/next 与 datesSet 继续工作 */}
      <div className={cn('flex min-h-0 flex-1 gap-4', view === 'list' && 'hidden')}>
        {/* 日历网格自身滚动：收件箱侧板因此常驻可见（主滚动容器在外壳 main 上） */}
        <div className="skin-gcal min-w-0 flex-1 overflow-y-auto select-none [&_.fc-event-mirror]:pointer-events-none">
          <FullCalendar
            ref={calendarRef}
            plugins={[dayGridPlugin, timeGridPlugin, listPlugin, interactionPlugin]}
            locale={zhCnLocale}
            headerToolbar={false}
            initialView={CAL_VIEW_TYPES[view]}
            datesSet={onDatesSet}
            events={fcEvents}
            selectable={view === 'timeline'}
            selectMirror
            select={onSelect}
            dateClick={onDateClick}
            droppable
            eventReceive={onEventReceive}
            eventClick={onEventClick}
            eventDidMount={onEventDidMount}
            eventWillUnmount={onEventWillUnmount}
            height="auto"
            stickyHeaderDates
            allDaySlot
            nowIndicator
            slotMinTime={cropWindow ? scrollTimeOf(cropWindow.min) : '00:00:00'}
            slotMaxTime={cropWindow ? scrollTimeOf(cropWindow.max) : '24:00:00'}
            firstDay={1}
            eventTimeFormat={{ hour: '2-digit', minute: '2-digit', hour12: false }}
            eventContent={(arg) => {
              const ev = arg.event.extendedProps.event as EventResponse | undefined
              const cancelled = arg.event.extendedProps.cancelled as boolean | undefined
              const repeated = arg.event.extendedProps.repeated as boolean | undefined
              // 月视图：GCal 圆点行（彩点 + 时间 + 标题）
              if (arg.view.type.startsWith('dayGrid')) {
                return (
                  <div className="flex min-w-0 items-center gap-1.5 px-0.5 py-0.5">
                    {!cancelled && (
                      <span
                        className="size-1.5 shrink-0 rounded-full"
                        style={{ backgroundColor: arg.event.backgroundColor }}
                        aria-hidden
                      />
                    )}
                    {arg.timeText && (
                      <span className="tnum shrink-0 text-[11px] text-text-3">{arg.timeText}</span>
                    )}
                    <span
                      className={cn(
                        'truncate text-[12px] leading-4 font-medium',
                        cancelled ? 'text-text-4 line-through' : 'text-text-1',
                      )}
                    >
                      {repeated && <span className="mr-0.5 text-text-3">↻</span>}
                      {arg.event.title}
                    </span>
                  </div>
                )
              }
              // 日视图：谷歌竖条（白底左宽条由皮肤 CSS 绘制，这里只排文字）
              // 顶部对齐；行数由 data-density（eventDidMount 实测高度写入）经 CSS 逐级放开
              const lines = summarizeLines(ev?.description, { exclude: [ev?.location, ev?.title], maxLines: 5 })
              const metaLine = [ev?.location, showAsLabel(ev?.showAs)].filter(Boolean).join(' · ')
              const whenText = arg.timeText || (ev ? formatTime(ev.dtStart) : '')
              return (
                <div className="flex h-full min-w-0 flex-col overflow-hidden rounded-[inherit] py-1 pr-1.5 pl-1">
                  {cancelled ? (
                    <span className="truncate text-[11px] leading-4 font-medium text-text-4 line-through">
                      {arg.event.title}
                    </span>
                  ) : (
                    <>
                      <span className="truncate text-[11px] leading-4 font-semibold">
                        {repeated && <span className="mr-0.5 font-normal text-text-3">↻</span>}
                        {arg.event.title}
                      </span>
                      {/* 次行：时间始终显示（极矮卡片也不丢时间） */}
                      {whenText && (
                        <span className="pim-ev-when tnum truncate text-[10px] leading-3 text-text-3">
                          {whenText}
                          {metaLine && <span className="font-sans"> · {metaLine}</span>}
                        </span>
                      )}
                      {/* 第三行起：描述逐行展示，行数越多放开越多 */}
                      {lines[0] && (
                        <span className="pim-ev-row3 truncate text-[10px] leading-3 text-text-4">{lines[0]}</span>
                      )}
                      {lines[1] && (
                        <span className="pim-ev-row4 truncate text-[10px] leading-3 text-text-4">{lines[1]}</span>
                      )}
                      {lines[2] && (
                        <span className="pim-ev-row5 truncate text-[10px] leading-3 text-text-4">{lines[2]}</span>
                      )}
                      {lines[3] && (
                        <span className="pim-ev-row6 truncate text-[10px] leading-3 text-text-4">{lines[3]}</span>
                      )}
                      {lines[4] && (
                        <span className="pim-ev-row7 truncate text-[10px] leading-3 text-text-4">{lines[4]}</span>
                      )}
                    </>
                  )}
                </div>
              )
            }}
          />
        </div>

        {/* 收件箱侧板（≥1024 常驻；日历网格独立滚动，此板不动，拖拽源不跑出视口） */}
        <aside className="hidden w-[280px] shrink-0 lg:block">
          <div className="h-full max-h-[calc(100dvh-8rem)] overflow-hidden rounded-card border border-border bg-bg shadow-card">
            <InboxPanel
            onNewTask={() => setTaskEditorOpen(true)}
            onNewEvent={() =>
              setEventEditor({ mode: 'create', initial: { start: nextHour(), end: nextHourPlus() } })
            }
          />
          </div>
        </aside>
      </div>

      {/* 窄屏收件箱抽屉 */}
      <Drawer open={inboxOpen} onOpenChange={setInboxOpen}>
        <DrawerContent side="right" className="w-[320px]">
            <InboxPanel
            onNewTask={() => {
              setInboxOpen(false)
              setTaskEditorOpen(true)
            }}
            onNewEvent={() => {
              setInboxOpen(false)
              setEventEditor({ mode: 'create', initial: { start: nextHour(), end: nextHourPlus() } })
            }}
          />
        </DrawerContent>
      </Drawer>

      <EventEditorDialog
        open={eventEditor != null}
        onOpenChange={(o) => !o && setEventEditor(null)}
        calendars={calendars}
        initial={eventEditor?.mode === 'create' ? eventEditor.initial : null}
        event={eventEditor?.mode === 'edit' ? eventEditor.event : null}
      />
      <TaskEditorDialog open={taskEditorOpen} onOpenChange={setTaskEditorOpen} taskBooks={taskBooks} />

      {/* 详情（只读）→ 点「编辑」才进编辑弹窗 */}
      <EventDetailDialog
        event={detail}
        calendarName={calendars.find((c) => c.id === detail?.calendarId)?.name}
        onClose={() => setDetail(null)}
        onEdit={(ev) => {
          setDetail(null)
          setEventEditor({ mode: 'edit', event: ev })
        }}
      />
    </div>
  )
}

function nextHour(): Date {
  const d = new Date()
  d.setHours(d.getHours() + 1, 0, 0, 0)
  return d
}
function nextHourPlus(): Date {
  const d = nextHour()
  d.setHours(d.getHours() + 1)
  return d
}

function CalendarSegmented({ view, onChange }: { view: CalView; onChange: (v: CalView) => void }) {
  return (
    <Segmented
      value={view}
      onValueChange={(v) => onChange(v as CalView)}
      options={(['timeline', 'week', 'month', 'list'] as const).map((v) => ({
        value: v,
        label: CAL_VIEW_LABEL[v],
      }))}
    />
  )
}
