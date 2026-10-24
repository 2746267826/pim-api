import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  batchUpdateTasks,
  buildTasksPath,
  calendarApiPaths,
  getTasksPaged,
  planTask,
  previewCalendarDelete,
} from '../../src/client-web/src/api/calendar';

const taskListPageSource = readFileSync(
  new URL('../../src/client-web/src/pages/TaskListPage.tsx', import.meta.url),
  'utf8',
);

assert.equal(buildTasksPath(), '/calendar/tasks');
assert.equal(buildTasksPath(false), '/calendar/tasks');
assert.equal(buildTasksPath(true), '/calendar/tasks?inbox=true');
assert.equal(calendarApiPaths.calendarDeletePreview('abc'), '/calendar/calendars/abc/delete-preview');
assert.equal(calendarApiPaths.eventBatchDelete(), '/calendar/events/batch-delete');
assert.equal(calendarApiPaths.taskPlan('abc'), '/calendar/tasks/abc/plan');
assert.equal(calendarApiPaths.taskBatchUpdate(), '/calendar/tasks/batch-update');
assert.equal(calendarApiPaths.taskBatchDelete(), '/calendar/tasks/batch-delete');

const failures: unknown[] = [];
const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

async function main() {
  await assert.rejects(
    () => getTasksPaged(),
    requestCaptured
  );
  assert.equal(requests[0].url, '/api/v1/calendar/tasks?page=1&pageSize=50');
  assert.equal(requests[0].init?.method, undefined);

  await assert.rejects(
    () => getTasksPaged({ inbox: true }),
    requestCaptured
  );
  assert.equal(requests[1].url, '/api/v1/calendar/tasks?inbox=true&page=1&pageSize=50');
  assert.equal(requests[1].init?.method, undefined);

  await assert.rejects(
    () => getTasksPaged({
      inbox: true,
      search: 'focus',
      calendarId: 'task-book-1',
      status: 'COMPLETED',
      priority: 1,
      plannedFrom: '2026-05-27T00:00:00',
      plannedTo: '2026-05-27T23:59:59',
      dueFrom: '2026-05-27T00:00:00',
      dueTo: '2026-05-27T23:59:59',
      page: 1,
      pageSize: 100,
    }),
    requestCaptured
  );
  assert.equal(
    requests[2].url,
    '/api/v1/calendar/tasks?inbox=true&search=focus&calendarId=task-book-1&status=COMPLETED&priority=1&plannedFrom=2026-05-27T00%3A00%3A00&plannedTo=2026-05-27T23%3A59%3A59&dueFrom=2026-05-27T00%3A00%3A00&dueTo=2026-05-27T23%3A59%3A59&page=1&pageSize=100'
  );
  assert.equal(requests[2].init?.method, undefined);

  await assert.rejects(
    () => planTask('task-1', {
      plannedStart: '2026-05-27T09:00:00Z',
      plannedEnd: '2026-05-27T10:00:00Z',
      estimatedDuration: '01:00:00',
    }),
    requestCaptured
  );
  assert.equal(requests[3].url, '/api/v1/calendar/tasks/task-1/plan');
  assert.equal(requests[3].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[3].init?.body)), {
      plannedStart: '2026-05-27T09:00:00Z',
      plannedEnd: '2026-05-27T10:00:00Z',
      estimatedDuration: '01:00:00',
    });
  } catch (error) {
    failures.push(error);
  }

  await assert.rejects(
    () => batchUpdateTasks({
      ids: ['task-1', 'task-2'],
      status: 'done',
      priority: 2,
      calendarId: 'calendar-2',
    }),
    requestCaptured
  );
  assert.equal(requests[4].url, '/api/v1/calendar/tasks/batch-update');
  assert.equal(requests[4].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[4].init?.body)), {
      ids: ['task-1', 'task-2'],
      status: 'done',
      priority: 2,
      calendarId: 'calendar-2',
    });
  } catch (error) {
    failures.push(error);
  }

  await assert.rejects(() => previewCalendarDelete('calendar-1'), requestCaptured);
  assert.equal(requests[5].url, '/api/v1/calendar/calendars/calendar-1/delete-preview');
  try {
    assert.equal(requests[5].init?.method, 'POST');
    assert.deepEqual(JSON.parse(String(requests[5].init?.body)), {});
  } catch (error) {
    failures.push(error);
  }

  if (failures.length > 0) {
    throw new AggregateError(failures, 'calendar task API contract assertions failed');
  }

  assert.doesNotMatch(taskListPageSource, /sortTasksByDue/);
  assert.match(taskListPageSource, /pendingDeleteIds/);
  assert.match(taskListPageSource, /setSelectedIds\(current => pruneSelectedIds\(current, currentIds\)\)/);
  assert.match(taskListPageSource, /batchDeleteTasks\(ids\)/);
  assert.match(taskListPageSource, /显示前 \{items\.length\} 项/);
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
