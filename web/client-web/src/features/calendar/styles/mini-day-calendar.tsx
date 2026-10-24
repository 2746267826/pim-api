import { useMemo } from 'react'
import { cn } from '@/lib/utils'

/*
 * 高保真日视觉提案（自绘，预览即所得）。
 * 十种大厂成熟风格 + 历史提案；重叠事件按簇均分列宽（绝不遮盖，有单测）。
 */

export type CalendarVariant =
  | 'fade' // 晕 · 左浓右淡（Google Calendar 渐变确认风）
  | 'gcal-bar' // 谷歌竖条（Google Calendar 日视图）
  | 'gcal-dot' // 谷歌圆点（Google Calendar 月视图语言）
  | 'notion' // Notion Calendar 色条
  | 'outlook' // Microsoft 365 新版浅块
  | 'apple' // Apple Calendar 浅渐变
  | 'linear' // Linear Schedule 圆点行
  | 'ticktick' // 滴答清单胶囊
  | 'feishu' // 飞书日历浅块
  | 'stripe' // Stripe Dashboard 顶条白块
  | 'glass' // 玻璃（当前正式方案）
  | 'amber'
  | 'ink'
  | 'frost'
  | 'outline'

export interface MiniEvent {
  id: string
  title: string
  start: string
  end: string
  color: string
}

const START_HOUR = 6
const END_HOUR = 22
export const PX_PER_HOUR = 40

export const GRID_GUTTER_PX = 52
export const GRID_EDGE_PX = 6

interface Column {
  event: MiniEvent
  column: number
  columns: number
  top: number
  height: number
}

/** 重叠事件 → 簇内分列（贪心；导出供单测验证分列正确性） */
export function layoutColumns(events: MiniEvent[]): Column[] {
  const parsed = events
    .map((e) => ({ ...e, s: new Date(e.start), en: new Date(e.end) }))
    .filter((e) => e.en.getTime() > e.s.getTime())
    .sort((a, b) => a.s.getTime() - b.s.getTime() || b.en.getTime() - a.en.getTime())

  const clusters: { items: typeof parsed; end: number }[] = []
  for (const e of parsed) {
    const last = clusters[clusters.length - 1]
    if (last && e.s.getTime() < last.end) {
      last.items.push(e)
      last.end = Math.max(last.end, e.en.getTime())
    } else {
      clusters.push({ items: [e], end: e.en.getTime() })
    }
  }

  const out: Column[] = []
  for (const cluster of clusters) {
    const colEnds: number[] = []
    const assigned = cluster.items.map((e) => {
      // 严格大于：相同起止的事件也分列，避免完全重叠
      let col = colEnds.findIndex((t) => e.s.getTime() > t)
      if (col === -1) {
        col = colEnds.length
        colEnds.push(0)
      }
      colEnds[col] = e.en.getTime()
      return { e, col }
    })
    for (const { e, col } of assigned) {
      const top = ((e.s.getHours() + e.s.getMinutes() / 60) - START_HOUR) * PX_PER_HOUR
      const rawH = ((e.en.getTime() - e.s.getTime()) / 3600_000) * PX_PER_HOUR
      out.push({ event: e, column: col, columns: colEnds.length, top: Math.max(0, top), height: Math.max(20, rawH - 3) })
    }
  }
  return out
}

/** 列定位：簇内均分宽度（calc+百分比，列间自然留缝） */
function columnInset(column: number, columns: number): { left: string; right: string } {
  const span = `(100% - ${GRID_GUTTER_PX + GRID_EDGE_PX}px)`
  return {
    left: `calc(${GRID_GUTTER_PX}px + ${span} * ${column / columns})`,
    right: `calc(${GRID_EDGE_PX}px + ${span} * ${(columns - 1 - column) / columns})`,
  }
}

