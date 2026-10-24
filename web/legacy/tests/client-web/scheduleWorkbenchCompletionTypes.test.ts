import assert from 'node:assert/strict';
import type {
  AuditVersion,
  CalendarLayerId,
  DomainProject,
  EndpointPlatform,
  EndpointStatus,
  HabitRoutine,
  NotificationActionResult,
  ReminderSummary,
  ReportArtifact,
  SyncConflict,
  TaskBook,
  TaskChecklistItem,
  TaskPlanningState,
} from '../../src/client-web/src/types';

const project: DomainProject = { id: 'p1', name: '项目', description: null, status: 'Active' };
const book: TaskBook = { id: 'b1', domainProjectId: 'p1', name: '任务本', kind: 'task', status: 'Active' };
const checklist: TaskChecklistItem = { id: 'c1', taskId: 't1', title: '检查项', isDone: false, sortOrder: 1 };
const habit: HabitRoutine = { id: 'h1', title: '运动', cadence: 'Daily', source: 'manual', status: 'Active' };
const reminder: ReminderSummary = { id: 'r1', title: '提醒', riskLevel: 'L1LowRiskAction', channels: ['Web'], status: 'Open' };
const report: ReportArtifact = { id: 'rp1', kind: 'Daily', title: '日报', riskLevel: 'L0AutomaticArtifact', generatedAt: '2026-07-08T00:00:00Z' };
const audit: AuditVersion = { id: 'a1', objectType: 'task', objectId: 't1', beforeJson: '{}', afterJson: '{}', changedFields: [], createdAt: '2026-07-08T00:00:00Z' };
const conflict: SyncConflict = { id: 's1', provider: 'outlook', objectType: 'event', objectId: 'e1', changedFields: ['location'], status: 'Pending' };
const endpoint: EndpointStatus = { deviceId: 'win-1', platform: 'windows', uploadStatus: 'Healthy', collectionCacheCount: 0, onlineOnlyBlockedCount: 0 };
const taskState: TaskPlanningState = 'Planned';
const layer: CalendarLayerId = 'task-segments';
const platform: EndpointPlatform = 'android';
const notificationResult: NotificationActionResult = 'OpenDetailRequired';

assert.equal(project.name, '项目');
void book;
void checklist;
void habit;
void reminder;
void report;
void audit;
void conflict;
void endpoint;
void taskState;
void layer;
void platform;
void notificationResult;
