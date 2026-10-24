import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { getPlannedEndForDrop, parseTimeSpanMs, toLocalDateTimeInputValue } from '../../src/client-web/src/utils/dropDuration';
import type { TaskResponse } from '../../src/client-web/src/types';

function makeTask(overrides: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id: 'task-1',
    calendarId: null,
    uid: 'task-1@pim',
    title: 'Task',
    description: null,
    priority: 2,
    estimatedDuration: null,
    minimumSegment: null,
    dtStart: null,
    due: null,
    status: 'OPEN',
    isInbox: true,
    sortOrder: 0,
    subTasks: [],
    plannedEnd: null,
    ...overrides,
  } as TaskResponse;
}

describe('dropDuration', () => {
  it('keeps the existing planned window duration when both ends are known', () => {
    const task = makeTask({
      dtStart: '2026-07-15T09:00:00Z',
      plannedEnd: '2026-07-15T09:30:00Z',
      estimatedDuration: '01:00:00',
    });

    assert.equal(getPlannedEndForDrop(task, '2026-07-16T10:00:00'), '2026-07-16T10:30');
  });

  it('uses estimatedDuration when no planned window exists', () => {
    const task = makeTask({ estimatedDuration: '01:30:00' });

    assert.equal(getPlannedEndForDrop(task, '2026-07-16T09:00:00'), '2026-07-16T10:30');
  });

  it('parses day-prefixed durations with fractional seconds', () => {
    assert.equal(parseTimeSpanMs('1.02:30:00'), (26.5 * 60 * 60) * 1000);
    assert.equal(parseTimeSpanMs('00:00:00.5'), 500);
    assert.equal(parseTimeSpanMs('01:00:00'), 3600 * 1000);
  });

  it('rejects malformed durations', () => {
    assert.equal(parseTimeSpanMs('01:99:00'), null);
    assert.equal(parseTimeSpanMs('01:00:99'), null);
    assert.equal(parseTimeSpanMs('abc'), null);
    assert.equal(parseTimeSpanMs(undefined), null);
  });

  it('falls back to the task due when no duration source exists', () => {
    const task = makeTask({ due: '2026-07-20T18:00:00Z' });

    assert.equal(getPlannedEndForDrop(task, '2026-07-16T09:00:00'), '2026-07-20T18:00:00Z');
  });

  it('falls back to due for an invalid planned start', () => {
    const task = makeTask({ due: '2026-07-20T18:00:00Z' });

    assert.equal(getPlannedEndForDrop(task, 'not-a-date'), '2026-07-20T18:00:00Z');
  });

  it('formats datetime-local values without seconds or zone', () => {
    assert.equal(toLocalDateTimeInputValue(new Date('2026-07-16T09:00:00')), '2026-07-16T09:00');
  });
});
