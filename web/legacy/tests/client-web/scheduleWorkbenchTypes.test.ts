import assert from 'node:assert/strict';
import type {
  CalendarLayerId,
  CalendarLayerResponse,
  DataCenterQueryResponse,
  OperationConfirmation,
  OperationRiskLevel,
  OutlookAuthorizationSessionResponse,
  OutlookCalendarBindingResponse,
  OutlookEventDraft,
  OutlookLocalDataPreview,
  OutlookPerCalendarChange,
  OutlookPerCalendarResult,
  OutlookSettingsResponse,
  OutlookSyncBatchPage,
  OutlookSyncBatchResponse,
  OutlookSyncRequest,
  OutlookWriteRequest,
  OutlookWriteResult,
  TaskExecutionSegmentResponse,
  WorkbenchDensityMode,
} from '../../src/client-web/src/types';

const riskLevel: OperationRiskLevel = 'L3ExternalSourceOrWriteback';
const layerId: CalendarLayerId = 'task-segments';
const habitLayerId: CalendarLayerId = 'habits';
const availabilityLayerId: CalendarLayerId = 'availability';
const aiPlaceholderLayerId: CalendarLayerId = 'ai-placeholders';
const densityMode: WorkbenchDensityMode = 'focus';
const denseDensityMode: WorkbenchDensityMode = 'dense';
const standardDensityMode: WorkbenchDensityMode = 'standard';

const taskSegment: TaskExecutionSegmentResponse = {
  id: 'segment-1',
  taskId: 'task-1',
  taskTitle: 'Draft launch plan',
  startsAt: '2026-07-08T09:00:00Z',
  endsAt: '2026-07-08T10:00:00Z',
  status: 'Planned',
  source: 'manual',
  planningReason: 'Priority block',
  confirmationId: 'confirmation-1',
};

const calendarLayers: CalendarLayerResponse = {
  start: '2026-07-08T00:00:00Z',
  end: '2026-07-09T00:00:00Z',
  items: [
    {
      id: 'layer-item-1',
      layer: layerId,
      objectType: 'task-segment',
      objectId: 'segment-1',
      title: 'Draft launch plan',
      startsAt: taskSegment.startsAt,
      endsAt: taskSegment.endsAt,
      source: 'manual',
      status: 'Planned',
      color: '#2563eb',
      requiresConfirmation: true,
    },
  ],
};

const dataCenterQuery: DataCenterQueryResponse = {
  items: [
    {
      objectType: 'event',
      objectId: 'event-1',
      title: 'Outlook planning',
      source: 'outlook',
      status: 'Pending',
      startsAt: '2026-07-08T11:00:00Z',
      endsAt: '2026-07-08T12:00:00Z',
      summary: 'Awaiting confirmation',
    },
  ],
  page: 1,
  pageSize: 50,
  totalCount: 1,
};

const outlookSettings: OutlookSettingsResponse = {
  provider: 'outlook',
  tenantId: 'common',
  clientId: 'client-1',
  scopes: 'Calendars.ReadWrite offline_access',
  status: 'connected',
  tokenHealth: 'Healthy',
  lastSyncedAt: '2026-07-08T08:00:00Z',
  lastError: null,
  uiStatus: 'connected',
  activeAuthorization: null,
};

const deviceCode: OutlookAuthorizationSessionResponse = {
  id: 'session-1',
  status: 'waiting-for-user',
  verificationUri: 'https://microsoft.com/devicelogin',
  userCode: 'ABCD-EFGH',
  expiresAt: '2026-07-08T08:15:00Z',
  accountDisplayName: null,
  accountLoginHint: null,
  errorCode: null,
  errorMessage: null,
  recoveryAction: null,
};

const calendarBinding: OutlookCalendarBindingResponse = {
  id: '11111111-1111-1111-1111-111111111111',
  pimCalendarId: '22222222-2222-2222-2222-222222222222',
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
  lastSyncedAt: '2026-07-08T08:00:00Z',
  lastError: null,
};

