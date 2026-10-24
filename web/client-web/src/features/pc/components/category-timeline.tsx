import { useCallback, useEffect, useMemo, useState } from 'react'
import { Clock, Info } from 'lucide-react'
import { Button, Card, CardTitle, Dialog, DialogBody, DialogContent, DialogHeader, EmptyState, InlineAlert, Skeleton, Switch } from '@/components/ui'
import { FloatingTooltip } from '@/components/viz/floating-tooltip'
import { cn } from '@/lib/utils'
import {
  buildTimelineModel,
  formatWallClock,
  type TimelineBar,
  type TimelineRecordInput,
} from './timeline-model'

/*
 * 「分类时间线」：把一天的使用记录按分类铺成「每小时一行」的时间条图。
 *
 * 独立可复用：数据仅从 props.timeline 传入，组件自身不发任何请求。
 * 时刻一律按 +08:00 墙钟解释（见 timeline-model.ts），与运行时区无关。
 */

export interface CategoryTimelineProps {
  /** GET /pc/summary 的 data.timeline（透传，字段缺失/为 null 均安全） */
  timeline: readonly TimelineRecordInput[] | null | undefined
  /** 加载中（显示骨架而非空态） */
  loading?: boolean
  /**
   * 加载失败信息。注意：失败时 timeline 通常为 undefined，
   * 因此错误优先级高于空态——否则会把「请求失败」误显示成「暂无数据」（静默空白）。
   */
  error?: string | null
  /** 标题（默认「分类时间线」） */
  title?: string
  /** 副标题（默认按所选业务日描述） */
  subtitle?: string
  className?: string
}

/** 每行左侧的刻度（0/15/30/45/60 分钟） */
const TICKS = [0, 15, 30, 45, 60]

/** 分钟 → 展示用小时数（1 位小数，允许 0.0h） */
function hours(minutes: number): string {
  return `${(minutes / 60).toFixed(1)}h`
}

