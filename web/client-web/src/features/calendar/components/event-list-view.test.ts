import { describe, expect, it } from 'vitest'
import { LIST_HORIZON_DAYS, buildEventListHorizon } from './event-list-view'

describe('日历列表查询窗口', () => {
  it('无限分页窗口最多 366 天，符合 REST 上限', () => {
    const horizon = buildEventListHorizon('2026-01-01T00:00:00.000Z')

    expect(LIST_HORIZON_DAYS).toBe(366)
    expect(horizon.start).toBe('2026-01-01T00:00:00.000Z')
    expect(horizon.end).toBe('2027-01-02T00:00:00.000Z')
    expect(Date.parse(horizon.end) - Date.parse(horizon.start)).toBe(366 * 86_400_000)
  })
})
