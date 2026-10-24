import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const screenshotTargets = [
  '/today',
  '/calendar',
  '/tasks',
  '/habits',
] as const;

const appLayout = readFileSync('src/client-web/src/layout/AppLayout.tsx', 'utf8');
const pages = {
  '/today': readFileSync('src/client-web/src/pages/TodayPage.tsx', 'utf8'),
  '/calendar': readFileSync('src/client-web/src/pages/CalendarPage.tsx', 'utf8'),
  '/tasks': readFileSync('src/client-web/src/pages/TaskListPage.tsx', 'utf8'),
  '/habits': readFileSync('src/client-web/src/pages/HabitsPage.tsx', 'utf8'),
};

for (const route of screenshotTargets) {
  assert.match(appLayout, new RegExp(route.replace('/', '\\/')));
  assert.ok(pages[route].includes('pim-panel'), `${route} should render workbench panels`);
}

assert.ok(pages['/today'].includes('日程任务工作台'));
assert.ok(pages['/calendar'].includes('CalendarLayerToolbar'));
assert.ok(pages['/tasks'].includes('TaskHierarchyPanel'));
assert.ok(pages['/habits'].includes('HabitRoutineEditor'));
