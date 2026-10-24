import assert from 'node:assert/strict';
import { scheduleWorkbenchZhCN } from '../../src/client-web/src/i18n/scheduleWorkbench.zh-CN';

for (const key of [
  'workbench.title',
  'today.title',
  'calendar.layers.events',
  'sync.deviceCode',
  'confirmations.secondLevelRequired',
  'dataCenter.batchPreview',
  'reminders.title',
  'reports.title',
  'habits.title',
  'endpoints.windows',
  'endpoints.android',
]) {
  assert.equal(typeof scheduleWorkbenchZhCN[key], 'string', key);
  assert.equal(/[A-Za-z]{4,}/.test(scheduleWorkbenchZhCN[key]), false, key);
}
