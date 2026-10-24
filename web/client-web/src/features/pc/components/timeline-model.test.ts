import { describe, expect, it } from 'vitest'
import {
  buildTimelineModel,
  formatWallClock,
  isFocusCategory,
  parseUtcMs,
  toWallClockMinutes,
  WALL_CLOCK_OFFSET_MINUTES,
  type TimelineRecordInput,
} from './timeline-model'

/* 后端真实的 7 位小数时间戳形态 */
const R = (over: Partial<TimelineRecordInput> = {}): TimelineRecordInput => ({
  start: '2026-09-27T03:00:00.0400000+00:00', // 墙钟 11:00
  end: '2026-09-27T03:30:00.0400000+00:00', // 墙钟 11:30
  durationMinutes: 30,
  appName: 'code',
  windowTitle: null,
  categoryName: '编程/折腾',
  categoryColor: '#6B5EE4',
  ...over,
})

describe('parseUtcMs（后端 7 位小数 / Z / 偏移 / 无时区）', () => {
  it('解析 7 位小数 + 偏移', () => {
    expect(parseUtcMs('2026-09-27T03:03:37.0400000+00:00')).toBe(Date.parse('2026-09-27T03:03:37.040Z'))
  })

  it('解析 Z 结尾与无小数', () => {
    expect(parseUtcMs('2026-09-27T03:03:37Z')).toBe(Date.parse('2026-09-27T03:03:37Z'))
  })

  it('无时区按 UTC 解释（后端契约）', () => {
    expect(parseUtcMs('2026-09-27T03:00:00')).toBe(Date.parse('2026-09-27T03:00:00Z'))
  })

  it('非法/空值返回 null', () => {
    expect(parseUtcMs(null)).toBeNull()
    expect(parseUtcMs('')).toBeNull()
    expect(parseUtcMs('not-a-date')).toBeNull()
  })
})

describe('墙钟换算固定 +08:00（与运行时区无关）', () => {
  it('UTC 03:00 → 墙钟 11:00（180 分钟）', () => {
    const ms = Date.parse('2026-09-27T03:00:00Z')
    expect(toWallClockMinutes(ms)).toBe(11 * 60)
  })

  it('UTC 前一日 20:00 → 墙钟次日 04:00（240 分钟）', () => {
    const ms = Date.parse('2026-09-26T20:00:00Z')
    expect(toWallClockMinutes(ms)).toBe(4 * 60)
  })

  it('偏移常量恒为 8 小时（无夏令时）', () => {
    expect(WALL_CLOCK_OFFSET_MINUTES).toBe(480)
  })

  it('formatWallClock 对跨日分钟取模', () => {
    expect(formatWallClock(11 * 60)).toBe('11:00')
    expect(formatWallClock(24 * 60 + 5)).toBe('00:05')
    expect(formatWallClock(-5)).toBe('23:55')
    expect(formatWallClock(23 * 60 + 59)).toBe('23:59')
  })
})

describe('专注类判定', () => {
  it('命中关键词', () => {
    expect(isFocusCategory('编程/折腾')).toBe(true)
    expect(isFocusCategory('文档')).toBe(true)
    expect(isFocusCategory('学习')).toBe(true)
    expect(isFocusCategory('工作')).toBe(true)
  })
  it('非专注类', () => {
    expect(isFocusCategory('单机游戏')).toBe(false)
    expect(isFocusCategory('浏览')).toBe(false)
    expect(isFocusCategory('其他')).toBe(false)
  })
})

