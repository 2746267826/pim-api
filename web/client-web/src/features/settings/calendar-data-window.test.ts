import { describe, expect, it } from 'vitest'
import {
  CALENDAR_DATA_WINDOW_DAYS,
  buildCalendarDataEventsPath,
  buildCalendarDataWindow,
} from './calendar-data-window'

describe('日程数据管理窗口', () => {
  it('始终生成不超过 REST 366 天上限的合法窗口', () => {
    const window = buildCalendarDataWindow(new Date('2026-07-01T12:00:00.000Z'))

    expect(window.start).toBe('2025-12-30T12:00:00.000Z')
    expect(window.end).toBe('2026-12-31T12:00:00.000Z')
    expect(Date.parse(window.end) - Date.parse(window.start)).toBe(
      CALENDAR_DATA_WINDOW_DAYS * 86_400_000,
    )
  })

  it('事件列表请求同时携带 start/end、分页与编码后的搜索词', () => {
    const path = buildCalendarDataEventsPath({
      page: 3,
      search: '项目 A&B',
      window: {
        start: '2026-01-01T00:00:00.000Z',
        end: '2027-01-02T00:00:00.000Z',
      },
    })
    const query = new URL(path, 'https://pim.local').searchParams

    expect(query.get('start')).toBe('2026-01-01T00:00:00.000Z')
    expect(query.get('end')).toBe('2027-01-02T00:00:00.000Z')
    expect(query.get('page')).toBe('3')
    expect(query.get('pageSize')).toBe('50')
    expect(query.get('search')).toBe('项目 A&B')
  })
})
