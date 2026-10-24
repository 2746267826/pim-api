import assert from 'node:assert/strict';
import {
  addTaskChecklistItem,
  archiveReport,
  calendarApiPaths,
  createHabit,
  createProject,
  createReminder,
  createTaskBook,
  generateReport,
  getAuditExport,
  getProjects,
  getReports,
  pollOutlookDeviceCode,
  previewDataCenterBatch,
  requestReportSuggestionAction,
} from '../../src/client-web/src/api/calendar';
import {
  confirmOperationSecondLevel,
  confirmOperationStrict,
  getAuditTimeline,
  getRestorePreview,
  operationsApiPaths,
} from '../../src/client-web/src/api/operations';
import {
  endpointApiPaths,
  getEndpointCollectionQuality,
  handleEndpointNotificationAction,
  heartbeatEndpoint,
  listEndpointStatuses,
} from '../../src/client-web/src/api/endpoints';

assert.equal(calendarApiPaths.projects(), '/calendar/projects');
assert.equal(calendarApiPaths.taskBooks(), '/calendar/task-books');
assert.equal(calendarApiPaths.taskChecklist('task-1'), '/calendar/tasks/task-1/checklist');
assert.equal(calendarApiPaths.habits(), '/calendar/habits');
assert.equal(calendarApiPaths.reminders(), '/calendar/reminders');
assert.equal(calendarApiPaths.reports(), '/calendar/reports');
assert.equal(calendarApiPaths.generateReport(), '/calendar/reports/generate');
assert.equal(calendarApiPaths.archiveReport('report-1'), '/calendar/reports/report-1/archive');
assert.equal(calendarApiPaths.requestReportSuggestionAction('suggestion-1'), '/calendar/reports/suggestions/suggestion-1/request-action');
assert.equal(calendarApiPaths.outlookDeviceCodePoll(), '/calendar/outlook/device-code/poll');
assert.equal(calendarApiPaths.dataCenterBatchPreview(), '/calendar/data-center/batch/preview');
assert.equal(calendarApiPaths.dataCenterAuditExport(), '/calendar/data-center/audit/export');
assert.equal(operationsApiPaths.confirmSecondLevel('abc'), '/operations/confirmations/abc/confirm-second-level');
assert.equal(operationsApiPaths.confirmStrict('abc'), '/operations/confirmations/abc/confirm-strict');
assert.equal(operationsApiPaths.auditTimeline('task', 't1'), '/operations/audit/task/t1');
assert.equal(operationsApiPaths.restorePreview('audit-1'), '/operations/audit/audit-1/restore-preview');
assert.equal(endpointApiPaths.list(), '/endpoints');
assert.equal(endpointApiPaths.heartbeat('win-1'), '/endpoints/win-1/heartbeat');
assert.equal(endpointApiPaths.collectionQuality('win-1'), '/endpoints/win-1/collection-quality');
assert.equal(endpointApiPaths.notificationActions('win-1'), '/endpoints/win-1/notification-actions');

const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

async function main() {
  await assert.rejects(() => getProjects(), requestCaptured);
  assert.equal(requests[0].url, '/api/v1/calendar/projects');

  await assert.rejects(() => createProject({ name: '项目', description: null }), requestCaptured);
  assert.equal(requests[1].url, '/api/v1/calendar/projects');
  assert.equal(requests[1].init?.method, 'POST');

  await assert.rejects(() => createTaskBook({ domainProjectId: 'p1', name: '任务本', kind: 'task' }), requestCaptured);
  assert.equal(requests[2].url, '/api/v1/calendar/task-books');

  await assert.rejects(() => addTaskChecklistItem('task-1', { title: '检查项' }), requestCaptured);
  assert.equal(requests[3].url, '/api/v1/calendar/tasks/task-1/checklist');

  await assert.rejects(() => createHabit({ title: '运动', cadence: 'Daily' }), requestCaptured);
  assert.equal(requests[4].url, '/api/v1/calendar/habits');

  await assert.rejects(() => createReminder({ relatedObjectType: 'task', relatedObjectId: 'task-1', title: '提醒', scheduledAt: '2026-07-08T00:00:00Z' }), requestCaptured);
  assert.equal(requests[5].url, '/api/v1/calendar/reminders');

  await assert.rejects(() => getReports(), requestCaptured);
  assert.equal(requests[6].url, '/api/v1/calendar/reports');

  await assert.rejects(() => generateReport({ kind: 'Daily', date: '2026-07-08', projectId: null }), requestCaptured);
  assert.equal(requests[7].url, '/api/v1/calendar/reports/generate');

  await assert.rejects(() => archiveReport('report-1'), requestCaptured);
  assert.equal(requests[8].url, '/api/v1/calendar/reports/report-1/archive');

  await assert.rejects(() => requestReportSuggestionAction('suggestion-1'), requestCaptured);
  assert.equal(requests[9].url, '/api/v1/calendar/reports/suggestions/suggestion-1/request-action');

  await assert.rejects(() => pollOutlookDeviceCode('device-code'), requestCaptured);
  assert.equal(requests[10].url, '/api/v1/calendar/outlook/device-code/poll');
  assert.equal(requests[10].init?.method, 'POST');

  await assert.rejects(() => previewDataCenterBatch({ action: 'archive', objects: [{ objectType: 'task', objectId: 't1' }] }), requestCaptured);
  assert.equal(requests[11].url, '/api/v1/calendar/data-center/batch/preview');

  await assert.rejects(() => getAuditExport(), requestCaptured);
  assert.equal(requests[12].url, '/api/v1/calendar/data-center/audit/export');

  await assert.rejects(() => confirmOperationSecondLevel('abc'), requestCaptured);
  assert.equal(requests[13].url, '/api/v1/operations/confirmations/abc/confirm-second-level');

  await assert.rejects(() => confirmOperationStrict('abc'), requestCaptured);
  assert.equal(requests[14].url, '/api/v1/operations/confirmations/abc/confirm-strict');

  await assert.rejects(() => getAuditTimeline('task', 't1'), requestCaptured);
  assert.equal(requests[15].url, '/api/v1/operations/audit/task/t1');

  await assert.rejects(() => getRestorePreview('audit-1'), requestCaptured);
  assert.equal(requests[16].url, '/api/v1/operations/audit/audit-1/restore-preview');

  await assert.rejects(() => listEndpointStatuses(), requestCaptured);
  assert.equal(requests[17].url, '/api/v1/endpoints');

  await assert.rejects(() => heartbeatEndpoint('win-1', { platform: 'windows' }), requestCaptured);
  assert.equal(requests[18].url, '/api/v1/endpoints/win-1/heartbeat');

  await assert.rejects(() => getEndpointCollectionQuality('win-1'), requestCaptured);
  assert.equal(requests[19].url, '/api/v1/endpoints/win-1/collection-quality');

  await assert.rejects(() => handleEndpointNotificationAction('win-1', { action: 'dismiss', riskLevel: 'L1LowRiskAction' }), requestCaptured);
  assert.equal(requests[20].url, '/api/v1/endpoints/win-1/notification-actions');
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
