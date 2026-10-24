import type {
  CalendarDeletePreviewResponse,
  CalendarOperationResult,
  CalendarOperationSample,
  CalendarRecycleBinItem,
  CalendarRestorePreviewResponse,
  EventResponse,
  ImportReport,
  TaskResponse,
} from '../../src/client-web/src/types';

const operationSample: CalendarOperationSample = {
  id: 'event-1',
  type: 'event',
  title: 'Planning',
  start: '2026-05-27T00:00:00Z',
  end: '2026-05-27T01:00:00Z',
  bookName: 'Work',
};

const deletePreview: CalendarDeletePreviewResponse = {
  targetType: 'calendar',
  targetId: 'calendar-1',
  title: 'Work',
  operationKind: 'delete_calendar',
  affectedCount: 1,
  samples: [operationSample],
  summary: 'Delete 1 calendar item',
  requiresStrictConfirmation: true,
};

const operationResult: CalendarOperationResult = {
  operation: 'delete',
  operationId: 'op-1',
  affectedCount: 1,
  affectedIds: ['event-1'],
  samples: [operationSample],
  message: 'Deleted 1 event',
};

const recycleItem: CalendarRecycleBinItem = {
  id: 'trash-1',
  type: 'event',
  title: 'Planning',
  deletedAt: '2026-05-27T00:00:00Z',
  bookName: 'Work',
  start: '2026-05-27T00:00:00Z',
  end: '2026-05-27T01:00:00Z',
  source: 'event',
  deletedByOperationId: 'op-1',
  deletedByOperationKind: 'delete_event',
};

const restorePreview: CalendarRestorePreviewResponse = {
  targetType: 'event',
  targetId: 'event-1',
  title: 'Planning',
  restoreCount: 1,
  samples: [operationSample],
  conflicts: [
    {
      deletedId: 'calendar-1',
      deletedType: 'calendar',
      activeId: 'calendar-2',
      activeType: 'calendar',
      reason: 'duplicate',
      title: 'Work',
    },
  ],
  canRestoreWithoutConflict: false,
};

const event: EventResponse = {
  id: 'event-1',
  calendarId: 'cal-1',
  uid: 'uid-1',
  title: 'Planning',
  dtStart: '2026-05-27T00:00:00Z',
  dtEnd: '2026-05-27T01:00:00Z',
  status: 'confirmed',
  source: 'ics',
  isAllDay: false,
  timeZoneId: 'Asia/Shanghai',
  sourceTimeZoneId: 'UTC',
  sourceUid: 'source-uid-1',
  recurrenceId: 'recurrence-1',
  exDatesJson: '[]',
  recurrenceMetadataJson: '{}',
};

const importReport: ImportReport = {
  imported: 1,
  skipped: 1,
  skippedReasons: {
    duplicate: 1,
  },
  samples: [
    {
      uid: 'uid-2',
      reason: 'duplicate',
      title: 'Duplicate event',
      start: '2026-05-27T02:00:00Z',
    },
  ],
};

const task: TaskResponse = {
  id: 'task-1',
  calendarId: 'cal-1',
  title: 'Draft plan',
  priority: 1,
  status: 'todo',
  isInbox: false,
  plannedEnd: '2026-05-27T01:00:00Z',
};

// @ts-expect-error EventResponse intentionally excludes raw ICS payloads.
event.sourceIcsComponent = 'BEGIN:VEVENT';

void deletePreview;
void operationResult;
void recycleItem;
void restorePreview;
void importReport;
void task;
