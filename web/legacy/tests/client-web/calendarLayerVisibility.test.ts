import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';
import type {
  CalendarLayerItem,
  CalendarResponse,
  EventResponse,
  TaskResponse,
} from '../../src/client-web/src/types';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

let buildCalendarEvents: typeof import('../../src/client-web/src/utils/calendarEvents').buildCalendarEvents;

before(async () => {
  const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  globalThis.window = dom.window as unknown as Window & typeof globalThis;
  globalThis.document = dom.window.document;
  globalThis.Node = dom.window.Node;
  globalThis.DocumentFragment = dom.window.DocumentFragment;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.HTMLDocument = dom.window.HTMLDocument;
  globalThis.DOMParser = dom.window.DOMParser;

  const mod = await import('../../src/client-web/src/utils/calendarEvents');
  buildCalendarEvents = mod.buildCalendarEvents;
});

const event: EventResponse = {
  id: 'event-1',
  calendarId: 'calendar-1',
  uid: 'event-uid-1',
  title: 'Planning review',
  dtStart: '2026-07-08T09:00:00',
  dtEnd: '2026-07-08T09:30:00',
  status: 'Confirmed',
  source: 'manual',
};

const plannedTask: TaskResponse = {
  id: 'task-1',
  title: 'Draft agenda',
  priority: 2,
  dtStart: '2026-07-08T10:00:00',
  plannedEnd: '2026-07-08T10:30:00',
  status: 'Todo',
  isInbox: false,
};

const taskSegmentLayerItem: CalendarLayerItem = {
  id: 'segment-1',
  layer: 'task-segments',
  objectType: 'task-segment',
  objectId: 'segment-1',
  title: 'Draft agenda segment',
  startsAt: '2026-07-08T11:00:00',
  endsAt: '2026-07-08T11:30:00',
  source: 'manual',
  status: 'Planned',
  color: '#2563eb',
  requiresConfirmation: false,
};

const calendars: CalendarResponse[] = [
  { id: 'calendar-1', name: 'Work', color: '#2563eb', kind: 'calendar', isDefault: true, canEdit: true },
  {
    id: 'calendar-outlook',
    name: 'Team',
    color: '#0f766e',
    kind: 'calendar',
    isDefault: false,
    outlookCalendarBindingId: 'binding-1',
    canEdit: true,
  },
];

const outlookEvent: EventResponse = {
  ...event,
  id: 'event-outlook',
  calendarId: 'calendar-outlook',
  uid: 'event-outlook-uid',
  title: 'Outlook planning review',
  source: 'outlook',
};

