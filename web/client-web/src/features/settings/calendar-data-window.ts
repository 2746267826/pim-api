export const CALENDAR_DATA_WINDOW_DAYS = 366

export interface CalendarDataWindow {
  start: string
  end: string
}

/** 设置页事件管理的固定合法窗口：以当前时刻为中心，总跨度恰好 366 天。 */
export function buildCalendarDataWindow(now = new Date()): CalendarDataWindow {
  const start = new Date(now.getTime() - Math.floor(CALENDAR_DATA_WINDOW_DAYS / 2) * 86_400_000)
  return {
    start: start.toISOString(),
    end: new Date(start.getTime() + CALENDAR_DATA_WINDOW_DAYS * 86_400_000).toISOString(),
  }
}

export function buildCalendarDataEventsPath(input: {
  page: number
  pageSize?: number
  search?: string
  window: CalendarDataWindow
}): string {
  const query = new URLSearchParams({
    start: input.window.start,
    end: input.window.end,
    page: String(input.page),
    pageSize: String(input.pageSize ?? 50),
  })
  if (input.search) query.set('search', input.search)
  return `/api/v1/calendar/events?${query}`
}