describe('buildTimelineModel 计算规则', () => {
  it('无效记录被忽略（缺字段 / 不可解析 / end <= start）', () => {
    const m = buildTimelineModel([
      R(),
      { start: null, end: '2026-09-27T03:00:00Z' },
      { start: '2026-09-27T03:00:00Z', end: null },
      { start: 'bad', end: '2026-09-27T04:00:00Z' },
      { start: '2026-09-27T04:00:00Z', end: '2026-09-27T04:00:00Z' }, // end == start
      { start: '2026-09-27T05:00:00Z', end: '2026-09-27T04:00:00Z' }, // end < start
    ])
    expect(m.recordCount).toBe(1)
  })

  it('durationMinutes 缺失或为 0 时回退 (end - start)', () => {
    const m = buildTimelineModel([
      R({ durationMinutes: 0, start: '2026-09-27T03:00:00Z', end: '2026-09-27T03:20:00Z' }),
      R({ durationMinutes: null as unknown as number, start: '2026-09-27T04:00:00Z', end: '2026-09-27T04:10:00Z' }),
    ])
    expect(m.totalMinutes).toBe(30) // 20 + 10
  })

  it('分类合计按时长降序，缺失分类算「其他」，小时保留原始精度', () => {
    const m = buildTimelineModel([
      R({ categoryName: '浏览', durationMinutes: 30 }),
      R({ categoryName: '编程/折腾', durationMinutes: 90 }),
      R({ categoryName: null as unknown as string, durationMinutes: 15 }),
      R({ categoryName: '   ', durationMinutes: 5 }),
    ])
    expect(m.categories.map((c) => c.categoryName)).toEqual(['编程/折腾', '浏览', '其他'])
    expect(m.categories[0].minutes).toBe(90)
    expect(m.categories[0].hours).toBe(1.5)
    expect(m.categories[2].minutes).toBe(20) // null 与空白合并为「其他」
  })

  it('图例各分类时长之和等于总时长', () => {
    const m = buildTimelineModel([
      R({ categoryName: '浏览', durationMinutes: 33.3 }),
      R({ categoryName: '文档', durationMinutes: 12.7 }),
      R({ categoryName: '其他', durationMinutes: 4.03 }),
    ])
    const sum = m.categories.reduce((a, c) => a + c.minutes, 0)
    expect(sum).toBeCloseTo(m.totalMinutes, 10)
  })

  it('专注率 = 专注类 ÷ 总时长 ×100 四舍五入；总时长为 0 时为 0%', () => {
    const m = buildTimelineModel([
      R({ categoryName: '编程/折腾', durationMinutes: 60 }),
      R({ categoryName: '单机游戏', durationMinutes: 40 }),
    ])
    expect(m.focusRate).toBe(60) // 60/100
    expect(buildTimelineModel([]).focusRate).toBe(0)
    expect(buildTimelineModel(null).focusRate).toBe(0)
  })

  it('N 条为原始记录数，非拆分后的横条数', () => {
    // 19:42–20:12（墙钟）= UTC 11:42–12:12，跨 19、20 两行
    const m = buildTimelineModel([
      R({ start: '2026-09-27T11:42:00Z', end: '2026-09-27T12:12:00Z', durationMinutes: 30 }),
    ])
    expect(m.recordCount).toBe(1)
    expect(m.activeHours).toEqual([19, 20])
    expect(m.barsByHour.get(19)!.length).toBe(1)
    expect(m.barsByHour.get(20)!.length).toBe(1)
  })
})

