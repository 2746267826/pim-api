import assert from 'node:assert/strict';
import type {
  TodaySectionRegistry,
  TodaySection,
  TodaySectionStatus,
  CalendarTasksTodayData,
} from '../../src/client-web/src/types';

const status: TodaySectionStatus = 'warning';

const registry: TodaySectionRegistry = {
  date: '2026-05-25',
  pcBusinessDate: '2026-05-25',
  generatedAt: '2026-05-25T00:00:00Z',
  sections: [
    {
      id: 'calendar.tasks',
      kind: 'calendar.tasks',
      status: 'available',
      links: [{ rel: 'self', href: '/api/v1/today/sections/calendar.tasks?date=2026-05-25' }],
    },
  ],
};

const tasksData: CalendarTasksTodayData = {
  incompleteCount: 1,
  dueTodayTasks: [],
  overdueTasks: [],
  unscheduledTasks: [],
};

const section: TodaySection<CalendarTasksTodayData> = {
  id: 'calendar.tasks',
  kind: 'calendar.tasks',
  status,
  generatedAt: '2026-05-25T00:00:00Z',
  data: tasksData,
  links: [{ rel: 'details', href: '/tasks' }],
  error: null,
};

assert.equal(registry.sections[0].kind, 'calendar.tasks');
assert.equal(section.data.incompleteCount, 1);
assert.equal(section.error, null);
