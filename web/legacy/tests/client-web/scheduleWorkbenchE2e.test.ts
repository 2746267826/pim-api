import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const packageJson = JSON.parse(readFileSync('src/client-web/package.json', 'utf8')) as {
  scripts: Record<string, string>;
};

const completeScript = packageJson.scripts['test:schedule-workbench-complete'] ?? '';

for (const expectedTest of [
  'endpointShellPage.test.tsx',
  'microsoftCalendarSyncApi.test.ts',
  'scheduleWorkbenchE2e.test.ts',
  'scheduleWorkbenchVisualAudit.test.ts',
  'beforeAfterDiff.test.tsx',
  'dropDuration.test.ts',
]) {
  assert.ok(
    completeScript.includes(expectedTest),
    `test:schedule-workbench-complete should run ${expectedTest}`,
  );
}

const appLayout = readFileSync('src/client-web/src/layout/AppLayout.tsx', 'utf8');
const apiClient = readFileSync('src/client-web/src/api/client.ts', 'utf8');
const endpointApi = readFileSync('src/client-web/src/api/endpoints.ts', 'utf8');

for (const route of [
  '/today',
  '/calendar',
  '/tasks',
  '/habits',
  '/reminders',
  '/reports',
  '/settings/sync',
  '/data-center',
  '/confirmations',
  '/audit/:objectType/:objectId',
  '/endpoint-shell',
]) {
  assert.match(appLayout, new RegExp(route.replace(/[/:]/g, match => `\\${match}`)));
}

assert.ok(apiClient.includes("localStorage.getItem('accessToken')"));
assert.ok(apiClient.includes("headers.set('Authorization'"));
assert.ok(endpointApi.includes('listEndpointStatuses'));
assert.ok(endpointApi.includes('handleEndpointNotificationAction'));
