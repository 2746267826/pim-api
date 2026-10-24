import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertSourceContains(path: string, snippets: string[]) {
  const source = readFileSync(path, 'utf8');

  for (const snippet of snippets) {
    assert.ok(source.includes(snippet), `${path} should contain ${snippet}`);
  }
}

assertSourceContains('src/client-web/src/pages/EndpointShellPage.tsx', [
  'EndpointShellPage',
  'listEndpointStatuses',
  'getEndpointCollectionQuality',
  'heartbeatEndpoint',
  'handleEndpointNotificationAction',
  'collection quality',
  'notification action',
  'online-only boundary',
]);

assertSourceContains('src/client-web/src/layout/AppLayout.tsx', [
  'EndpointShellPage',
  '/endpoint-shell',
]);

assertSourceContains('src/client-web/src/api/endpoints.ts', [
  "return '/endpoints'",
  'collection-quality',
  'notification-actions',
]);
