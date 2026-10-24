import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  calendarApiPaths,
  createOutlookDeviceCode,
  createTaskExecutionSegment,
  deleteTaskExecutionSegment,
  getCalendarLayers,
  getOutlookSettings,
  getOutlookSyncBatches,
  listTaskExecutionSegments,
  queryDataCenter,
  runOutlookSync,
  updateOutlookSettings,
} from '../../src/client-web/src/api/calendar';
import {
  confirmOperation,
  getConfirmationDetail,
  getPendingConfirmations,
  operationsApiPaths,
  rejectOperation,
} from '../../src/client-web/src/api/operations';

function source(path: string) {
  return readFileSync(path, 'utf8');
}

function assertSourceContains(path: string, snippets: string[]) {
  const text = source(path);

  for (const snippet of snippets) {
    assert.ok(text.includes(snippet), `${path} should contain ${snippet}`);
  }
}

assertSourceContains('src/client-web/src/layout/AppLayout.tsx', [
  '/workbench',
  '/settings/sync',
  '/data-center',
  '/confirmations',
  '/reminders',
  '/reports',
  '/habits',
]);
assertSourceContains('src/client-web/src/layout/Sidebar.tsx', [
  '/workbench',
  '/data-center',
  '/confirmations',
  '/reminders',
  '/reports',
  '/habits',
]);
assertSourceContains('src/client-web/src/pages/TodayPage.tsx', ['日程任务工作台']);
assertSourceContains('src/client-web/src/pages/CalendarPage.tsx', ['task-segments']);

assert.equal(calendarApiPaths.taskSegments('task-1'), '/calendar/tasks/task-1/segments');
assert.equal(
  calendarApiPaths.calendarLayers({
    start: '2026-07-08T00:00:00Z',
    end: '2026-07-09T00:00:00Z',
    layers: ['events', 'task-segments'],
    outlookOnly: true,
  }),
  '/calendar/layers?start=2026-07-08T00%3A00%3A00Z&end=2026-07-09T00%3A00%3A00Z&layers=events%2Ctask-segments&outlookOnly=true',
);
assert.equal(calendarApiPaths.dataCenterQuery(), '/calendar/data-center/query');
assert.equal(calendarApiPaths.outlookSettings(), '/calendar/outlook/settings');
assert.equal(calendarApiPaths.outlookDeviceCode(), '/calendar/outlook/device-code');
assert.equal(calendarApiPaths.outlookSyncBatches(), '/calendar/outlook/sync/batches');
assert.equal(calendarApiPaths.outlookSync(), '/calendar/outlook/sync');
assert.equal(operationsApiPaths.pendingConfirmations(), '/operations/confirmations/pending');
assert.equal(operationsApiPaths.detail('abc'), '/operations/confirmations/abc');
assert.equal(operationsApiPaths.confirm('abc'), '/operations/confirmations/abc/confirm');
assert.equal(operationsApiPaths.reject('abc'), '/operations/confirmations/abc/reject');

const failures: unknown[] = [];
const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

function assertJsonBody(index: number, expected: unknown) {
  try {
    assert.deepEqual(JSON.parse(String(requests[index].init?.body)), expected);
  } catch (error) {
    failures.push(error);
  }
}

