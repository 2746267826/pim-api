import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  isoToDatetimeLocal,
  datetimeLocalToUtcIso,
  minimumEndValue,
  isEndAfterStart,
  normalizeZoneId,
} from '../../src/client-web/src/utils/dateTimeInput';

import {
  dotnetDurationToHoursMinutes,
  hoursMinutesToIsoDuration,
  isValidDuration,
  durationErrorMessage,
} from '../../src/client-web/src/utils/durationInput';

import {
  resolveCalendarId,
  hasWritableCalendar,
  noWritableCalendarMessage,
} from '../../src/client-web/src/utils/calendarSelection';

import type { CalendarResponse } from '../../src/client-web/src/types';

// ---------------------------------------------------------------------------
// dateTimeInput.ts
// ---------------------------------------------------------------------------
describe('dateTimeInput', () => {
  describe('isoToDatetimeLocal', () => {
    it('converts ISO UTC to Asia/Shanghai datetime-local', () => {
      const result = isoToDatetimeLocal('2026-07-20T06:00:00.000Z', 'Asia/Shanghai');
      assert.equal(result, '2026-07-20T14:00');
    });

    it('returns empty string for invalid ISO input', () => {
      assert.equal(isoToDatetimeLocal('not-a-date'), '');
      assert.equal(isoToDatetimeLocal(''), '');
    });

    it('converts UTC ISO with the Windows timezone ID China Standard Time', () => {
      const result = isoToDatetimeLocal('2026-07-20T06:00:00.000Z', 'China Standard Time');
      assert.equal(result, '2026-07-20T14:00');
    });

    it('falls back to the local zone for an unknown timezone ID instead of returning empty', () => {
      const result = isoToDatetimeLocal('2026-07-20T06:00:00.000Z', 'Totally Unknown Time');
      assert.notEqual(result, '', 'isoToDatetimeLocal must not return empty for a valid ISO input');
    });
  });

  describe('normalizeZoneId', () => {
    it('maps Windows timezone IDs to their IANA equivalents', () => {
      assert.equal(normalizeZoneId('China Standard Time'), 'Asia/Shanghai');
    });

    it('passes through IANA IDs unchanged', () => {
      assert.equal(normalizeZoneId('Asia/Shanghai'), 'Asia/Shanghai');
    });

    it('passes through empty or unknown values unchanged', () => {
      assert.equal(normalizeZoneId(''), '');
      assert.equal(normalizeZoneId(undefined), undefined);
      assert.equal(normalizeZoneId('Totally Unknown Time'), 'Totally Unknown Time');
    });
  });

  describe('datetimeLocalToUtcIso', () => {
    it('converts Asia/Shanghai datetime-local to UTC ISO with .000Z', () => {
      const result = datetimeLocalToUtcIso('2026-07-20T14:00', 'Asia/Shanghai');
      assert.equal(result, '2026-07-20T06:00:00.000Z');
    });

    it('returns empty string for invalid datetime-local input', () => {
      assert.equal(datetimeLocalToUtcIso('not-a-datetime', 'Asia/Shanghai'), '');
      assert.equal(datetimeLocalToUtcIso('', 'Asia/Shanghai'), '');
    });

    it('normalizes the Windows timezone ID China Standard Time to Asia/Shanghai', () => {
      const result = datetimeLocalToUtcIso('2026-07-20T14:00', 'China Standard Time');
      assert.equal(result, '2026-07-20T06:00:00.000Z');
    });

    it('falls back to the local zone for an unknown timezone ID instead of dropping the moment', () => {
      const result = datetimeLocalToUtcIso('2026-07-20T14:00', 'Totally Unknown Time');
      assert.notEqual(result, '', 'datetimeLocalToUtcIso must not return empty for a valid local value');
    });
  });

  describe('minimumEndValue', () => {
    it('returns one minute after start', () => {
      assert.equal(minimumEndValue('2026-07-20T14:00'), '2026-07-20T14:01');
    });

    it('handles minute rollover at end of hour', () => {
      assert.equal(minimumEndValue('2026-07-20T14:59'), '2026-07-20T15:00');
    });

    it('returns empty string for empty or invalid start', () => {
      assert.equal(minimumEndValue(''), '');
      assert.equal(minimumEndValue('not-valid'), '');
    });
  });

  describe('isEndAfterStart', () => {
    it('returns true when end is strictly later', () => {
      assert.equal(isEndAfterStart('2026-07-20T14:00', '2026-07-20T15:00'), true);
    });

    it('returns false when end equals start', () => {
      assert.equal(isEndAfterStart('2026-07-20T14:00', '2026-07-20T14:00'), false);
    });

    it('returns false when end is before start', () => {
      assert.equal(isEndAfterStart('2026-07-20T15:00', '2026-07-20T14:00'), false);
    });

    it('returns false for empty or invalid values', () => {
      assert.equal(isEndAfterStart('', '2026-07-20T15:00'), false);
      assert.equal(isEndAfterStart('2026-07-20T14:00', ''), false);
      assert.equal(isEndAfterStart('bad', 'also-bad'), false);
    });
  });
});

