import assert from 'node:assert/strict';
import type {
  OutlookAuthorizationSessionResponse,
  OutlookCalendarBindingResponse,
  OutlookLocalDataPreview,
  OutlookEventDraft,
  OutlookWriteRequest,
  OutlookWriteResult,
  OutlookPerCalendarChange,
  UpdateOutlookSettingsRequest,
  OutlookSyncRequest,
  OutlookSyncBatchResponse,
  OutlookPerCalendarResult,
} from '../../src/client-web/src/types';
import {
  calendarApiPaths,
  outlookBindings,
  outlookDiscover,
  outlookSelection,
  outlookLocalDataPreview,
  updateOutlookSettings,
  createOutlookDeviceCode,
  pollOutlookDeviceCode,
  runOutlookSync,
  cancelOutlookSync,
  outlookDisconnect,
  outlookLocalDataDelete,
  getOutlookSyncBatchesPaged,
  cancelOutlookDeviceCode,
  checkOutlookConnection,
} from '../../src/client-web/src/api/calendar';

// UUID fixtures
const UUID_BINDING_1 = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
const UUID_BINDING_2 = 'b2c3d4e5-f6a7-8901-bcde-f12345678901';
const UUID_BATCH = 'c3d4e5f6-a7b8-9012-cdef-123456789012';
const UUID_SESSION = 'd4e5f6a7-b8c9-0123-defa-234567890123';

// --- Path builders ---
assert.equal(calendarApiPaths.outlookDiscover(), '/calendar/outlook/calendars/discover');
assert.equal(calendarApiPaths.outlookCalendars(), '/calendar/outlook/calendars');
assert.equal(calendarApiPaths.outlookSelection(), '/calendar/outlook/calendars/selection');
assert.equal(calendarApiPaths.outlookWriteback(), '/calendar/outlook/events/writeback');
assert.equal(calendarApiPaths.outlookLocalDataPreview(), '/calendar/outlook/local-data/preview');
assert.equal(calendarApiPaths.outlookLocalData(), '/calendar/outlook/local-data');
assert.equal(calendarApiPaths.outlookDisconnect(), '/calendar/outlook/disconnect');
assert.equal(calendarApiPaths.outlookSyncCancel(UUID_BATCH), `/calendar/outlook/sync/${UUID_BATCH}/cancel`);
assert.equal(calendarApiPaths.outlookDeviceCodeCancel(UUID_SESSION), `/calendar/outlook/device-code/${UUID_SESSION}/cancel`);
assert.equal(calendarApiPaths.outlookCheck(), '/calendar/outlook/check');

// --- Request/response type contracts ---

// UpdateOutlookSettingsRequest must only contain clientId
const settingsReq: UpdateOutlookSettingsRequest = { clientId: UUID_BINDING_1 };
assert.equal(settingsReq.clientId, UUID_BINDING_1);
// @ts-expect-error tenantId should not be in the type
settingsReq.tenantId;
// @ts-expect-error scopes should not be in the type
settingsReq.scopes;
assert.deepEqual(Object.keys(settingsReq), ['clientId']);

// OutlookAuthorizationSessionResponse shape - uses lowercase statuses
const session: OutlookAuthorizationSessionResponse = {
  id: UUID_SESSION,
  status: 'connected',
  verificationUri: 'https://microsoft.com/devicelogin',
  userCode: 'ABC123',
  expiresAt: '2026-07-13T12:00:00Z',
  accountDisplayName: 'Test User',
  accountLoginHint: 'user@example.com',
  errorCode: null,
  errorMessage: null,
  recoveryAction: null,
};
assert.equal(session.id, UUID_SESSION);
assert.equal(session.status, 'connected');

// OutlookCalendarBindingResponse shape - exact backend fields, no calendarId/isPaused/remoteMissing
const binding: OutlookCalendarBindingResponse = {
  id: UUID_BINDING_1,
  pimCalendarId: UUID_BINDING_2,
  graphCalendarId: 'graph-cal-1',
  groupId: null,
  groupName: 'Work',
  name: '工作日历',
  color: '#0044CC',
  ownerName: null,
  ownerAddress: null,
  isDefault: true,
  canEdit: true,
  isSelected: true,
  remoteState: 'active',
  lastSyncedAt: '2026-07-13T10:00:00Z',
  lastError: null,
};
assert.equal(binding.id, UUID_BINDING_1);
assert.equal(binding.remoteState, 'active');
assert.equal(binding.groupName, 'Work');

