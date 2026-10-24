import type { CalendarResponse } from '../types';

function isWritable(cal: CalendarResponse): boolean {
  return cal.canEdit !== false;
}

export function resolveCalendarId(
  calendars: CalendarResponse[],
  currentId: string | undefined,
  hiddenCalendarIds: Set<string>,
): string {
  if (currentId && calendars.some(c => c.id === currentId)) {
    return currentId;
  }

  const visible = calendars.filter(c => !hiddenCalendarIds.has(c.id));
  const writable = visible.filter(isWritable);

  const defaultCal = writable.find(c => c.isDefault);
  if (defaultCal) return defaultCal.id;

  const first = writable[0];
  if (first) return first.id;

  return '';
}

export function hasWritableCalendar(
  calendars: CalendarResponse[],
  hiddenCalendarIds: Set<string>,
): boolean {
  return calendars.some(c => !hiddenCalendarIds.has(c.id) && isWritable(c));
}

export function noWritableCalendarMessage(): string {
  return '没有可用的可写日历，请先在设置中添加或启用日历';
}
