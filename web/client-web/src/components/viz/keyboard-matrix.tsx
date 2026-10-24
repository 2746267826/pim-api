import { useMemo } from 'react'
import { cn } from '@/lib/utils'

/*
 * 108 键键盘矩阵热力（规格"键盘热力图"）：keyCounts 按 ANSI 布局渲染，
 * 每键颜色按最大计数归一（tokens 热力 ramp）。
 */

// ANSI 108 键布局（行 × 键位，'-' 为占位）
const LAYOUT: string[][] = [
  ['Esc', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12', 'PrtSc', 'Scroll', 'Pause'],
  ['`', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 'Backspace', 'Insert', 'Home', 'PgUp'],
  ['Tab', 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '[', ']', '\\', 'Delete', 'End', 'PgDn'],
  ['CapsLock', 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', ';', "'", 'Enter', '-', '-', '-'],
  ['Shift', 'Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/', 'Shift', '-', 'ArrowUp', '-', '-'],
  ['Ctrl', 'Win', 'Alt', 'Space', 'Alt', 'Win', 'Menu', 'Ctrl', '-', '-', 'ArrowLeft', 'ArrowDown', 'ArrowRight', '-', '-'],
]

const HEAT = ['#F1F5F9', '#DBEAFE', '#93C5FD', '#3B82F6', '#1D4ED8']

function heatColor(v: number, max: number): string {
  if (max <= 0 || v <= 0) return HEAT[0]
  const idx = Math.min(4, Math.ceil((v / max) * 4))
  return HEAT[idx]
}

export function KeyboardMatrix({ keyCounts }: { keyCounts: Record<string, number> }) {
  const max = useMemo(() => Math.max(1, ...Object.values(keyCounts)), [keyCounts])
  const rows = LAYOUT

  const countFor = (key: string): number => {
    const k = keyCounts[key] ?? keyCounts[key.toLowerCase()] ?? keyCounts[key === 'Space' ? ' ' : ''] ?? 0
    return k
  }

  return (
    <div className="space-y-1">
      {rows.map((row, ri) => (
        <div key={ri} className="flex gap-1">
          {row.map((key, ki) => {
            if (key === '-') return <div key={ki} className="flex-1" />
            const v = countFor(key)
            const isWide = key === 'Backspace' || key === 'Tab' || key === 'Enter' || key === 'CapsLock' || key === 'Shift' || key === 'Space' || key === 'Ctrl'
            return (
              <div
                key={ki}
                title={v > 0 ? `${key}: ${v.toLocaleString()} 次` : key}
                className={cn(
                  'flex h-9 min-w-0 flex-1 items-center justify-center rounded-[4px] border border-border text-[9px] font-medium text-text-2 transition-colors',
                  v > 0 && 'border-transparent text-text-1',
                  key === 'Space' && 'flex-[6]',
                  isWide && key !== 'Space' && 'flex-[1.8]',
                )}
                style={{ backgroundColor: heatColor(v, max) }}
              >
                <span className="truncate px-0.5">{key}</span>
              </div>
            )
          })}
        </div>
      ))}
      {/* 图例 */}
      <div className="flex items-center gap-1 pt-1">
        <span className="text-[10px] text-text-4">少</span>
        {HEAT.map((c) => (
          <span key={c} className="size-2.5 rounded-[2px]" style={{ backgroundColor: c }} />
        ))}
        <span className="text-[10px] text-text-4">多</span>
        <span className="tnum ml-2 text-[10px] text-text-4">峰值 {max.toLocaleString()}</span>
      </div>
    </div>
  )
}

/** 鼠标按键小矩阵（左/中/右/侧键） */
export function MouseMatrix({
  left,
  middle,
  right,
  sideBack,
  sideForward,
}: {
  left: number
  middle: number
  right: number
  sideBack: number
  sideForward: number
}) {
  const cells = [
    { key: '左键', v: left },
    { key: '中键', v: middle },
    { key: '右键', v: right },
    { key: '侧后退', v: sideBack },
    { key: '侧前进', v: sideForward },
  ]
  const max = Math.max(1, ...cells.map((c) => c.v))
  return (
    <div className="grid grid-cols-5 gap-1">
      {cells.map((c) => (
        <div
          key={c.key}
          className="rounded-[4px] border border-border px-2 py-1.5 text-center"
          style={{ backgroundColor: heatColor(c.v, max) }}
        >
          <div className="tnum text-xs font-semibold text-text-1">{c.v.toLocaleString()}</div>
          <div className="text-[10px] text-text-3">{c.key}</div>
        </div>
      ))}
    </div>
  )
}
