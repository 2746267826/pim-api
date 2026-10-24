import assert from 'node:assert/strict';
import {
  calendarApiPaths,
  previewRecycleRestore,
  restoreRecycleItem,
} from '../../src/client-web/src/api/calendar';

const failures: unknown[] = [];

assert.equal(calendarApiPaths.recycleBin(), '/calendar/recycle-bin');
assert.equal(
  calendarApiPaths.recycleBin({ type: 'event', search: 'plan', page: 2, pageSize: 20 }),
  '/calendar/recycle-bin?type=event&search=plan&page=2&pageSize=20'
);
try {
  assert.equal(
    calendarApiPaths.recycleBin({ pageSize: 20, page: 2, search: 'plan', type: 'event' }),
    '/calendar/recycle-bin?type=event&search=plan&page=2&pageSize=20'
  );
} catch (error) {
  failures.push(error);
}
assert.equal(
  calendarApiPaths.recycleRestorePreview('event', 'abc'),
  '/calendar/recycle-bin/event/abc/restore-preview'
);
assert.equal(
  calendarApiPaths.recycleRestore('event', 'abc'),
  '/calendar/recycle-bin/event/abc/restore'
);
assert.equal(calendarApiPaths.taskPlan('abc'), '/calendar/tasks/abc/plan');
assert.equal(calendarApiPaths.taskBatchUpdate(), '/calendar/tasks/batch-update');
assert.equal(calendarApiPaths.taskBatchDelete(), '/calendar/tasks/batch-delete');

const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

async function main() {
  await assert.rejects(() => previewRecycleRestore('event', 'abc'), requestCaptured);
  assert.equal(requests[0].url, '/api/v1/calendar/recycle-bin/event/abc/restore-preview');
  try {
    assert.equal(requests[0].init?.method, 'POST');
  } catch (error) {
    failures.push(error);
  }

  await assert.rejects(() => restoreRecycleItem('event', 'abc', true), requestCaptured);
  assert.equal(requests[1].url, '/api/v1/calendar/recycle-bin/event/abc/restore');
  assert.equal(requests[1].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[1].init?.body)), { restoreAsCopy: true });
  } catch (error) {
    failures.push(error);
  }

  if (failures.length > 0) {
    throw new AggregateError(failures, 'recycle bin API contract assertions failed');
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
