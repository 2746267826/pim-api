import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertPageSourceContains(path: string, snippets: string[]) {
  const source = readFileSync(path, 'utf8');

  for (const snippet of snippets) {
    assert.ok(source.includes(snippet), `${path} should contain ${snippet}`);
  }
}

assertPageSourceContains('src/client-web/src/pages/TodayPage.tsx', [
  '日程任务工作台',
  '待确认',
  '微软同步',
  '提醒队列',
  '报告',
]);

assertPageSourceContains('src/client-web/src/pages/CalendarPage.tsx', [
  'CalendarLayerToolbar',
  'outlookOnly',
  'ai-placeholders',
]);

assertPageSourceContains('src/client-web/src/pages/TaskListPage.tsx', [
  'TaskHierarchyPanel',
  'TaskSegmentEditor',
  'Checklist',
]);

assertPageSourceContains('src/client-web/src/pages/HabitsPage.tsx', [
  'HabitRoutineEditor',
  '完成历史',
  '投射到日历',
]);
