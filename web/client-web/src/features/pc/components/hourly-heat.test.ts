import { describe, expect, it } from 'vitest'
import { buildHourlyHeatRows } from './hourly-heat'

/* 后端真实形态：7 位小数 + 偏移 */
const heat = (hour: number, over: Record<string, unknown> = {}) => ({
  // hour 为本地小时；本地 h 点 = UTC (h-8) 点
  start: `2026-09-27T${String((hour - 8 + 24) % 24).padStart(2, '0')}:00:00.0000000+00:00`,
  activeMinutes: 0,
  intensityLevel: 0,
  totalEvents: 0,
  ...over,
})

describe('buildHourlyHeatRows（时间块热力数据合并）', () => {
  it('以 heatmap 为主体：本地小时标签与 +08:00 换算一致', () => {
    const rows = buildHourlyHeatRows(
      [heat(11, { activeMinutes: 40, intensityLevel: 4, totalEvents: 120 })],
      [],
    )
    expect(rows).toHaveLength(1)
    expect(rows[0].hour).toBe(11)
    expect(rows[0].label).toBe('11:00')
    expect(rows[0].activeMinutes).toBe(40)
    expect(rows[0].intensity).toBe(4)
    expect(rows[0].totalEvents).toBe(120)
  })

  it('UTC 03:00 桶对应本地 11:00（跨日取模正确）', () => {
    // 本地 04:00 = UTC 前一日 20:00
    const rows = buildHourlyHeatRows([heat(4, { activeMinutes: 30 })], [])
    expect(rows[0].hour).toBe(4)
    expect(rows[0].label).toBe('04:00')
  })

  it('activity-analysis 的待分类与主要应用按小时对齐合并', () => {
    const rows = buildHourlyHeatRows(
      [heat(11, { activeMinutes: 40 })],
      [
        {
          start: '2026-09-27T03:00:00.0000000+00:00',
          pendingClassificationCount: 56,
          apps: [
            { appName: 'zcode', durationSeconds: 1240 },
            { appName: 'explorer', durationSeconds: 840 },
          ],
        },
      ],
    )
    expect(rows[0].pending).toBe(56)
    expect(rows[0].topApp).toBe('zcode') // 时长最长者
  })

  it('heatmap 没有的小时不凭空造行（保持口径一致）', () => {
    const rows = buildHourlyHeatRows(
      [heat(11)],
      [{ start: '2026-09-27T10:00:00.0000000+00:00', pendingClassificationCount: 9 }], // 本地 18:00，heatmap 无
    )
    expect(rows.map((r) => r.hour)).toEqual([11])
  })

  it('同一小时取更大的待分类值（多次上报不回退）', () => {
    const rows = buildHourlyHeatRows(
      [heat(11)],
      [
        { start: '2026-09-27T03:20:00.0000000+00:00', pendingClassificationCount: 30 },
        { start: '2026-09-27T03:40:00.0000000+00:00', pendingClassificationCount: 56 },
      ],
    )
    expect(rows[0].pending).toBe(56)
  })

  it('按本地小时升序', () => {
    const rows = buildHourlyHeatRows([heat(13), heat(11), heat(22)], [])
    expect(rows.map((r) => r.hour)).toEqual([11, 13, 22])
  })

  it('空/坏数据安全：不崩溃、跳过不可解析时间戳', () => {
    expect(buildHourlyHeatRows([], [])).toEqual([])
    expect(buildHourlyHeatRows(null, null)).toEqual([])
    expect(buildHourlyHeatRows([{ start: 'bad' }], [{ start: null }])).toEqual([])
  })

  it('字段为 null 时不产生 NaN/undefined', () => {
    const rows = buildHourlyHeatRows(
      [{ start: '2026-09-27T03:00:00Z', activeMinutes: null, intensityLevel: null, totalEvents: null }],
      [{ start: '2026-09-27T03:00:00Z', pendingClassificationCount: null, apps: [{ appName: null, durationSeconds: null }] }],
    )
    expect(rows[0].activeMinutes).toBe(0)
    expect(rows[0].intensity).toBe(0)
    expect(rows[0].totalEvents).toBe(0)
    expect(rows[0].pending).toBe(0)
    expect(rows[0].topApp).toBeNull()
    expect(JSON.stringify(rows)).not.toContain('NaN')
    expect(JSON.stringify(rows)).not.toContain('undefined')
  })

  it('时间线覆盖拆到对应小时（同小时内的记录）', () => {
    // 本地 13:12 起 12 分钟 → 全部落 13 点档
    const rows = buildHourlyHeatRows(
      [heat(13, { activeMinutes: 60 })],
      [],
      [{ start: '2026-09-27T05:12:00.0000000+00:00', durationMinutes: 12 }],
    )
    expect(rows[0].coveredMinutes).toBe(12)
    expect(rows[0].activeMinutes).toBe(60)
  })

  it('跨小时记录按墙钟拆分（23:50 → 次日 00:10 分属两档）', () => {
    const rows = buildHourlyHeatRows(
      [heat(23, { activeMinutes: 60 }), heat(0, { activeMinutes: 30 })],
      [],
      [{ start: '2026-09-26T15:50:00.0000000+00:00', durationMinutes: 20 }],
    )
    const h23 = rows.find((r) => r.hour === 23)!
    const h0 = rows.find((r) => r.hour === 0)!
    expect(h23.coveredMinutes).toBeCloseTo(10, 1)
    expect(h0.coveredMinutes).toBeCloseTo(10, 1)
  })

  it('无时间线时覆盖为 0；坏记录安全跳过', () => {
    const rows = buildHourlyHeatRows([heat(11, { activeMinutes: 40 })], [], [])
    expect(rows[0].coveredMinutes).toBe(0)
    expect(buildHourlyHeatRows([heat(11)], [], [{ start: 'bad', durationMinutes: 5 }, { durationMinutes: 5 }, { start: '2026-09-27T03:00:00Z', durationMinutes: -1 }])[0].coveredMinutes).toBe(0)
  })
})
