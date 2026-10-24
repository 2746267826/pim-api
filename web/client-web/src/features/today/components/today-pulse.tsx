/*
 * 今日脉搏：业务日（04:00 → 次日 04:00）的三轨时间条——
 * 日程 / 任务段 / 习惯各一轨，一屏看清一天的节奏与空档。
 *
 * 数据复用日历域查询（useEvents / useLayers），不发新接口；
 * 时轴定位走 +08:00 墙钟（与时间线同一口径，不依赖浏览器时区）。
 * 点击任意条 → 跳日历；悬浮 title 给出时间段。
 */
import { useMemo } from 'react'
import { useNavigate } from 'react-router'
import { Activity } from 'lucide-react'
import { useCalendars, useEvents, useLayers } from '@/features/calendar/queries'
import type { LayerItem } from '@/features/calendar/types'
import { Card, CardTitle, Skeleton } from '@/components/ui'
import { FadeIn } from '@/components/motion/primitives'

const CST_OFFSET_MS = 8 * 3600_000
const DAY_MS = 86_400_000
/** 业务日起点：本地 04:00 → 墙钟分钟 240 */
const BD_START_MIN = 4 * 60

interface TrackSeg {
  key: string
  title: string
  /** 业务日轴上的位置（分钟 0–1440，0 = 业务日 04:00） */
  startMin: number
  lengthMin: number
  color: string
}

function toTrackSegs(
  items: { id: string; title: string; startsAt: string; endsAt: string; color: string }[],
): TrackSeg[] {
  const segs: TrackSeg[] = []
  for (const it of items) {
    const s = Date.parse(it.startsAt)
    const e = Date.parse(it.endsAt)
    if (Number.isNaN(s) || Number.isNaN(e) || e <= s) continue
    const wallStart = (((s + CST_OFFSET_MS) % DAY_MS) + DAY_MS) % DAY_MS / 60_000
    const pos = (wallStart - BD_START_MIN + 1440) % 1440
    const lengthMin = Math.min(1440, (e - s) / 60_000)
    if (lengthMin <= 0) continue
    const hhmm = (ms: number) => new Date(ms + CST_OFFSET_MS).toISOString().slice(11, 16)
    segs.push({
      key: it.id,
      title: `${it.title} · ${hhmm(s)}–${hhmm(Math.min(e, s + lengthMin * 60_000))}`,
      startMin: pos,
      lengthMin,
      color: it.color,
    })
  }
  return segs.sort((a, b) => a.startMin - b.startMin)
}

function Track({
  label,
  segs,
  onJump,
}: {
  label: string
  segs: TrackSeg[]
  onJump: () => void
}) {
  return (
    <div className="flex items-center gap-3">
      <span className="w-12 shrink-0 text-right text-[11px] text-text-4">{label}</span>
      <div
        className="relative h-2.5 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-2"
        style={
          segs.length === 0
            ? { backgroundImage: 'repeating-linear-gradient(45deg, transparent 0 6px, rgb(148 163 184 / 0.18) 6px 12px)' }
            : undefined
        }
      >
        {segs.map((seg) => (
          <button
            key={seg.key}
            type="button"
            title={seg.title}
            aria-label={seg.title}
            onClick={onJump}
            className="absolute inset-y-0 rounded-full outline-none transition-[filter] duration-100 hover:brightness-110"
            style={{
              left: `${(seg.startMin / 1440) * 100}%`,
              width: `${Math.max(0.6, (Math.min(seg.lengthMin, 1440 - seg.startMin) / 1440) * 100)}%`,
              backgroundColor: seg.color,
            }}
          />
        ))}
      </div>
    </div>
  )
}

/** 业务日 UTC 窗口（本地 04:00 起算，后端只收 UTC） */
function businessDayWindow(date: string): { start: string; end: string } | null {
  const ms = Date.parse(`${date}T04:00:00+08:00`)
  if (Number.isNaN(ms)) return null
  return { start: new Date(ms).toISOString(), end: new Date(ms + DAY_MS).toISOString() }
}

export function TodayPulse({ date }: { date: string }) {
  const navigate = useNavigate()
  const win = useMemo(() => businessDayWindow(date), [date])
  const enabled = Boolean(win)
  const { data: calendars = [] } = useCalendars('calendar')
  const eventsQ = useEvents(win?.start ?? '', win?.end ?? '', enabled)
  const layersQ = useLayers(
    win?.start ?? '',
    win?.end ?? '',
    'task-segments,habits',
    false,
    enabled,
  )

  const tracks = useMemo(() => {
    if (!win) return []
    const colorOf = (calendarId: string) => calendars.find((c) => c.id === calendarId)?.color ?? '#3B82F6'
    const events = (eventsQ.data?.items ?? []).map((e) => ({
      id: `ev:${e.id}`,
      title: e.title,
      startsAt: e.dtStart,
      endsAt: e.dtEnd,
      color: colorOf(e.calendarId),
    }))
    const layerSegs = (items: LayerItem[], color: string) =>
      items.map((l) => ({ id: `ly:${l.id}`, title: l.title, startsAt: l.startsAt, endsAt: l.endsAt, color }))
    return [
      { label: '日程', segs: toTrackSegs(events) },
      {
        label: '任务段',
        segs: toTrackSegs(layerSegs((layersQ.data?.items ?? []).filter((l) => l.layer === 'task-segments'), '#22C55E')),
      },
      {
        label: '习惯',
        segs: toTrackSegs(layerSegs((layersQ.data?.items ?? []).filter((l) => l.layer === 'habits'), '#A855F7')),
      },
    ]
  }, [win, eventsQ.data, layersQ.data, calendars])

  const loading = enabled && (eventsQ.isLoading || layersQ.isLoading)
  const jump = () => navigate('/calendar')

  return (
    <FadeIn>
      <Card className="p-4">
        <div className="flex items-center gap-2">
          <CardTitle className="flex items-center gap-1.5">
            <Activity className="size-4 text-text-3" aria-hidden />
            今日脉搏
          </CardTitle>
          <span className="tnum text-xs text-text-4">
            {date}（04:00 → 次日 04:00）
          </span>
          <button
            type="button"
            onClick={jump}
            className="ml-auto text-xs text-primary outline-none hover:text-primary-hover"
          >
            去日历查看 →
          </button>
        </div>
        <div className="mt-3 space-y-2">
          {loading ? (
            <>
              <Skeleton className="h-2.5 w-full" />
              <Skeleton className="h-2.5 w-full" />
              <Skeleton className="h-2.5 w-full" />
            </>
          ) : (
            tracks.map((t) => <Track key={t.label} label={t.label} segs={t.segs} onJump={jump} />)
          )}
          {!loading && tracks.every((t) => t.segs.length === 0) && (
            <p className="pt-0.5 text-center text-[11px] text-text-4">
              区间内暂无日程 / 任务段 / 习惯投射
            </p>
          )}
        </div>
        <div className="mt-2 flex items-center justify-between text-[10px] text-text-4">
          <span>04:00</span>
          <span>08:00</span>
          <span>12:00</span>
          <span>16:00</span>
          <span>20:00</span>
          <span>次日 00:00</span>
          <span>04:00</span>
        </div>
      </Card>
    </FadeIn>
  )
}