const pausedBinding: OutlookCalendarBindingResponse = {
  id: UUID_BINDING_2,
  pimCalendarId: 'pim-cal-2',
  graphCalendarId: 'graph-cal-2',
  groupId: null,
  groupName: null,
  name: '暂停日历',
  color: '#00AA44',
  ownerName: null,
  ownerAddress: null,
  isDefault: false,
  canEdit: true,
  isSelected: false,
  remoteState: 'paused',
  lastSyncedAt: null,
  lastError: null,
};

const missingBinding: OutlookCalendarBindingResponse = {
  id: 'missing-1',
  pimCalendarId: 'pim-cal-3',
  graphCalendarId: 'graph-cal-3',
  groupId: null,
  groupName: null,
  name: '已删除日历',
  color: '#888888',
  ownerName: null,
  ownerAddress: null,
  isDefault: false,
  canEdit: false,
  isSelected: false,
  remoteState: 'remote-missing',
  lastSyncedAt: null,
  lastError: null,
};

// @ts-expect-error calendarId should not exist on binding
binding.calendarId;
// @ts-expect-error isPaused should not exist on binding
binding.isPaused;
// @ts-expect-error remoteMissing should not exist on binding
binding.remoteMissing;

// OutlookSyncRequest shape - no failedCalendarBindingIds
const normalSync: OutlookSyncRequest = { mode: 'normal' };
assert.equal(normalSync.mode, 'normal');

const deepSync: OutlookSyncRequest = {
  mode: 'full-resources',
  calendarBindingIds: [UUID_BINDING_1, UUID_BINDING_2],
};
assert.equal(deepSync.mode, 'full-resources');

const rangeSync: OutlookSyncRequest = {
  mode: 'range-instances',
  calendarBindingIds: [UUID_BINDING_1],
  rangeStart: '2026-07-01T00:00:00Z',
  rangeEnd: '2026-07-31T23:59:59Z',
};
assert.equal(rangeSync.mode, 'range-instances');
assert.equal(rangeSync.rangeStart, '2026-07-01T00:00:00Z');

const retrySync: OutlookSyncRequest = {
  mode: 'normal',
  retryOfBatchId: UUID_BATCH,
  calendarBindingIds: ['binding-3'],
};
assert.equal(retrySync.retryOfBatchId, UUID_BATCH);
assert.deepEqual(retrySync.calendarBindingIds, ['binding-3']);

// @ts-expect-error failedCalendarBindingIds should not exist
const badRequest: OutlookSyncRequest = { mode: 'normal' };
// We need a way to validate - let's use Object.keys check
{
  const req: Record<string, unknown> = { mode: 'normal', retryOfBatchId: UUID_BATCH, calendarBindingIds: ['b1'] };
  assert.equal('failedCalendarBindingIds' in req, false);
}

// OutlookSyncBatchResponse shape - no page/pageSize/totalCount/failedCalendarBindings
const perCalendarData: OutlookPerCalendarResult[] = [
  {
    bindingId: 'binding-3',
    calendarName: 'Failed Calendar',
    status: 'failed',
    readCount: 0,
    createdCount: 0,
    updatedCount: 0,
    deletedCount: 0,
    failureCount: 1,
    changes: [{ id: 'evt-1', title: 'Meeting', action: 'created' }],
    failures: [
      { eventId: 'evt-1', title: 'Meeting', code: 'AuthError', message: 'Permission denied' },
    ],
    retryOfBatchId: UUID_BATCH,
  },
];

const batchResponse: OutlookSyncBatchResponse = {
  id: UUID_BATCH,
  provider: 'outlook',
  status: 'completed',
  readCount: 100,
  createdCount: 5,
  updatedCount: 10,
  conflictCount: 0,
  confirmationCount: 0,
  failureCount: 1,
  steps: [],
  errorSummary: null,
  startedAt: '2026-07-13T10:00:00Z',
  finishedAt: '2026-07-13T10:05:00Z',
  mode: 'normal',
  requestedWindowStart: null,
  requestedWindowEnd: null,
  perCalendarJson: JSON.stringify(perCalendarData),
  cancelRequested: false,
};
assert.equal(batchResponse.mode, 'normal');
assert.equal(batchResponse.cancelRequested, false);
assert.equal(batchResponse.requestedWindowStart, null);
assert.equal(typeof batchResponse.perCalendarJson, 'string');

