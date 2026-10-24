import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const appLayout = readFileSync('src/client-web/src/layout/AppLayout.tsx', 'utf8');
const sidebar = readFileSync('src/client-web/src/layout/Sidebar.tsx', 'utf8');
const settingsPage = readFileSync('src/client-web/src/pages/SettingsPage.tsx', 'utf8');
const workbenchPage = readFileSync('src/client-web/src/pages/WorkbenchPage.tsx', 'utf8');

for (const forbidden of [
  'Schedule Workbench',
  'Operational dashboard',
  'Workbench density',
  'Standard',
  'Dense',
  'Focus',
  'Data Center',
  'Schedule layers',
  'Pending confirmations',
  'Outlook sync',
  'Last sync batch',
  'Schedule Layers',
  'Pending Confirmations',
  'Outlook Sync',
  'Endpoints And Status Links',
  'No pending confirmations.',
  'Open Calendar',
  'Review all',
  'Configure',
  'Last synced',
  'System Status',
]) {
  assert.ok(!workbenchPage.includes(forbidden), `WorkbenchPage should not expose English copy: ${forbidden}`);
}

for (const forbidden of [
  "label: 'Workbench'",
  "label: 'Confirmations'",
  "label: 'Sync'",
  "label: 'Data Center'",
  "label: 'Reminders'",
  "label: 'Reports'",
  "label: 'Habits'",
  "label: 'PC",
  "label: 'App",
  "short: 'WB'",
  "short: 'CF'",
  "short: 'SY'",
  "short: 'DC'",
  "short: 'RM'",
  "short: 'RP'",
  "short: 'HB'",
  "short: 'PC'",
]) {
  assert.ok(!sidebar.includes(forbidden), `Sidebar navigation should be Chinese-only: ${forbidden}`);
}

assert.ok(!sidebar.includes("path: '/sync'"), 'Sync should not be a top-level sidebar item');
assert.ok(appLayout.includes('path="/settings/sync"'), 'Sync page should live under settings routes');
assert.ok(!appLayout.includes('path="/sync" element={<SyncPage />}'), 'Top-level /sync route should not render SyncPage directly');
assert.ok(settingsPage.includes('/settings/sync'), 'Settings page should link to the sync panel');