// ---------------------------------------------------------------------------
// durationInput.ts
// ---------------------------------------------------------------------------
describe('durationInput', () => {
  describe('dotnetDurationToHoursMinutes', () => {
    it('parses hh:mm:ss format (01:30:00)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:00'), { hours: 1, minutes: 30 });
    });

    it('rolls days into hours (1.02:30:00)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('1.02:30:00'), { hours: 26, minutes: 30 });
    });

    it('ignores fractional seconds', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:00.1234567'), { hours: 1, minutes: 30 });
    });

    it('returns { hours: 0, minutes: 30 } for empty input', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes(''), { hours: 0, minutes: 30 });
    });

    it('returns { hours: 0, minutes: 30 } for undefined input', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes(undefined), { hours: 0, minutes: 30 });
    });

    it('returns { hours: 0, minutes: 30 } for invalid input', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('not-a-duration'), { hours: 0, minutes: 30 });
    });

    it('rejects 01:60:00 (minutes out of range)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:60:00'), { hours: 0, minutes: 30 });
    });

    it('rejects 01:30:60 (seconds out of range)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:60'), { hours: 0, minutes: 30 });
    });

    it('rejects 01:30:00.12345678 (8 fractional digits)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:00.12345678'), { hours: 0, minutes: 30 });
    });

    it('accepts 01:30:00.1 (1 fractional digit)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:00.1'), { hours: 1, minutes: 30 });
    });

    it('accepts 01:30:00.1234567 (7 fractional digits)', () => {
      assert.deepEqual(dotnetDurationToHoursMinutes('01:30:00.1234567'), { hours: 1, minutes: 30 });
    });
  });

  describe('hoursMinutesToIsoDuration', () => {
    it('produces PT1H30M for mixed hours and minutes', () => {
      assert.equal(hoursMinutesToIsoDuration(1, 30), 'PT1H30M');
    });

    it('produces PT45M for minutes only', () => {
      assert.equal(hoursMinutesToIsoDuration(0, 45), 'PT45M');
    });

    it('produces PT2H for hours only', () => {
      assert.equal(hoursMinutesToIsoDuration(2, 0), 'PT2H');
    });

    it('returns empty string when total minutes <= 0', () => {
      assert.equal(hoursMinutesToIsoDuration(0, 0), '');
      assert.equal(hoursMinutesToIsoDuration(-1, 30), '');
    });

    it('floors non-integer values', () => {
      assert.equal(hoursMinutesToIsoDuration(1.9, 30.7), 'PT1H30M');
    });

    it('normalizes 0h90m into PT1H30M', () => {
      assert.equal(hoursMinutesToIsoDuration(0, 90), 'PT1H30M');
    });

    it('normalizes 2h120m into PT4H', () => {
      assert.equal(hoursMinutesToIsoDuration(2, 120), 'PT4H');
    });

    it('computes total from negative hours plus overflow minutes (-1h90m = PT30M)', () => {
      assert.equal(hoursMinutesToIsoDuration(-1, 90), 'PT30M');
    });

    it('returns empty string for Infinity hours', () => {
      assert.equal(hoursMinutesToIsoDuration(Infinity, 0), '');
    });

    it('returns empty string for NaN minutes', () => {
      assert.equal(hoursMinutesToIsoDuration(0, NaN), '');
    });
  });

  describe('isValidDuration', () => {
    it('accepts valid positive duration', () => {
      assert.equal(isValidDuration('1', '30'), true);
      assert.equal(isValidDuration('0', '1'), true);
    });

    it('rejects zero total minutes', () => {
      assert.equal(isValidDuration('0', '0'), false);
    });

    it('rejects 60 minutes (out of range)', () => {
      assert.equal(isValidDuration('1', '60'), false);
    });

    it('rejects negative hours', () => {
      assert.equal(isValidDuration('-1', '30'), false);
    });

    it('rejects negative minutes', () => {
      assert.equal(isValidDuration('1', '-1'), false);
    });

    it('rejects fractional hours', () => {
      assert.equal(isValidDuration('1.5', '30'), false);
    });

    it('rejects non-numeric input', () => {
      assert.equal(isValidDuration('abc', '30'), false);
      assert.equal(isValidDuration('1', 'def'), false);
    });
  });

  describe('durationErrorMessage', () => {
    it('returns Chinese message containing 分钟', () => {
      const msg = durationErrorMessage();
      assert.ok(msg.includes('分钟'), `message "${msg}" should contain 分钟`);
    });
  });
});

