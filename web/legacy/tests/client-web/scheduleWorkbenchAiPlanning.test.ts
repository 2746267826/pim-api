import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  calendarApiPaths,
  getAiPlaceholders,
  generateAiPlan,
  confirmAiPlaceholder,
  dismissAiPlaceholder,
} from '../../src/client-web/src/api/calendar';

// 1. Verify API path definitions
assert.equal(calendarApiPaths.aiPlaceholders(), '/calendar/ai-placeholders');
assert.equal(calendarApiPaths.aiPlaceholders('Suggested'), '/calendar/ai-placeholders?status=Suggested');
assert.equal(calendarApiPaths.aiPlaceholdersGenerate(), '/calendar/ai-placeholders/generate');
assert.equal(calendarApiPaths.aiPlaceholderConfirm('ph-123'), '/calendar/ai-placeholders/ph-123/confirm');
assert.equal(calendarApiPaths.aiPlaceholderDismiss('ph-123'), '/calendar/ai-placeholders/ph-123/dismiss');

// 2. Mock fetch and verify request dispatching
const requests: Array<{ url: string; init?: RequestInit }> = [];
const requestCaptured = new Error('request captured');
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), init });
  throw requestCaptured;
}) as typeof fetch;

async function runTests() {
  // Test getAiPlaceholders
  await assert.rejects(() => getAiPlaceholders('Suggested'), requestCaptured);
  assert.equal(requests[0].url, '/api/v1/calendar/ai-placeholders?status=Suggested');
  assert.equal(requests[0].init?.method, undefined);

  // Test generateAiPlan
  await assert.rejects(() => generateAiPlan({ horizonDays: 7 }), requestCaptured);
  assert.equal(requests[1].url, '/api/v1/calendar/ai-placeholders/generate');
  assert.equal(requests[1].init?.method, 'POST');
  assert.equal(requests[1].init?.body, JSON.stringify({ horizonDays: 7 }));

  // Test confirmAiPlaceholder
  await assert.rejects(() => confirmAiPlaceholder('ph-456'), requestCaptured);
  assert.equal(requests[2].url, '/api/v1/calendar/ai-placeholders/ph-456/confirm');
  assert.equal(requests[2].init?.method, 'POST');

  // Test dismissAiPlaceholder
  await assert.rejects(() => dismissAiPlaceholder('ph-789'), requestCaptured);
  assert.equal(requests[3].url, '/api/v1/calendar/ai-placeholders/ph-789/dismiss');
  assert.equal(requests[3].init?.method, 'POST');

  // 3. Verify WorkbenchPage.tsx integrates AI Planning UI
  const workbenchSource = readFileSync(
    new URL('../../src/client-web/src/pages/WorkbenchPage.tsx', import.meta.url),
    'utf8',
  );
  assert.match(workbenchSource, /AI 智能排程建议/);
  assert.match(workbenchSource, /一键生成排程建议/);
  assert.match(workbenchSource, /采纳排程/);
  assert.match(workbenchSource, /智能排程建议/);
  assert.match(workbenchSource, /getAiPlaceholders/);
  assert.match(workbenchSource, /confirmAiPlaceholder/);
  assert.match(workbenchSource, /dismissAiPlaceholder/);

  console.log('scheduleWorkbenchAiPlanning.test.ts passed successfully!');
}

runTests();