describe('跨小时切分（验收 1）', () => {
  it('19:42–20:12 → 19 行 [42,60] 与 20 行 [0,12]，位置正确且首尾相接', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T11:42:00Z', end: '2026-09-27T12:12:00Z', durationMinutes: 30 }),
    ])
    const a = m.barsByHour.get(19)![0]
    const b = m.barsByHour.get(20)![0]
    expect(a.offsetMinutes).toBe(42)
    expect(a.spanMinutes).toBe(18)
    expect(b.offsetMinutes).toBe(0)
    expect(b.spanMinutes).toBe(12)
    // 19 行末段接向 20 行；20 行首段接自 19 行
    expect(a.continuesToNextHour).toBe(true)
    expect(b.continuesFromPrevHour).toBe(true)
    expect(a.continuesFromPrevHour).toBe(false)
    expect(b.continuesToNextHour).toBe(false)
  })

  it('切分守恒：段长之和等于记录时长', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T11:30:00Z', end: '2026-09-27T14:20:00Z', durationMinutes: 170 }), // 19:30–22:20
    ])
    let sum = 0
    for (const h of m.activeHours) for (const b of m.barsByHour.get(h)!) sum += b.spanMinutes
    expect(sum).toBeCloseTo(170, 6)
    expect(m.activeHours).toEqual([19, 20, 21, 22])
  })

  it('每段都落在所属小时内（offset + span ≤ 60）', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T11:00:00Z', end: '2026-09-27T16:00:00Z', durationMinutes: 300 }),
    ])
    for (const h of m.activeHours) {
      for (const b of m.barsByHour.get(h)!) {
        expect(b.hour).toBe(h)
        expect(b.offsetMinutes).toBeGreaterThanOrEqual(0)
        expect(b.offsetMinutes + b.spanMinutes).toBeLessThanOrEqual(60.0001)
      }
    }
  })

  it('同小时的多个记录各自定位', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T03:05:00Z', end: '2026-09-27T03:15:00Z', durationMinutes: 10 }), // 11:05–11:15
      R({ start: '2026-09-27T03:40:00Z', end: '2026-09-27T03:55:00Z', durationMinutes: 15 }), // 11:40–11:55
    ])
    const bars = m.barsByHour.get(11)!
    expect(bars.map((b) => b.offsetMinutes)).toEqual([5, 40])
  })
})

describe('升序清单（详情弹窗）', () => {
  it('记录按开始时间升序，并带 HH:mm 文案', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T10:00:00Z', end: '2026-09-27T10:10:00Z', durationMinutes: 10 }), // 18:00
      R({ start: '2026-09-27T03:00:00Z', end: '2026-09-27T03:10:00Z', durationMinutes: 10 }), // 11:00
    ])
    expect(m.records.map((r) => r.startLabel)).toEqual(['11:00', '18:00'])
  })

  it('windowTitle 为 null 时不产生 undefined 文案', () => {
    const m = buildTimelineModel([R({ windowTitle: null })])
    expect(m.records[0].windowTitle).toBeNull()
  })
})

describe('小时行集合（验收 2）', () => {
  it('activeHours 仅有数据的小时且升序', () => {
    const m = buildTimelineModel([
      R({ start: '2026-09-27T03:00:00Z', end: '2026-09-27T03:10:00Z' }), // 11
      R({ start: '2026-09-27T14:00:00Z', end: '2026-09-27T14:10:00Z' }), // 22
    ])
    expect(m.activeHours).toEqual([11, 22])
    expect(m.activeHours.length).toBe(2) // 关闭开关时的行数
  })

  it('空输入无小时行（空态）', () => {
    expect(buildTimelineModel([]).activeHours).toEqual([])
    expect(buildTimelineModel(undefined).activeHours).toEqual([])
  })
})

describe('规模（验收 6：数百条不退化）', () => {
  it('500 条记录构建结果正确且线性', () => {
    const many = Array.from({ length: 500 }, (_, i) => {
      const minuteOffset = i * 2 // 每条 2 分钟，跨越 ~16.6 小时
      const startMs = Date.parse('2026-09-27T00:00:00Z') + minuteOffset * 60_000
      const endMs = startMs + 2 * 60_000
      return R({
        start: new Date(startMs).toISOString(),
        end: new Date(endMs).toISOString(),
        durationMinutes: 2,
        categoryName: i % 3 === 0 ? '编程/折腾' : '浏览',
      })
    })
    const t0 = Date.now()
    const m = buildTimelineModel(many)
    const elapsed = Date.now() - t0
    expect(m.recordCount).toBe(500)
    expect(m.totalMinutes).toBe(1000)
    const legendSum = m.categories.reduce((a, c) => a + c.minutes, 0)
    expect(legendSum).toBe(m.totalMinutes)
    expect(elapsed).toBeLessThan(500)
  })
})