function timeLabel(d: Date): string {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

/* ── 变体容器样式 ─────────────────────────────────────────── */

const VARIANT_EVENT: Record<CalendarVariant, (e: MiniEvent) => React.CSSProperties> = {
  fade: (e) => ({
    background: `linear-gradient(90deg, ${hexAlpha(e.color, 0.32)} 0%, ${hexAlpha(e.color, 0.07)} 45%, transparent 90%)`,
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 8,
    color: 'var(--color-text-1)',
  }),
  'gcal-bar': (e) => ({
    background: 'var(--color-bg)',
    borderLeft: `9px solid ${e.color}`,
    borderRadius: 3,
    boxShadow: '0 1px 2px rgba(15,23,42,.10)',
    color: 'var(--color-text-1)',
  }),
  'gcal-dot': () => ({
    background: 'transparent',
    borderRadius: 4,
    color: 'var(--color-text-1)',
  }),
  notion: (e) => ({
    background: hexAlpha(e.color, 0.08),
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 4,
    color: 'var(--color-text-1)',
  }),
  outlook: (e) => ({
    background: hexAlpha(e.color, 0.18),
    borderLeft: `4px solid ${e.color}`,
    borderRadius: 2,
    color: 'var(--color-text-1)',
  }),
  apple: (e) => ({
    background: `linear-gradient(180deg, ${hexAlpha(e.color, 0.16)}, ${hexAlpha(e.color, 0.30)})`,
    borderRadius: 6,
    color: 'var(--color-text-1)',
  }),
  linear: () => ({
    background: 'transparent',
    borderRadius: 4,
    color: 'var(--color-text-1)',
  }),
  ticktick: () => ({
    background: 'var(--color-bg)',
    border: '1px solid var(--color-border)',
    borderRadius: 999,
    color: 'var(--color-text-1)',
  }),
  feishu: (e) => ({
    background: hexAlpha(e.color, 0.12),
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 6,
    color: 'var(--color-text-1)',
  }),
  stripe: (e) => ({
    background: 'var(--color-bg)',
    borderTop: `2px solid ${e.color}`,
    border: '1px solid var(--color-border)',
    borderTopWidth: 2,
    borderTopColor: e.color,
    borderRadius: 6,
    color: 'var(--color-text-1)',
  }),
  glass: (e) => ({
    background: `linear-gradient(180deg, ${lighten(e.color, 0.18)}, ${e.color})`,
    borderRadius: 8,
    boxShadow: `inset 0 1px 0 rgba(255,255,255,.35), 0 2px 6px ${hexAlpha(e.color, 0.35)}`,
    color: '#ffffff',
  }),
  amber: (e) => ({
    background: `linear-gradient(160deg, ${hexAlpha(e.color, 0.22)}, ${hexAlpha(e.color, 0.13)})`,
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 10,
    boxShadow: `0 2px 8px ${hexAlpha(e.color, 0.18)}`,
    color: 'var(--color-text-1)',
  }),
  ink: (e) => ({
    background: 'var(--color-bg)',
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 6,
    border: '1px solid var(--color-border)',
    borderLeftWidth: 3,
    color: 'var(--color-text-1)',
  }),
  frost: (e) => ({
    background: hexAlpha(e.color, 0.08),
    borderLeft: `3px solid ${e.color}`,
    borderRadius: 8,
    color: 'var(--color-text-1)',
  }),
  outline: (e) => ({
    background: 'var(--color-bg)',
    border: `1.5px solid ${e.color}`,
    borderRadius: 8,
    color: 'var(--color-text-1)',
  }),
}

/** 事件内容的结构差异：标准（时间+标题）/ 圆点行 / 彩色时间 */
type ContentKind = 'std' | 'dot' | 'colortime'

const VARIANT_CONTENT: Record<CalendarVariant, ContentKind> = {
  fade: 'std',
  'gcal-bar': 'std',
  'gcal-dot': 'dot',
  notion: 'std',
  outlook: 'std',
  apple: 'std',
  linear: 'dot',
  ticktick: 'dot',
  feishu: 'std',
  stripe: 'colortime',
  glass: 'std',
  amber: 'std',
  ink: 'std',
  frost: 'std',
  outline: 'colortime',
}

/** 事件时间文字的颜色（变体各自的语言） */
function timeColor(variant: CalendarVariant, eventColor: string): string {
  switch (variant) {
    case 'glass':
      return 'rgba(255,255,255,.8)'
    case 'outline':
    case 'stripe':
      return eventColor
    default:
      return 'var(--color-text-3)'
  }
}

function hexAlpha(hex: string, alpha: number): string {
  const [r, g, b] = hexToRgb(hex)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}
function lighten(hex: string, amount: number): string {
  const [r, g, b] = hexToRgb(hex)
  const mix = (v: number) => Math.round(v + (255 - v) * amount)
  return `rgb(${mix(r)}, ${mix(g)}, ${mix(b)})`
}
function hexToRgb(hex: string): [number, number, number] {
  const v = hex.replace('#', '')
  return [parseInt(v.slice(0, 2), 16), parseInt(v.slice(2, 4), 16), parseInt(v.slice(4, 6), 16)]
}

export function MiniDayCalendar({
  variant,
  events,
  className,
}: {
  variant: CalendarVariant
  events: MiniEvent[]
  className?: string
}) {
  const now = useMemo(() => new Date(), [])
  const columns = useMemo(() => layoutColumns(events), [events])
  const hours = useMemo(
    () => Array.from({ length: END_HOUR - START_HOUR }, (_, i) => START_HOUR + i),
    [],
  )
  const contentKind = VARIANT_CONTENT[variant]

  const nowVisible = now.getHours() >= START_HOUR && now.getHours() < END_HOUR
  const nowTop = (now.getHours() + now.getMinutes() / 60 - START_HOUR) * PX_PER_HOUR

  return (
    <div
      className={cn(
        'relative select-none overflow-hidden rounded-card border border-border bg-bg',
        className,
      )}
    >
      {/* 头部日期 */}
      <div className="flex items-baseline gap-2 border-b border-divider bg-surface/60 px-4 py-2.5">
        <span className="tnum text-[18px] leading-6 font-semibold text-text-1">26</span>
        <span className="text-xs text-text-3">星期六</span>
      </div>

      {/* 时间网格 */}
      <div className="relative" style={{ height: (END_HOUR - START_HOUR) * PX_PER_HOUR }}>
        {hours.map((h) => (
          <div key={h} className="absolute inset-x-0" style={{ top: (h - START_HOUR) * PX_PER_HOUR }}>
            <div className={cn('border-t', variant === 'ink' ? 'border-divider' : 'border-divider/70 border-dashed')} />
            <span className="tnum absolute -top-2 left-1 text-[10px] text-text-4">
              {String(h).padStart(2, '0')}:00
            </span>
          </div>
        ))}

        {/* 事件块（簇内均分列宽，绝不重叠遮盖） */}
        {columns.map(({ event: e, column, columns: cols, top, height }) => {
          const { left, right } = columnInset(column, cols)
          const startD = new Date(e.start)
          const short = height < 32
          return (
            <div
              key={e.id}
              className="absolute flex flex-col justify-center overflow-hidden px-2 py-0.5"
              style={{ top, height, left, right, ...VARIANT_EVENT[variant](e) }}
            >
              {contentKind === 'dot' ? (
                <div className="flex min-w-0 items-center gap-1.5">
                  <span className="size-1.5 shrink-0 rounded-full" style={{ backgroundColor: e.color }} aria-hidden />
                  <span className="truncate text-[12px] leading-4 font-medium">{e.title}</span>
                  <span className="tnum ml-auto shrink-0 text-[10px] text-text-4">{timeLabel(startD)}</span>
                </div>
              ) : contentKind === 'colortime' ? (
                <div className="min-w-0">
                  <div className="flex items-baseline gap-1.5">
                    <span className="tnum text-[10px] font-semibold" style={{ color: e.color }}>
                      {timeLabel(startD)}
                    </span>
                    <span className={cn('truncate font-semibold', short ? 'text-[11px] leading-4' : 'text-[12px] leading-4')}>
                      {e.title}
                    </span>
                  </div>
                  {!short && height > 48 && (
                    <div className="truncate text-[10px] leading-3 text-text-4">{e.end.slice(11, 16)}</div>
                  )}
                </div>
              ) : (
                <div className="min-w-0">
                  {!short && (
                    <div className="tnum text-[10px] leading-3" style={{ color: timeColor(variant, e.color) }}>
                      {timeLabel(startD)}
                    </div>
                  )}
                  <div className={cn('truncate font-semibold', short ? 'text-[11px] leading-[18px]' : 'text-[12px] leading-4')}>
                    {e.title}
                  </div>
                </div>
              )}
            </div>
          )
        })}

        {/* 现在时刻线 */}
        {nowVisible && (
          <div className="pointer-events-none absolute inset-x-0 z-10" style={{ top: nowTop }}>
            <div className="relative border-t-2 border-crit">
              <span className="absolute -top-[5px] -left-1 size-2.5 rounded-full border-2 border-crit bg-bg" />
              <span className="tnum absolute -top-2.5 left-11 rounded bg-crit px-1 text-[10px] leading-4 text-white">
                {timeLabel(now)}
              </span>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
