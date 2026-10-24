import { describe, expect, it } from 'vitest'
import { layoutColumns, PX_PER_HOUR } from './mini-day-calendar'
import type { MiniEvent } from './mini-day-calendar'

function ev(id: string, startHour: number, endHour: number, color = '#2563EB'): MiniEvent {
  const d = (h: number) => `2026-09-26T${String(h).padStart(2, '0')}:00:00+08:00`
  return { id, title: id, start: d(startHour), end: d(endHour), color }
}

describe('layoutColumns（重叠事件分列，不得遮盖）', () => {
  it('相同起止的两个事件 → 两列各自独立', () => {
    const cols = layoutColumns([ev('a', 19, 20), ev('b', 19, 20)])
    expect(cols).toHaveLength(2)
    expect(cols.map((c) => c.column).sort()).toEqual([0, 1])
    expect(new Set(cols.map((c) => c.columns))).toEqual(new Set([2]))
  })

  it('三个相同起止 → 三列', () => {
    const cols = layoutColumns([ev('a', 9, 10), ev('b', 9, 10), ev('c', 9, 10)])
    expect(cols.map((c) => c.column)).toEqual([0, 1, 2])
    expect(cols.every((c) => c.columns === 3)).toBe(true)
  })

  it('互不重叠的事件各自独占整行', () => {
    const cols = layoutColumns([ev('a', 9, 10), ev('b', 11, 12)])
    expect(cols.map((c) => c.columns)).toEqual([1, 1])
  })

  it('部分重叠的链式事件分列且不越簇', () => {
    // a 9-11 与 b 10-12 重叠成簇（2 列）；c 12-13 不重叠
    const cols = layoutColumns([ev('a', 9, 11), ev('b', 10, 12), ev('c', 12, 13)])
    const [a, b, c] = cols
    expect(a.columns).toBe(2)
    expect(b.columns).toBe(2)
    expect(b.column).toBe(1)
    expect(c.columns).toBe(1)
  })

  it('位置按小时推进、高度非负', () => {
    const cols = layoutColumns([ev('a', 8, 10)])
    expect(cols[0]!.top).toBe((8 - 6) * PX_PER_HOUR)
    expect(cols[0]!.height).toBeGreaterThan(0)
  })
})
