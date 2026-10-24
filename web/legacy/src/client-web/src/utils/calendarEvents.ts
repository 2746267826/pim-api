import type { EventInput } from '@fullcalendar/core';
import type { CalendarLayerId, CalendarLayerItem, CalendarResponse, EventResponse, TaskResponse } from '../types';

export type CalendarEventProps =
  | {
      type: 'event';
      raw: EventResponse;
      accentColor: string;
      calendarLabel: string;
    }
  | {
      type: 'task';
      raw: TaskResponse;
      accentColor: string;
      calendarLabel: string;
    }
  | {
      type: 'layer';
      raw: CalendarLayerItem;
      accentColor: string;
    };

export type CalendarEventInput = EventInput & {
  extendedProps: CalendarEventProps;
};

type CalendarLayerToggleId = CalendarLayerId;

function calendarDisplayName(cal: CalendarResponse | undefined): string | undefined {
  if (!cal) return undefined;
  return cal.outlookCalendarBindingId ? `${cal.name} (Outlook)` : cal.name;
}

function eventSourceDisplayName(source: string): string {
  if (source === 'outlook') return 'Outlook';
  if (source === 'outlook-ics') return 'Outlook ICS';
  return '日程';
}

export function buildCalendarEvents(
  events: unknown,
  tasks: unknown,
  layerItems: unknown,
  enabledLayerSet: Set<CalendarLayerToggleId> | unknown,
  calendars: unknown = [],
): CalendarEventInput[] {
  const safeEvents = Array.isArray(events) ? (events as EventResponse[]) : [];
  const safeTasks = Array.isArray(tasks) ? (tasks as TaskResponse[]) : [];
  const safeLayerItems = Array.isArray(layerItems) ? (layerItems as CalendarLayerItem[]) : [];
  const safeCalendars = Array.isArray(calendars) ? (calendars as CalendarResponse[]).filter((c): c is CalendarResponse => !!c && typeof c === 'object' && typeof (c as CalendarResponse).id === 'string') : [];
  const safeEnabledSet: Set<CalendarLayerToggleId> = enabledLayerSet instanceof Set
    ? enabledLayerSet as Set<CalendarLayerToggleId>
    : new Set();
  const calMap = new Map(safeCalendars.map(c => [c.id, c]));

  return [
    ...safeEvents.map(event => {
      if (!event || typeof event !== 'object') return null as unknown as CalendarEventInput;
      const cal = calMap.get((event as EventResponse).calendarId);
      const accentColor = cal?.color ?? '#2563eb';
      const src = (event as EventResponse).source ?? 'manual';
      const calendarLabel = calendarDisplayName(cal) ?? eventSourceDisplayName(src);
      const isCancelled = (event as EventResponse).isCancelled ?? (event as EventResponse).status === 'CANCELLED';

      return {
        id: (event as EventResponse).id,
        title: (event as EventResponse).title,
        start: (event as EventResponse).dtStart,
        end: (event as EventResponse).dtEnd,
        allDay: (event as EventResponse).isAllDay,
        classNames: isCancelled ? ['calendar-event--cancelled'] : undefined,
        extendedProps: {
          type: 'event' as const,
          raw: event as EventResponse,
          accentColor,
          calendarLabel,
        },
      };
    }).filter(Boolean) as CalendarEventInput[],
    ...(safeEnabledSet.has('task-segments') ? safeTasks : []).filter(task => task && (task as TaskResponse).dtStart).map(task => {
      const t = task as TaskResponse;
      const cal = t.calendarId ? calMap.get(t.calendarId) : undefined;
      const accentColor = t.priority === 1 ? '#ef4444'
        : t.priority === 3 ? '#14b8a6'
        : '#f59e0b';
      const calendarLabel = calendarDisplayName(cal) ?? '任务';

      return {
        id: t.id,
        title: t.title,
        start: t.dtStart,
        end: t.plannedEnd || t.due,
        extendedProps: {
          type: 'task' as const,
          raw: t,
          accentColor,
          calendarLabel,
        },
      };
    }),
    ...safeLayerItems
      .filter(item => item && safeEnabledSet.has((item as CalendarLayerItem).layer as CalendarLayerToggleId))
      .filter(item => (item as CalendarLayerItem).layer !== 'events')
      .map(item => ({
        id: `layer-${item.layer}-${item.id}`,
        title: item.title,
        start: item.startsAt,
        end: item.endsAt,
        backgroundColor: 'transparent',
        borderColor: 'transparent',
        classNames: item.layer === 'task-segments'
          ? ['pim-calendar-layer-task-segment']
          : ['pim-calendar-layer'],
        extendedProps: {
          type: 'layer' as const,
          raw: item,
          accentColor: item.color,
        },
      })),
  ];
}