async function main() {
  await assert.rejects(() => listTaskExecutionSegments('task-1'), requestCaptured);
  assert.equal(requests[0].url, '/api/v1/calendar/tasks/task-1/segments');
  assert.equal(requests[0].init?.method, undefined);
  assert.equal(requests[0].init?.body, undefined);

  const segmentRequest = {
    startsAt: '2026-07-08T09:00:00Z',
    endsAt: '2026-07-08T10:00:00Z',
    status: 'Planned',
    source: 'user',
    planningReason: 'Deep work',
  };
  await assert.rejects(() => createTaskExecutionSegment('task-1', segmentRequest), requestCaptured);
  assert.equal(requests[1].url, '/api/v1/calendar/tasks/task-1/segments');
  assert.equal(requests[1].init?.method, 'POST');
  assertJsonBody(1, segmentRequest);

  await assert.rejects(() => deleteTaskExecutionSegment('task-1', 'segment-1'), requestCaptured);
  assert.equal(requests[2].url, '/api/v1/calendar/tasks/task-1/segments/segment-1');
  assert.equal(requests[2].init?.method, 'DELETE');
  assert.equal(requests[2].init?.body, undefined);

  await assert.rejects(
    () =>
      getCalendarLayers({
        start: '2026-07-08T00:00:00Z',
        end: '2026-07-09T00:00:00Z',
        layers: ['events', 'task-segments'],
        outlookOnly: true,
      }),
    requestCaptured,
  );
  assert.equal(
    requests[3].url,
    '/api/v1/calendar/layers?start=2026-07-08T00%3A00%3A00Z&end=2026-07-09T00%3A00%3A00Z&layers=events%2Ctask-segments&outlookOnly=true',
  );
  assert.equal(requests[3].init?.method, undefined);
  assert.equal(requests[3].init?.body, undefined);

  const dataCenterRequest = {
    search: 'sync',
    objectType: 'event',
    source: 'outlook',
    pendingOnly: true,
    page: 2,
    pageSize: 25,
  };
  await assert.rejects(() => queryDataCenter(dataCenterRequest), requestCaptured);
  assert.equal(requests[4].url, '/api/v1/calendar/data-center/query');
  assert.equal(requests[4].init?.method, 'POST');
  assertJsonBody(4, dataCenterRequest);

  await assert.rejects(() => getOutlookSettings(), requestCaptured);
  assert.equal(requests[5].url, '/api/v1/calendar/outlook/settings');
  assert.equal(requests[5].init?.method, undefined);
  assert.equal(requests[5].init?.body, undefined);

  const outlookSettingsRequest = {
    clientId: '11111111-1111-1111-1111-111111111111',
  };
  await assert.rejects(() => updateOutlookSettings(outlookSettingsRequest), requestCaptured);
  assert.equal(requests[6].url, '/api/v1/calendar/outlook/settings');
  assert.equal(requests[6].init?.method, 'PUT');
  assertJsonBody(6, outlookSettingsRequest);

  await assert.rejects(() => createOutlookDeviceCode(), requestCaptured);
  assert.equal(requests[7].url, '/api/v1/calendar/outlook/device-code');
  assert.equal(requests[7].init?.method, 'POST');
  assertJsonBody(7, {});

  await assert.rejects(() => runOutlookSync({ mode: 'normal' }), requestCaptured);
  assert.equal(requests[8].url, '/api/v1/calendar/outlook/sync');
  assert.equal(requests[8].init?.method, 'POST');
  assertJsonBody(8, { mode: 'normal' });

  await assert.rejects(() => getOutlookSyncBatches(), requestCaptured);
  assert.equal(requests[9].url, '/api/v1/calendar/outlook/sync/batches');
  assert.equal(requests[9].init?.method, undefined);
  assert.equal(requests[9].init?.body, undefined);

  await assert.rejects(() => getPendingConfirmations(), requestCaptured);
  assert.equal(requests[10].url, '/api/v1/operations/confirmations/pending');
  assert.equal(requests[10].init?.method, undefined);
  assert.equal(requests[10].init?.body, undefined);

  await assert.rejects(() => getConfirmationDetail('abc'), requestCaptured);
  assert.equal(requests[11].url, '/api/v1/operations/confirmations/abc');
  assert.equal(requests[11].init?.method, undefined);
  assert.equal(requests[11].init?.body, undefined);

  await assert.rejects(() => confirmOperation('abc'), requestCaptured);
  assert.equal(requests[12].url, '/api/v1/operations/confirmations/abc/confirm');
  assert.equal(requests[12].init?.method, 'POST');
  assertJsonBody(12, {});

  await assert.rejects(() => rejectOperation('abc'), requestCaptured);
  assert.equal(requests[13].url, '/api/v1/operations/confirmations/abc/reject');
  assert.equal(requests[13].init?.method, 'POST');
  assertJsonBody(13, {});

  if (failures.length > 0) {
    throw new AggregateError(failures, 'schedule workbench API contract assertions failed');
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
