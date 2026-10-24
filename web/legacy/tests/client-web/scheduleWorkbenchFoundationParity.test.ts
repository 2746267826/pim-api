import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const appLayout = readFileSync('src/client-web/src/layout/AppLayout.tsx', 'utf8');
const sidebar = readFileSync('src/client-web/src/layout/Sidebar.tsx', 'utf8');
const settingsPage = readFileSync('src/client-web/src/pages/SettingsPage.tsx', 'utf8');
const types = readFileSync('src/client-web/src/types/index.ts', 'utf8');

for (const route of ['/workbench', '/settings/sync', '/data-center', '/confirmations', '/reminders', '/reports', '/habits']) {
  assert.match(appLayout, new RegExp(route.replace('/', '\\/')));
}

for (const route of ['/workbench', '/data-center', '/confirmations', '/reminders', '/reports', '/habits']) {
  assert.match(sidebar, new RegExp(route.replace('/', '\\/')));
}

assert.match(settingsPage, /\/settings\/sync/);

for (const symbol of ['OperationConfirmation', 'CalendarLayerResponse', 'OutlookSyncBatchResponse', 'DataCenterQueryResponse']) {
  assert.match(types, new RegExp(`interface ${symbol}|type ${symbol}`));
}