export function CategoryTimeline({
  timeline,
  loading = false,
  error = null,
  title = '分类时间线',
  subtitle,
  className,
}: CategoryTimelineProps) {
  const [showAllHours, setShowAllHours] = useState(false)
  const [detailOpen, setDetailOpen] = useState(false)
  /*
   * 悬浮明细锚点：容器已 overflow-hidden（不出现滚动条），若 tooltip 留在行内会被裁掉，
   * 故提升到组件根、用 position:fixed 依锚点矩形定位，脱离任何祖先裁剪。
   */
  const [anchor, setAnchor] = useState<{ bar: TimelineBar; rect: DOMRect } | null>(null)

  const model = useMemo(() => buildTimelineModel(timeline), [timeline])

  /* 行集合：默认仅有数据的小时；开关打开后固定 0–23 */
  const rows = useMemo(
    () => (showAllHours ? Array.from({ length: 24 }, (_, i) => i) : model.activeHours),
    [showAllHours, model.activeHours],
  )

  /*
   * 悬浮明细的清除时机：仅靠横条的 mouseleave 会漏掉几种路径——
   * 指针从行间空隙移出、滚轮滚动后横条已移开、窗口失焦。
   * 这里统一兜底清理，避免提示框「跟着鼠标赖着不走」。
   */
  const clearAnchor = useCallback(() => setAnchor(null), [])
  useEffect(() => {
    if (!anchor) return
    window.addEventListener('blur', clearAnchor)
    // 捕获阶段监听滚动（滚动容器可能是任意祖先），滚动即清除
    window.addEventListener('scroll', clearAnchor, { capture: true, passive: true })
    return () => {
      window.removeEventListener('blur', clearAnchor)
      window.removeEventListener('scroll', clearAnchor, { capture: true })
    }
  }, [anchor, clearAnchor])

  const empty = !loading && !error && model.recordCount === 0
  return (
    <Card className={cn('p-4', className)}>
      {/* 1) 标题 + 副标题 + 查看详情 */}
      <div className="flex flex-wrap items-center gap-2">
        <CardTitle>{title}</CardTitle>
        {subtitle && <span className="text-xs text-text-4">{subtitle}</span>}
        <Button
          variant="ghost"
          size="sm"
          className="ml-auto"
          disabled={model.recordCount === 0}
          onClick={() => setDetailOpen(true)}
        >
          <Clock className="size-3.5" aria-hidden /> 查看详情
        </Button>
      </div>

      {/* 4/边界：加载失败要可见，不允许静默空白 */}
      {error && (
        <InlineAlert tone="crit" className="mt-3" title="时间线数据加载失败">
          {error}
        </InlineAlert>
      )}

      {loading && (
        <div className="mt-3 space-y-1.5" aria-busy="true" aria-label="时间线加载中">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-6" />
          ))}
        </div>
      )}

      {/* 空态：不画空图 */}
      {empty && (
        <EmptyState
          size="sm"
          className="mt-3"
          icon={Clock}
          title="当日暂无活动时间线"
          description="该业务日没有可用的电脑使用记录。"
        />
      )}

      {!loading && !error && model.recordCount > 0 && (
        <>
          {/* 2) 统计条 */}
          <div className="mt-3 space-y-2">
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5">
              {model.categories.map((c) => (
                <span key={c.categoryName} className="flex items-center gap-1.5 text-[12px]">
                  <span className="size-2.5 shrink-0 rounded-[3px]" style={{ backgroundColor: c.color }} aria-hidden />
                  <span className="text-text-2">{c.categoryName}</span>
                  <span className="tnum text-text-4">{hours(c.minutes)}</span>
                </span>
              ))}
            </div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-divider pt-2">
              <span className="tnum text-[12px] text-text-3">
                专注率 <span className="font-medium text-text-1">{model.focusRate}%</span>
                <span className="mx-1.5 text-text-4">·</span>
                {model.recordCount} 条
                <span className="mx-1.5 text-text-4">·</span>
                合计 {(model.totalMinutes / 60).toFixed(1)}h
              </span>
              <label className="ml-auto flex cursor-pointer items-center gap-2 text-[12px] text-text-3">
                <Switch checked={showAllHours} onCheckedChange={setShowAllHours} />
                显示全部 00:00–23:00（含空行）
              </label>
            </div>
          </div>

          {/* 3) 主体：每小时一行的时间条 */}
          <div className="mt-3 overflow-hidden rounded-ctl border border-border">
            {/* 表头刻度 */}
            <div className="flex items-center border-b border-divider bg-surface text-[10px] text-text-4">
              <span className="w-14 shrink-0 px-2 py-1">小时</span>
              <div className="relative flex-1">
                {TICKS.slice(0, -1).map((t) => (
                  <span key={t} className="absolute top-1 text-[10px]" style={{ left: `${(t / 60) * 100}%` }}>
                    {t}m
                  </span>
                ))}
                <span className="absolute top-1 right-0 text-[10px]">60m</span>
                <span className="block py-1">&nbsp;</span>
              </div>
            </div>

            {/*
              不要滚动条：纵向不再限高（24 行全部展开，由页面整体滚动承担），
              横向用 overflow-hidden 兜住 100% 处的刻度线等亚像素溢出。
              容器级 onMouseLeave 作为兜底：即使指针从行间空隙或图表边缘移出，
              悬浮明细也必须消失（只靠横条自身的 mouseleave 会漏掉这些路径）。
            */}
            <div className="overflow-hidden" onMouseLeave={() => setAnchor(null)}>
              {rows.map((hour) => {
                const bars = model.barsByHour.get(hour) ?? []
                return (
                  <HourRow
                    key={hour}
                    hour={hour}
                    bars={bars}
                    activeKey={anchor?.bar.key ?? null}
                    onHover={(bar, rect) => setAnchor(bar ? { bar, rect } : null)}
                  />
                )
              })}
            </div>
          </div>

          <p className="mt-2 flex items-center gap-1.5 text-[11px] text-text-4">
            <Info className="size-3 shrink-0" aria-hidden />
            横轴为该小时的 0–60 分钟；跨小时的记录会在相邻两行各占一段，位置首尾相接。
          </p>
        </>
      )}

      {/* 4) 查看详情弹窗 */}
      {detailOpen && (
        <TimelineDetailDialog
          model={model}
          onClose={() => setDetailOpen(false)}
        />
      )}

      {/* 悬浮明细：挂在组件根部（不在 overflow-hidden 容器内，避免被裁） */}
      {anchor && <BarTooltip bar={anchor.bar} rect={anchor.rect} />}
    </Card>
  )
}

/* ── 单个小时行 ─────────────────────────────────────────────── */

