import { cn } from '@/lib/utils'

/*
 * 鼠标外形按键热力图（规格"鼠标矩阵"）：以鼠标轮廓呈现左/中/右/侧键，
 * 各键按点击数归一取热力色，直观对应真实鼠标握持位置。
 */

const HEAT = ['#F1F5F9', '#DBEAFE', '#93C5FD', '#3B82F6', '#1D4ED8']

function heatColor(v: number, max: number): string {
  if (max <= 0 || v <= 0) return HEAT[0]
  return HEAT[Math.min(4, Math.ceil((v / max) * 4))]
}

export interface MouseHeatmapProps {
  left: number
  middle: number
  right: number
  sideBack: number
  sideForward: number
  /** 滚轮距离（原值，用于说明文字） */
  scrollDistance?: number
  className?: string
}

/** 鼠标俯视轮廓：左/右主键为上下两半的宽区，中键（滚轮）夹在中间，侧键在左缘 */
export function MouseHeatmap({ left, middle, right, sideBack, sideForward, scrollDistance, className }: MouseHeatmapProps) {
  const max = Math.max(1, left, middle, right, sideBack, sideForward)

  return (
    <div className={cn('flex items-start gap-4', className)}>
      {/* 鼠标轮廓（SVG，键位即热力块） */}
      <svg viewBox="0 0 120 200" className="h-[180px] w-[108px] shrink-0" role="img" aria-label="鼠标按键热力图">
        {/* 机身 */}
        <rect x="18" y="4" width="84" height="192" rx="42" fill="#FFFFFF" stroke="var(--color-border)" strokeWidth="1.5" />
        {/* 左键（左上宽区） */}
        <path d="M20 46 C20 22 40 6 60 6 L60 92 L20 92 Z" fill={heatColor(left, max)} stroke="var(--color-border)" strokeWidth="1" />
        {/* 右键（右上宽区） */}
        <path d="M100 46 C100 22 80 6 60 6 L60 92 L100 92 Z" fill={heatColor(right, max)} stroke="var(--color-border)" strokeWidth="1" />
        {/* 中键 / 滚轮长条 */}
        <rect x="55" y="30" width="10" height="34" rx="5" fill={heatColor(middle, max)} stroke="var(--color-border)" strokeWidth="1" />
        {/* 分隔线 */}
        <line x1="60" y1="92" x2="60" y2="6" stroke="var(--color-border)" strokeWidth="1" />
        {/* 侧键（左侧两个小键） */}
        <rect x="6" y="66" width="12" height="18" rx="4" fill={heatColor(sideForward, max)} stroke="var(--color-border)" strokeWidth="1" />
        <rect x="6" y="90" width="12" height="18" rx="4" fill={heatColor(sideBack, max)} stroke="var(--color-border)" strokeWidth="1" />
        {/* 数值标注：仅短数值直接标在键位上（范围聚合的大数会溢出键区并与滚轮标注
            重叠，图例列已给出精确值，大数不在轮廓内重复） */}
        {left < 10_000 && <text x="38" y="62" textAnchor="middle" fontSize="12" fontWeight="600" fill="var(--color-text-1)">{left.toLocaleString()}</text>}
        {right < 10_000 && <text x="82" y="62" textAnchor="middle" fontSize="12" fontWeight="600" fill="var(--color-text-1)">{right.toLocaleString()}</text>}
        {middle > 0 && middle < 10_000 && <text x="60" y="52" textAnchor="middle" fontSize="8" fill="var(--color-text-3)">{middle.toLocaleString()}</text>}
        <text x="60" y="130" textAnchor="middle" fontSize="9" fill="var(--color-text-4)">鼠标</text>
      </svg>

      {/* 图例 + 明细 */}
      <div className="min-w-0 flex-1 space-y-2.5">
        <div className="space-y-1 text-[11px]">
          {[
            { label: '左键', v: left },
            { label: '中键', v: middle },
            { label: '右键', v: right },
            { label: '侧前进', v: sideForward },
            { label: '侧后退', v: sideBack },
          ].map((r) => (
            <div key={r.label} className="flex items-center gap-2">
              <span className="size-2.5 shrink-0 rounded-[3px]" style={{ backgroundColor: heatColor(r.v, max) }} aria-hidden />
              <span className="text-text-3">{r.label}</span>
              <span className="tnum ml-auto font-medium text-text-1">{r.v.toLocaleString()}</span>
            </div>
          ))}
        </div>
        {scrollDistance != null && scrollDistance > 0 && (
          <p className="tnum text-[11px] text-text-4">滚轮距离 {Math.round(scrollDistance).toLocaleString()}</p>
        )}
        <div className="flex items-center gap-1">
          <span className="text-[10px] text-text-4">少</span>
          {HEAT.map((c) => (
            <span key={c} className="size-2.5 rounded-[2px]" style={{ backgroundColor: c }} />
          ))}
          <span className="text-[10px] text-text-4">多</span>
        </div>
      </div>
    </div>
  )
}