const syncRequest: OutlookSyncRequest = {
  mode: 'normal',
  calendarBindingIds: ['11111111-1111-1111-1111-111111111111'],
  rangeStart: '2026-07-01T00:00:00Z',
  rangeEnd: '2026-07-31T23:59:59Z',
  retryOfBatchId: '33333333-3333-3333-3333-333333333333',
};

const localDataPreview: OutlookLocalDataPreview = {
  bindingCount: 3,
  calendarCount: 5,
  eventCount: 120,
};

const syncBatch: OutlookSyncBatchResponse = {
  id: '33333333-3333-3333-3333-333333333333',
  provider: 'outlook',
  status: 'completed',
  readCount: 10,
  createdCount: 2,
  updatedCount: 3,
  conflictCount: 1,
  confirmationCount: 1,
  failureCount: 0,
  steps: [
    {
      name: 'Read calendar',
      status: 'Succeeded',
      detail: 'Imported 10 items',
      at: '2026-07-08T08:00:00Z',
    },
  ],
  errorSummary: null,
  startedAt: '2026-07-08T08:00:00Z',
  finishedAt: '2026-07-08T08:01:00Z',
  mode: 'normal',
  requestedWindowStart: null,
  requestedWindowEnd: null,
  perCalendarJson: null,
  cancelRequested: false,
};

const perCalendarResult: OutlookPerCalendarResult = {
  bindingId: '11111111-1111-1111-1111-111111111111',
  calendarName: '工作日历',
  status: 'completed',
  readCount: 5,
  createdCount: 1,
  updatedCount: 2,
  deletedCount: 0,
  failureCount: 0,
  changes: [{ id: 'ch-1', title: 'Event A', action: 'created' }],
  failures: [],
};

const syncBatchPage: OutlookSyncBatchPage = {
  items: [syncBatch],
  total: 1,
  page: 1,
  pageSize: 20,
};

const writeDraft: OutlookEventDraft = {
  calendarId: 'cal-1',
  title: 'Meeting',
  dtStart: '2026-07-13T09:00:00Z',
  dtEnd: '2026-07-13T10:00:00Z',
  timeZoneId: 'Asia/Shanghai',
};

const writeRequest: OutlookWriteRequest = {
  operation: 'create',
  calendarBindingId: 'b1',
  draft: writeDraft,
  scope: 'instance',
  clientOperationId: 'op-1',
};

const writeResult: OutlookWriteResult = {
  status: 'completed',
  errorCode: null,
  errorMessage: null,
};

const perCalendarChange: OutlookPerCalendarChange = {
  id: 'ch-1',
  title: 'Event A',
  action: 'created',
};

const confirmation: OperationConfirmation = {
  id: 'confirmation-1',
  requestedByUserId: 'user-1',
  operationType: 'outlook.writeback',
  summary: 'Write back Outlook task block',
  riskLevel,
  source: 'outlook',
  payloadJson: '{}',
  previewJson: '{}',
  status: 'Pending',
  expiresAt: '2026-07-08T09:00:00Z',
  createdAt: '2026-07-08T08:00:00Z',
  confirmedAt: null,
  executedAt: null,
  resultJson: null,
  correlationId: 'correlation-1',
  changedFields: ['startsAt', 'endsAt'],
  allowedActions: ['confirm', 'reject'],
  objectType: 'task-segment',
  objectId: 'segment-1',
  requiresSecondLevelConfirmation: false,
};

assert.equal(riskLevel, 'L3ExternalSourceOrWriteback');
assert.equal(layerId, 'task-segments');
assert.equal(habitLayerId, 'habits');
assert.equal(availabilityLayerId, 'availability');
assert.equal(aiPlaceholderLayerId, 'ai-placeholders');
assert.equal(densityMode, 'focus');
assert.equal(denseDensityMode, 'dense');
assert.equal(standardDensityMode, 'standard');

void calendarLayers;
void dataCenterQuery;
void outlookSettings;
void deviceCode;
void calendarBinding;
void syncRequest;
void localDataPreview;
void syncBatch;
void perCalendarResult;
void syncBatchPage;
void writeDraft;
void writeRequest;
void writeResult;
void perCalendarChange;
void confirmation;