function HourRow({
  hour,
  bars,
  activeKey,
  onHover,
}: {
  hour: number
  bars: TimelineBar[]
  activeKey: string | null
  onHover: (bar: TimelineBar | null, rect: DOMRect) => void
}) {
  return (
    <div className="flex items-stretch border-b border-divider last:border-b-0">
      <span className="tnum w-14 shrink-0 px-2 py-1.5 text-[11px] text-text-3">
        {String(hour).padStart(2, '0')}:00
      </span>
      <div className="relative min-h-[26px] flex-1 py-0.5">
        {/* 15 分钟刻度线（100% 处的线并入右边界，避免产生 1px 横向溢出） */}
        {TICKS.filter((t) => t < 60).map((t) => (
          <span
            key={t}
            className={cn('absolute top-0 bottom-0 w-px', t === 0 ? 'bg-transparent' : 'bg-divider')}
            style={{ left: `${(t / 60) * 100}%` }}
            aria-hidden
          />
        ))}

        {/* 横条 */}
        {bars.map((b) => {
          const left = (b.offsetMinutes / 60) * 100
          const width = Math.max((b.spanMinutes / 60) * 100, 0.4)
          return (
            <button
              key={b.key}
              type="button"
              className={cn(
                'absolute top-1 bottom-1 min-w-[3px] cursor-default rounded-[3px] transition-[filter,opacity]',
                // 与相邻小时的段首尾相接：贴着边界的一侧不做圆角
                b.offsetMinutes <= 0.001 ? 'rounded-l-none' : '',
                b.offsetMinutes + b.spanMinutes >= 59.999 ? 'rounded-r-none' : '',
                activeKey && activeKey !== b.key ? 'opacity-40' : 'hover:brightness-110',
              )}
              style={{
                left: `${left}%`,
                width: `${width}%`,
                backgroundColor: b.categoryColor,
              }}
              onMouseEnter={(e) => onHover(b, e.currentTarget.getBoundingClientRect())}
              onMouseLeave={() => onHover(null, new DOMRect())}
              onFocus={(e) => onHover(b, e.currentTarget.getBoundingClientRect())}
              onBlur={() => onHover(null, new DOMRect())}
              aria-label={`${b.record.categoryName} ${b.record.appName} ${b.record.startLabel} 至 ${b.record.endLabel} ${Math.round(b.record.durationMinutes)} 分钟`}
            />
          )
        })}
      </div>
    </div>
  )
}

/**
 * 悬停/聚焦明细：分类 · 应用名 / 窗口标题 / 起止 HH:mm / 时长（分钟，四舍五入）。
 * 用统一的 FloatingTooltip（position:fixed 依锚点矩形定位）：容器是
 * overflow-hidden（为了不出现滚动条），行内绝对定位会被裁掉。
 */
function BarTooltip({ bar, rect }: { bar: TimelineBar; rect: DOMRect }) {
  const r = bar.record
  return (
    <FloatingTooltip anchor={rect} maxWidth={320}>
      <div className="flex items-center gap-1.5">
        <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: r.categoryColor }} aria-hidden />
        <span className="font-medium">{r.categoryName}</span>
        <span className="opacity-60">·</span>
        <span className="truncate">{r.appName}</span>
      </div>
      {r.windowTitle && <div className="mt-0.5 truncate opacity-75">{r.windowTitle}</div>}
      <div className="tnum mt-0.5 opacity-90">
        {r.startLabel} – {r.endLabel} · {Math.round(r.durationMinutes)} 分钟
      </div>
    </FloatingTooltip>
  )
}

/* ── 查看详情弹窗 ───────────────────────────────────────────── */

function TimelineDetailDialog({
  model,
  onClose,
}: {
  model: ReturnType<typeof buildTimelineModel>
  onClose: () => void
}) {
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[680px]">
        <DialogHeader
          title="时间线明细"
          description={`${model.recordCount} 条记录 · 总时长 ${(model.totalMinutes / 60).toFixed(1)}h · 专注率 ${model.focusRate}%`}
        />
        {/* 纵向滚动是必要的（最多数百条），但不允许出现横向滚动条 */}
        <DialogBody className="max-h-[64dvh] overflow-x-hidden overflow-y-auto p-0">
          <table className="w-full table-fixed text-[12px]">
            <thead className="sticky top-0 z-10 bg-surface text-left text-[11px] text-text-3">
              <tr>
                <th className="w-[104px] px-3 py-2 font-medium">时刻</th>
                <th className="w-[92px] px-2 py-2 font-medium">分类</th>
                <th className="px-2 py-2 font-medium">应用</th>
                <th className="w-[68px] px-2 py-2 text-right font-medium">时长</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-divider">
              {model.records.map((r) => (
                <tr key={r.key} className="hover:bg-surface">
                  <td className="tnum px-3 py-1.5 whitespace-nowrap text-text-2">
                    {r.startLabel}–{r.endLabel}
                  </td>
                  <td className="px-2 py-1.5">
                    <span className="flex items-center gap-1.5">
                      <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: r.categoryColor }} aria-hidden />
                      <span className="truncate text-text-2">{r.categoryName}</span>
                    </span>
                  </td>
                  <td className="px-2 py-1.5">
                    <span className="block truncate text-text-1">{r.appName}</span>
                    {r.windowTitle && (
                      <span className="block truncate text-[11px] text-text-4">{r.windowTitle}</span>
                    )}
                  </td>
                  <td className="tnum px-2 py-1.5 text-right whitespace-nowrap text-text-3">
                    {Math.round(r.durationMinutes)}m
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {model.records.length === 0 && (
            <EmptyState size="sm" title="当日没有可用记录" />
          )}
        </DialogBody>
      </DialogContent>
    </Dialog>
  )
}

/* 便于外部按墙钟格式自行取用 */
export { formatWallClock }
