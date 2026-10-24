import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';
import type { CalendarResponse, EventResponse } from '../../src/client-web/src/types';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

let buildCalendarEvents: typeof import('../../src/client-web/src/utils/calendarEvents').buildCalendarEvents;

before(async () => {
  const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  (globalThis as any).window = dom.window;
  (globalThis as any).document = dom.window.document;
  (globalThis as any).Node = dom.window.Node;
  (globalThis as any).HTMLElement = dom.window.HTMLElement;
  (globalThis as any).DOMParser = dom.window.DOMParser;
  const mod = await import('../../src/client-web/src/utils/calendarEvents');
  buildCalendarEvents = mod.buildCalendarEvents;
});

const baseEvent: EventResponse = {
  id: 'e1',
  calendarId: 'cal-1',
  uid: 'uid-1',
  title: 'Standup',
  dtStart: '2026-07-10T09:00:00.000Z',
  dtEnd: '2026-07-10T09:30:00.000Z',
  status: 'CONFIRMED',
  source: 'manual',
};

const calendars: CalendarResponse[] = [
  { id: 'cal-1', name: 'Work', color: '#2563eb', kind: 'calendar', isDefault: true, canEdit: true },
];

describe('calendarRecurrence - IsCancelled grey', () => {
  it('cancelled via isCancelled flag gets calendar-event--cancelled class', () => {
    const ev: EventResponse = { ...baseEvent, isCancelled: true };
    const [result] = buildCalendarEvents([ev], [], [], new Set(['events']), calendars);
    assert.ok((result.classNames ?? []).includes('calendar-event--cancelled'), 'should have cancelled class');
  });

  it('cancelled via status CANCELLED fallback gets grey class', () => {
    const ev: EventResponse = { ...baseEvent, status: 'CANCELLED', isCancelled: undefined as any };
    const [result] = buildCalendarEvents([ev], [], [], new Set(['events']), calendars);
    assert.ok((result.classNames ?? []).includes('calendar-event--cancelled'));
  });

  it('non-cancelled has no cancelled class', () => {
    const ev: EventResponse = { ...baseEvent, isCancelled: false, status: 'CONFIRMED' };
    const [result] = buildCalendarEvents([ev], [], [], new Set(['events']), calendars);
    assert.ok(!((result.classNames ?? []) as string[]).includes('calendar-event--cancelled'));
  });

  it('preserves rrule and series fields for recurrence badge', () => {
    const master: EventResponse = { ...baseEvent, id: 'm1', rrule: 'FREQ=WEEKLY;BYDAY=MO,WE', isSeriesMaster: true };
    const occurrence: EventResponse = { ...baseEvent, id: 'occ1', seriesMasterId: 'm1', recurrenceId: '2026-07-10T09:00:00.000Z' };
    const results = buildCalendarEvents([master, occurrence], [], [], new Set(['events']), calendars);
    const rawMaster = (results[0].extendedProps as any).raw as EventResponse;
    const rawOcc = (results[1].extendedProps as any).raw as EventResponse;
    assert.equal(rawMaster.rrule, 'FREQ=WEEKLY;BYDAY=MO,WE');
    assert.equal(rawMaster.isSeriesMaster, true);
    assert.equal(rawOcc.seriesMasterId, 'm1');
    assert.ok(rawOcc.recurrenceId);
  });

  it('SeriesMasterId grouping kept in raw for UI', () => {
    const ev: EventResponse = { ...baseEvent, seriesMasterId: 'master-123', recurrenceId: '2026-07-14T09:00:00.000Z', isException: true };
    const [result] = buildCalendarEvents([ev], [], [], new Set(['events']), calendars);
    const raw = (result.extendedProps as any).raw as EventResponse;
    assert.equal(raw.seriesMasterId, 'master-123');
    assert.equal(raw.isException, true);
  });
});