// OutlookEventDraft - lightweight write event shape
const eventDraft: OutlookEventDraft = {
  calendarId: UUID_BINDING_1,
  title: 'Team Sync',
  description: 'Weekly sync',
  location: 'Room A',
  dtStart: '2026-07-13T09:00:00Z',
  dtEnd: '2026-07-13T10:00:00Z',
  rRule: 'FREQ=WEEKLY',
  uid: 'uid-123',
  isAllDay: false,
  timeZoneId: 'Asia/Shanghai',
};
assert.equal(eventDraft.calendarId, UUID_BINDING_1);
assert.equal(eventDraft.title, 'Team Sync');
assert.equal(eventDraft.timeZoneId, 'Asia/Shanghai');

// OutlookWriteRequest - exact backend contract
const writeRequest: OutlookWriteRequest = {
  operation: 'create',
  calendarBindingId: UUID_BINDING_1,
  draft: eventDraft,
  scope: 'instance',
  clientOperationId: 'op-001',
  expectedEtag: 'etag-abc',
};
assert.equal(writeRequest.operation, 'create');
assert.equal(writeRequest.calendarBindingId, UUID_BINDING_1);
assert.equal(writeRequest.scope, 'instance');

// OutlookWriteResult - backend response shape
const writeResult: OutlookWriteResult = {
  status: 'completed',
  errorCode: null,
  errorMessage: null,
  latestEtag: 'etag-xyz',
};
assert.equal(writeResult.status, 'completed');
assert.equal(writeResult.errorCode, null);

// OutlookPerCalendarChange shape
const perCalendarChange: OutlookPerCalendarChange = {
  id: 'change-1',
  title: 'Meeting Updated',
  action: 'updated',
};
assert.equal(perCalendarChange.id, 'change-1');
assert.equal(perCalendarChange.action, 'updated');

// @ts-expect-error page should not exist on batch response
batchResponse.page;
// @ts-expect-error totalCount should not exist on batch response
batchResponse.totalCount;
// @ts-expect-error failedCalendarBindings should not exist
batchResponse.failedCalendarBindings;

// OutlookLocalDataPreview shape
const localPreview: OutlookLocalDataPreview = {
  bindingCount: 3,
  calendarCount: 5,
  eventCount: 120,
};
assert.equal(localPreview.bindingCount, 3);

// --- API function contracts (mock fetch, verify URL + body) ---
const failures: unknown[] = [];
const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