// ---------------------------------------------------------------------------
// calendarSelection.ts
// ---------------------------------------------------------------------------
describe('calendarSelection', () => {
  const writableDefault: CalendarResponse = {
    id: 'cal-1',
    name: 'Default',
    color: '#ff0000',
    kind: 'google',
    isDefault: true,
    canEdit: true,
  };

  const writableNonDefault: CalendarResponse = {
    id: 'cal-2',
    name: 'Work',
    color: '#00ff00',
    kind: 'google',
    isDefault: false,
    canEdit: true,
  };

  const readOnly: CalendarResponse = {
    id: 'cal-3',
    name: 'ReadOnly',
    color: '#0000ff',
    kind: 'google',
    isDefault: false,
    canEdit: false,
  };

  const canEditUndefined: CalendarResponse = {
    id: 'cal-4',
    name: 'UndefinedEdit',
    color: '#ffff00',
    kind: 'google',
    isDefault: false,
  };

  const calendars = [writableDefault, writableNonDefault, readOnly, canEditUndefined];

  describe('resolveCalendarId', () => {
    it('keeps currentId when it exists in the list', () => {
      assert.equal(resolveCalendarId(calendars, 'cal-2', new Set()), 'cal-2');
    });

    it('chooses visible writable default calendar when currentId is not found', () => {
      assert.equal(resolveCalendarId(calendars, 'cal-nonexistent', new Set()), 'cal-1');
    });

    it('chooses first visible writable calendar when default is hidden', () => {
      assert.equal(resolveCalendarId(calendars, 'cal-nonexistent', new Set(['cal-1'])), 'cal-2');
    });

    it('excludes read-only calendar from default selection', () => {
      const noWritable = [readOnly];
      assert.equal(resolveCalendarId(noWritable, 'nonexistent', new Set()), '');
    });

    it('treats undefined canEdit as writable', () => {
      const mixed = [readOnly, canEditUndefined];
      assert.equal(resolveCalendarId(mixed, 'nonexistent', new Set()), 'cal-4');
    });

    it('returns empty string when no writable calendar', () => {
      assert.equal(resolveCalendarId([], 'gone', new Set()), '');
    });

    it('accepts undefined currentId and falls back to default', () => {
      assert.equal(resolveCalendarId(calendars, undefined, new Set()), 'cal-1');
    });

    it('excludes hidden writable calendars from fallback selection', () => {
      const hidden = new Set(['cal-1', 'cal-4']);
      assert.equal(resolveCalendarId(calendars, undefined, hidden), 'cal-2');
    });
  });

  describe('hasWritableCalendar', () => {
    it('returns true when a writable calendar exists', () => {
      assert.equal(hasWritableCalendar(calendars, new Set()), true);
    });

    it('returns false when all are read-only', () => {
      assert.equal(hasWritableCalendar([readOnly], new Set()), false);
    });

    it('returns false for empty list', () => {
      assert.equal(hasWritableCalendar([], new Set()), false);
    });

    it('returns false when all writable calendars are hidden', () => {
      const hidden = new Set(['cal-1', 'cal-2', 'cal-4']);
      assert.equal(hasWritableCalendar(calendars, hidden), false);
    });

    it('returns true when at least one writable calendar is visible', () => {
      const hidden = new Set(['cal-1', 'cal-2']);
      assert.equal(hasWritableCalendar(calendars, hidden), true);
    });
  });

  describe('noWritableCalendarMessage', () => {
    it('returns correct Chinese message', () => {
      assert.equal(
        noWritableCalendarMessage(),
        '没有可用的可写日历，请先在设置中添加或启用日历',
      );
    });
  });
});