describe('buildCalendarEvents', () => {
  it('builds events with calendar accent and label', () => {
    const onlyEvents = buildCalendarEvents(
      [event],
      [plannedTask],
      [taskSegmentLayerItem],
      new Set(['events']),
      calendars,
    );
    assert.deepEqual(
      onlyEvents.map(item => item.title),
      ['Planning review'],
    );

    const evtResult = onlyEvents[0];
    const evtProps = evtResult.extendedProps as Record<string, unknown>;
    assert.equal(evtResult.backgroundColor, undefined,
      'Native event must not set hardcoded backgroundColor');
    assert.equal(evtResult.borderColor, undefined,
      'Native event must not set hardcoded borderColor');
    assert.equal(evtProps.type, 'event');
    assert.equal(evtProps.accentColor, '#2563eb',
      'Event must carry calendar accent color in extendedProps');
    assert.equal(evtProps.calendarLabel, 'Work',
      'Event must carry calendar label in extendedProps');
  });

  it('extends the Outlook calendar label', () => {
    const [outlookResult] = buildCalendarEvents(
      [outlookEvent],
      [],
      [],
      new Set(['events']),
      calendars,
    );
    const outlookProps = outlookResult.extendedProps as Record<string, unknown>;
    assert.equal(outlookProps.calendarLabel, 'Team (Outlook)',
      'Outlook calendar label must extend the native calendar label');
  });

  it('falls back to a neutral label when calendar metadata is missing', () => {
    const [missingCalendarResult] = buildCalendarEvents(
      [{ ...event, calendarId: 'missing-calendar' }],
      [],
      [],
      new Set(['events']),
      calendars,
    );
    const missingCalendarProps = missingCalendarResult.extendedProps as Record<string, unknown>;
    assert.equal(missingCalendarProps.calendarLabel, '日程',
      'Missing calendar metadata must not expose the manual source identifier');
  });

  it('merges events, tasks and layer items by enabled layers', () => {
    const eventsAndTaskSegments = buildCalendarEvents(
      [event],
      [plannedTask],
      [taskSegmentLayerItem],
      new Set(['events', 'task-segments']),
      calendars,
    );

    assert.deepEqual(
      eventsAndTaskSegments.map(item => item.title),
      ['Planning review', 'Draft agenda', 'Draft agenda segment'],
    );

    const taskResult = eventsAndTaskSegments[1];
    const taskProps = taskResult.extendedProps as Record<string, unknown>;
    assert.equal(taskResult.backgroundColor, undefined,
      'Task must not set hardcoded backgroundColor');
    assert.equal(taskResult.borderColor, undefined,
      'Task must not set hardcoded borderColor');
    assert.equal(taskProps.type, 'task');
    assert.equal(taskProps.accentColor, '#f59e0b',
      'Priority-2 task must carry priority accent in extendedProps');
    assert.equal(taskProps.calendarLabel, '任务',
      'Task without calendarId must carry task label');

    const layerResult = eventsAndTaskSegments[2];
    const layerProps = layerResult.extendedProps as Record<string, unknown>;
    assert.equal(layerResult.backgroundColor, 'transparent',
      'Layer item must keep transparent backgroundColor');
    assert.equal(layerResult.borderColor, 'transparent',
      'Layer item must keep transparent borderColor');
    assert.equal(layerProps.type, 'layer');
    assert.equal(layerProps.accentColor, '#2563eb',
      'Layer item must carry its fixture color as accentColor');
  });

  it('preserves showAs, importance and reminder semantics for the renderer', () => {
    const richEvent: EventResponse = {
      ...event,
      showAs: 'free',
      importance: 'high',
      isReminderOn: true,
      reminderMinutesBeforeStart: 15,
      categories: ['工作', '客户'],
      attendees: [{ name: '张三', email: 'zhangsan@example.com', type: 'required' }],
      attachmentReferences: [{ kind: 'outlook', id: 'att-1', name: '会议纪要.docx', canDownload: true }],
    };
    const [result] = buildCalendarEvents([richEvent], [], [], new Set(['events']), calendars);
    const raw = (result.extendedProps as { raw: EventResponse }).raw;
    assert.equal(raw.showAs, 'free');
    assert.equal(raw.importance, 'high');
    assert.equal(raw.isReminderOn, true);
    assert.deepEqual(raw.categories, ['工作', '客户']);
    assert.deepEqual(raw.attachmentReferences, richEvent.attachmentReferences);
  });

  it('preserves tentative showAs for the renderer', () => {
    const [result] = buildCalendarEvents(
      [{ ...event, showAs: 'tentative' }],
      [],
      [],
      new Set(['events']),
      calendars,
    );
    const raw = (result.extendedProps as { raw: EventResponse }).raw;
    assert.equal(raw.showAs, 'tentative');
  });

  it('preserves allDay for all-day events', () => {
    const [result] = buildCalendarEvents(
      [{ ...event, isAllDay: true }],
      [],
      [],
      new Set(['events']),
      calendars,
    );
    assert.equal(result.allDay, true);
  });

  it('falls back to the task due when no planned end exists', () => {
    const taskWithDue: TaskResponse = {
      ...plannedTask,
      plannedEnd: undefined,
      due: '2026-07-08T18:00:00',
    };
    const [result] = buildCalendarEvents(
      [],
      [taskWithDue],
      [],
      new Set(['task-segments']),
      calendars,
    );
    assert.equal(result.end, '2026-07-08T18:00:00');
  });

  it('maps priority 1 and 3 task accents', () => {
    const [high] = buildCalendarEvents(
      [],
      [{ ...plannedTask, id: 'p1', priority: 1 }],
      [],
      new Set(['task-segments']),
      calendars,
    );
    const [low] = buildCalendarEvents(
      [],
      [{ ...plannedTask, id: 'p3', priority: 3 }],
      [],
      new Set(['task-segments']),
      calendars,
    );
    assert.equal((high.extendedProps as { accentColor: string }).accentColor, '#ef4444');
    assert.equal((low.extendedProps as { accentColor: string }).accentColor, '#14b8a6');
  });

  it('drops tasks when task-segments layer is disabled', () => {
    const results = buildCalendarEvents(
      [],
      [plannedTask],
      [],
      new Set(['events']),
      calendars,
    );
    assert.deepEqual(results.map(item => item.title), []);
  });

  it('tags layer items with prefixed ids and class names', () => {
    const [result] = buildCalendarEvents(
      [],
      [],
      [taskSegmentLayerItem],
      new Set(['task-segments']),
      calendars,
    );
    assert.equal(result.id, 'layer-task-segments-segment-1');
    assert.deepEqual(result.classNames, ['pim-calendar-layer-task-segment']);
  });
});