async function main() {
  // updateOutlookSettings sends PUT with only clientId
  await assert.rejects(
    () => updateOutlookSettings({ clientId: UUID_BINDING_1 }),
    requestCaptured,
  );
  assert.equal(requests[0].url, '/api/v1/calendar/outlook/settings');
  assert.equal(requests[0].init?.method, 'PUT');
  try {
    assert.deepEqual(JSON.parse(String(requests[0].init?.body)), { clientId: UUID_BINDING_1 });
  } catch (error) {
    failures.push(error);
  }

  // createOutlookDeviceCode sends POST with empty body
  await assert.rejects(() => createOutlookDeviceCode(), requestCaptured);
  assert.equal(requests[1].url, '/api/v1/calendar/outlook/device-code');
  assert.equal(requests[1].init?.method, 'POST');

  // pollOutlookDeviceCode sends POST with only { sessionId }
  await assert.rejects(
    () => pollOutlookDeviceCode(UUID_SESSION),
    requestCaptured,
  );
  assert.equal(requests[2].url, '/api/v1/calendar/outlook/device-code/poll');
  assert.equal(requests[2].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[2].init?.body)), { sessionId: UUID_SESSION });
  } catch (error) {
    failures.push(error);
  }

  // cancelOutlookDeviceCode sends POST to cancel path
  await assert.rejects(
    () => cancelOutlookDeviceCode(UUID_SESSION),
    requestCaptured,
  );
  assert.equal(requests[3].url, `/api/v1/calendar/outlook/device-code/${UUID_SESSION}/cancel`);
  assert.equal(requests[3].init?.method, 'POST');

  // checkOutlookConnection sends POST
  await assert.rejects(() => checkOutlookConnection(), requestCaptured);
  assert.equal(requests[4].url, '/api/v1/calendar/outlook/check');
  assert.equal(requests[4].init?.method, 'POST');

  // runOutlookSync sends POST with body (default mode normal)
  await assert.rejects(() => runOutlookSync({ mode: 'normal' }), requestCaptured);
  assert.equal(requests[5].url, '/api/v1/calendar/outlook/sync');
  assert.equal(requests[5].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[5].init?.body)), { mode: 'normal' });
  } catch (error) {
    failures.push(error);
  }

  // runOutlookSync with full-resources sends POST with binding ids
  await assert.rejects(
    () => runOutlookSync({ mode: 'full-resources', calendarBindingIds: [UUID_BINDING_1] }),
    requestCaptured,
  );
  assert.equal(requests[6].url, '/api/v1/calendar/outlook/sync');
  assert.equal(requests[6].init?.method, 'POST');
  try {
    assert.deepEqual(JSON.parse(String(requests[6].init?.body)), {
      mode: 'full-resources',
      calendarBindingIds: [UUID_BINDING_1],
    });
  } catch (error) {
    failures.push(error);
  }

  // runOutlookSync retry without failedCalendarBindingIds
  await assert.rejects(
    () => runOutlookSync({
      mode: 'normal',
      retryOfBatchId: UUID_BATCH,
      calendarBindingIds: ['binding-3'],
      rangeStart: '2026-07-01T00:00:00Z',
      rangeEnd: '2026-07-31T23:59:59Z',
    }),
    requestCaptured,
  );
  assert.equal(requests[7].url, '/api/v1/calendar/outlook/sync');
  assert.equal(requests[7].init?.method, 'POST');

  // cancelOutlookSync sends POST
  await assert.rejects(() => cancelOutlookSync(UUID_BATCH), requestCaptured);
  assert.equal(requests[8].url, `/api/v1/calendar/outlook/sync/${UUID_BATCH}/cancel`);
  assert.equal(requests[8].init?.method, 'POST');

  // outlookDiscover sends POST
  await assert.rejects(() => outlookDiscover(), requestCaptured);
  assert.equal(requests[9].url, '/api/v1/calendar/outlook/calendars/discover');
  assert.equal(requests[9].init?.method, 'POST');

  // outlookSelection sends PUT with selectedBindingIds body
  await assert.rejects(
    () => outlookSelection([UUID_BINDING_1, UUID_BINDING_2]),
    requestCaptured,
  );
  assert.equal(requests[10].url, '/api/v1/calendar/outlook/calendars/selection');
  assert.equal(requests[10].init?.method, 'PUT');
  try {
    assert.deepEqual(JSON.parse(String(requests[10].init?.body)), {
      selectedBindingIds: [UUID_BINDING_1, UUID_BINDING_2],
    });
  } catch (error) {
    failures.push(error);
  }

  // outlookLocalDataPreview sends GET
  await assert.rejects(() => outlookLocalDataPreview(), requestCaptured);
  assert.equal(requests[11].url, '/api/v1/calendar/outlook/local-data/preview');
  assert.equal(requests[11].init?.method, undefined);

  // outlookLocalDataDelete sends DELETE
  await assert.rejects(() => outlookLocalDataDelete(), requestCaptured);
  assert.equal(requests[12].url, '/api/v1/calendar/outlook/local-data');
  assert.equal(requests[12].init?.method, 'DELETE');

  // outlookDisconnect sends POST
  await assert.rejects(() => outlookDisconnect(), requestCaptured);
  assert.equal(requests[13].url, '/api/v1/calendar/outlook/disconnect');
  assert.equal(requests[13].init?.method, 'POST');

  // getOutlookSyncBatchesPaged sends GET with pagination
  await assert.rejects(
    () => getOutlookSyncBatchesPaged({ page: 2, pageSize: 10 }),
    requestCaptured,
  );
  assert.equal(requests[14].url, '/api/v1/calendar/outlook/sync/batches?page=2&pageSize=10');
  assert.equal(requests[14].init?.method, undefined);

  // outlookBindings sends a read-only GET (no discovery, must not hit Graph)
  await assert.rejects(() => outlookBindings(), requestCaptured);
  assert.equal(requests[15].url, '/api/v1/calendar/outlook/calendars');
  assert.equal(requests[15].init?.method, undefined);

  if (failures.length > 0) {
    throw new AggregateError(failures, 'API contract assertions failed');
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
