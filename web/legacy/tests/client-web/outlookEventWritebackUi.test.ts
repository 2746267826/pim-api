import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const editorSource = readFileSync(
  new URL('../../src/client-web/src/dialogs/EventEditorDialog.tsx', import.meta.url),
  'utf8',
);

const typesSource = readFileSync(
  new URL('../../src/client-web/src/types/index.ts', import.meta.url),
  'utf8',
);

const calendarApiSource = readFileSync(
  new URL('../../src/client-web/src/api/calendar.ts', import.meta.url),
  'utf8',
);

const clientSource = readFileSync(
  new URL('../../src/client-web/src/api/client.ts', import.meta.url),
  'utf8',
);

const beforeAfterDiffSource = readFileSync(
  new URL('../../src/client-web/src/components/schedule/BeforeAfterDiff.tsx', import.meta.url),
  'utf8',
);

const failures: unknown[] = [];

// --- CalendarResponse must have optional outlook fields and canEdit ---
{
  const ifaceMatch = typesSource.match(/interface CalendarResponse\s*\{[^}]+\}/);
  assert.ok(ifaceMatch, 'CalendarResponse interface must exist');
  const iface = ifaceMatch[0];
  if (!iface.includes('eventCount?:')) failures.push('CalendarResponse missing eventCount');
  if (!iface.includes('source?:')) failures.push('CalendarResponse missing source');
  if (!iface.includes('outlookCalendarBindingId?:')) failures.push('CalendarResponse missing outlookCalendarBindingId');
  if (!iface.includes('canEdit?:')) failures.push('CalendarResponse missing canEdit');
}

// --- EventResponse must have optional outlook fields ---
{
  const ifaceMatch = typesSource.match(/interface EventResponse\s*\{[^}]+\}/);
  assert.ok(ifaceMatch, 'EventResponse interface must exist');
  const iface = ifaceMatch[0];
  if (!iface.includes('outlookCalendarBindingId?:')) failures.push('EventResponse missing outlookCalendarBindingId');
  if (!iface.includes('outlookEventId?:')) failures.push('EventResponse missing outlookEventId');
  if (!iface.includes('outlookEtag?:')) failures.push('EventResponse missing outlookEtag');
  if (!iface.includes('outlookEventType?:')) failures.push('EventResponse missing outlookEventType');
}

// --- OutlookWriteRequest.operation narrowed to union, scope narrowed ---
{
  const ifaceMatch = typesSource.match(/interface OutlookWriteRequest\s*\{[^}]+\}/);
  assert.ok(ifaceMatch, 'OutlookWriteRequest interface must exist');
  const iface = ifaceMatch[0];
  if (!iface.includes("'create' | 'update' | 'delete'")) failures.push('OutlookWriteRequest.operation must be union');
  if (!iface.includes("'instance' | 'series'")) failures.push('OutlookWriteRequest.scope must be instance|series');
  if (!iface.includes('clientOperationId:')) failures.push('OutlookWriteRequest must have clientOperationId');
}

// --- OutlookWriteResult must have typed latestEvent/latestEtag; latestOutlookJson is a deprecated compat field ---
{
  const ifaceMatch = typesSource.match(/interface OutlookWriteResult\s*\{[^}]+\}/);
  assert.ok(ifaceMatch, 'OutlookWriteResult interface must exist');
  const iface = ifaceMatch[0];
  if (!iface.includes('latestOutlookJson?:')) failures.push('OutlookWriteResult keeps deprecated latestOutlookJson');
  if (!iface.includes('latestEvent?:')) failures.push('OutlookWriteResult missing latestEvent');
  if (!iface.includes('latestEtag?:')) failures.push('OutlookWriteResult missing latestEtag');
}

// --- Required UI strings in EventEditorDialog ---
if (!editorSource.includes('最新 Outlook 内容')) failures.push('Editor must show 最新 Outlook 内容');
if (!editorSource.includes('conflict')) failures.push('Editor must handle conflict status');
if (!editorSource.includes('实例')) failures.push('Editor must show 实例 scope option');
if (!editorSource.includes('系列')) failures.push('Editor must show 系列 scope option');

// --- Task 8: typed field-level writeback confirmation, no raw JSON <pre> ---
{
  if (!editorSource.includes("from '../utils/eventFieldDiff'")) {
    failures.push('Editor must build typed field-level diffs');
  }
  if (!editorSource.includes('toDiffRecord')) failures.push('Editor must convert events to typed diff records');
  if (!editorSource.includes('formatFieldValue')) failures.push('Editor must format typed values safely');
  if (!editorSource.includes('summarizeEventFields')) failures.push('Editor must summarize the typed latest event');
  if (!editorSource.includes('<BeforeAfterDiff')) failures.push('Editor must keep BeforeAfterDiff');
  if (!editorSource.includes('meta={')) failures.push('Writeback dialog must pass operation/account/calendar/scope meta');
  if (!editorSource.includes('变更范围')) failures.push('Writeback dialog must show the scope');
  if (!editorSource.includes("type: 'conflict'; latestEvent")) {
    failures.push('Conflict phase must carry the typed latestEvent, not raw JSON');
  }
  if (editorSource.includes('<pre')) failures.push('Writeback UI must not render raw JSON <pre>');
  if (editorSource.includes('buildDraftJson')) failures.push('Raw draft JSON builder must be removed');
  if (editorSource.includes('buildEventJson')) failures.push('Raw event JSON builder must be removed');
  if (editorSource.includes('latestOutlookJson')) failures.push('Editor must not consume the deprecated latestOutlookJson');
  if (editorSource.includes('JSON.stringify')) failures.push('Editor must never build diff payloads via JSON.stringify');
}

// --- BeforeAfterDiff must stay a typed field-level diff, never raw JSON ---
{
  if (!beforeAfterDiffSource.includes('export default function BeforeAfterDiff')) {
    failures.push('BeforeAfterDiff default export must be preserved');
  }
  if (!beforeAfterDiffSource.includes('diffs?:') || !beforeAfterDiffSource.includes('before?:')) {
    failures.push('BeforeAfterDiff props must accept typed before/after or EventFieldDiff[]');
  }
  if (!beforeAfterDiffSource.includes('meta?')) failures.push('BeforeAfterDiff must render meta (operation/account/calendar/scope)');
  for (const label of ['操作', '账户', '日历', '范围']) {
    if (!beforeAfterDiffSource.includes(label)) failures.push(`BeforeAfterDiff meta must show ${label}`);
  }
  if (beforeAfterDiffSource.includes('<pre')) failures.push('BeforeAfterDiff must not render raw JSON <pre>');
  if (beforeAfterDiffSource.includes('JSON.stringify')) failures.push('BeforeAfterDiff must not build raw JSON');
}

// --- Every manual and Outlook mutation must invalidate both calendar layer caches ---
{
  const invalidationCalls = editorSource.match(/invalidateEventQueries\(\);/g) ?? [];
  if (invalidationCalls.length < 4) {
    failures.push('Create, update, delete, and Outlook writeback must share event query invalidation');
  }
  if (!editorSource.includes("queryKey: ['calendar-layers']")) {
    failures.push('Event query invalidation must include calendar-layers');
  }
  if (!editorSource.includes("queryKey: ['workbench-calendar-layers']")) {
    failures.push('Event query invalidation must include workbench-calendar-layers');
  }
}

// --- Forbidden strings ---
if (editorSource.includes('强制覆盖')) failures.push('Editor must NOT contain 强制覆盖');
if (editorSource.includes('复制为 PIM 日程')) failures.push('Editor must NOT contain 复制为 PIM 日程');

// --- Manual CRUD functions still exist ---
if (!/\bexport async function createEvent\b/.test(calendarApiSource)) failures.push('createEvent export missing');
if (!/\bexport async function updateEvent\b/.test(calendarApiSource)) failures.push('updateEvent export missing');
if (!/\bexport async function deleteEvent\b/.test(calendarApiSource)) failures.push('deleteEvent export missing');

// --- writeOutlookEvent exists and is exported ---
if (!/\bexport async function writeOutlookEvent\b/.test(calendarApiSource)) failures.push('writeOutlookEvent export missing');

// --- client.ts must have acceptedStatuses mechanism ---
if (!clientSource.includes('acceptedStatuses')) failures.push('acceptedStatuses mechanism missing from client.ts');
// --- calendar.ts must import authedFetch ---
if (!calendarApiSource.includes('authedFetch')) failures.push('calendar.ts must import authedFetch');

// --- Outlook writeback route exists ---
if (!calendarApiSource.includes("'/calendar/outlook/events/writeback'")) failures.push('Writeback route missing');

// --- create scope must be fixed instance ---
if (!editorSource.includes('crypto.randomUUID')) failures.push('Editor must generate clientOperationId via crypto.randomUUID');

// --- TDD: stale scope race must not exist ---
{
  const staleMatch = editorSource.match(/setOutlookScope\(scopeDefault\);\s+openWritebackPreview\(/);
  if (staleMatch) failures.push('setOutlookScope before openWritebackPreview must not exist (stale scope race)');
}

// --- TDD: missing ETag guard must exist ---
{
  if (!editorSource.includes('缺少版本标识')) failures.push('Missing Chinese error message for missing ETag');
  if (!editorSource.includes('outlookEtag')) failures.push('Missing ETag guard in editor');
}

// --- TDD: manual CRUD (POST/PUT/DELETE to /calendar/events) never uses writeback ---
{
  if (editorSource.includes("'/calendar/outlook/events/writeback'")) failures.push('EventEditorDialog must NOT import writeback route');
  if (!calendarApiSource.includes("'/calendar/outlook/events/writeback'")) failures.push('Writeback route must be in calendar.ts');
}

// --- TDD: non-recurring Outlook events must NOT show scope radio ---
{
  if (editorSource.includes("showScopeRadio = !!event && !!event.outlookEventType")) failures.push('showScopeRadio must filter by recurring types only');
}

// --- TDD: read-only check must cover selectedCalendar for new events ---
{
  if (editorSource.includes("const isReadOnly = eventCalendar?.canEdit === false")) failures.push('isReadOnly must check selectedCalendar for new events');
}

async function main() {
  // --- API: writeOutlookEvent must handle 409 and return typed conflict body ---
  const conflictBody = {
    code: 0,
    message: 'Conflict',
    data: {
    status: 'conflict',
      latestEvent: {
        id: 'pim-evt-1',
        calendarId: 'cal-1',
        uid: 'uid-1',
        title: 'Updated from Outlook',
        dtStart: '2026-07-14T09:00:00',
        dtEnd: '2026-07-14T10:00:00',
        status: 'confirmed',
        source: 'outlook',
        outlookEventId: 'graph-evt-1',
      },
      latestEtag: 'new-etag-456',
    },
    timestamp: new Date().toISOString(),
  };

  let capturedUrl: string | undefined;
  let capturedInit: RequestInit | undefined;

  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input);
    capturedInit = init;
    return new Response(JSON.stringify(conflictBody), {
      status: 409,
      headers: { 'Content-Type': 'application/json' },
    });
  };

  const { writeOutlookEvent } = await import('../../src/client-web/src/api/calendar');
  let result: import('../../src/client-web/src/types').OutlookWriteResult;
  try {
    result = await writeOutlookEvent({
      operation: 'update',
      calendarBindingId: 'binding-1',
      eventId: 'pim-evt-1',
      draft: {
        calendarId: 'cal-1',
        title: 'Updated Event',
        dtStart: '2026-07-14T09:00:00Z',
        dtEnd: '2026-07-14T10:00:00Z',
      },
      scope: 'instance',
      clientOperationId: 'test-op-001',
      expectedEtag: 'old-etag-123',
    });

    if (result.status !== 'conflict') failures.push('writeOutlookEvent should return conflict status on 409');
    if (result.latestEtag !== 'new-etag-456') failures.push('writeOutlookEvent should return latestEtag');
    if (result.latestOutlookJson) failures.push('writeOutlookEvent must not expose raw latestOutlookJson');
    if (!result.latestEvent) failures.push('writeOutlookEvent should return typed latestEvent');
    if (result.latestEvent?.title !== 'Updated from Outlook') {
      failures.push('latestEvent must carry the typed latest event fields');
    }
    if (!capturedUrl?.includes('/api/v1/calendar/outlook/events/writeback')) failures.push('Wrong URL');
    if (capturedInit?.method !== 'POST') failures.push('Must be POST');
  } catch {
    failures.push('writeOutlookEvent must NOT throw on HTTP 409');
  } finally {
    globalThis.fetch = originalFetch;
  }

  // --- API: writeOutlookEvent must accept HTTP 412 as a typed conflict too ---
  {
    const typedConflict = (status: number) => async (input: RequestInfo | URL, init?: RequestInit) => {
      return new Response(JSON.stringify(conflictBody), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });
    };

    const { writeOutlookEvent: writeOutlookEvent412 } = await import('../../src/client-web/src/api/calendar');
    for (const status of [409, 412]) {
      globalThis.fetch = typedConflict(status);
      try {
        const result = await writeOutlookEvent412({
          operation: 'update',
          calendarBindingId: 'binding-1',
          eventId: 'pim-evt-1',
          draft: {
            calendarId: 'cal-1',
            title: 'Updated Event',
            dtStart: '2026-07-14T09:00:00Z',
            dtEnd: '2026-07-14T10:00:00Z',
          },
          scope: 'instance',
          clientOperationId: 'test-op-001',
          expectedEtag: 'old-etag-123',
        });
        if (result.status !== 'conflict') failures.push(`writeOutlookEvent should return conflict status on HTTP ${status}`);
        if (result.latestEtag !== 'new-etag-456') failures.push(`writeOutlookEvent should return latestEtag on HTTP ${status}`);
        if (!result.latestEvent) failures.push(`writeOutlookEvent should return typed latestEvent on HTTP ${status}`);
        if (result.latestEvent?.title !== 'Updated from Outlook') {
          failures.push(`latestEvent must carry the typed latest event fields on HTTP ${status}`);
        }
      } catch {
        failures.push(`writeOutlookEvent must NOT throw on HTTP ${status}`);
      }
    }
  }

  // --- Manual CRUD must never call writeback endpoint ---
  {
    const manualRequests: Array<{ url: string; method?: string }> = [];
    const manualFetch = globalThis.fetch;
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
      manualRequests.push({ url: String(input), method: init?.method });
      return new Response(JSON.stringify({ code: 0, message: 'OK', data: { id: 'test-evt-id' } }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    };
    try {
      const { createEvent, updateEvent, deleteEvent } = await import('../../src/client-web/src/api/calendar');
      await createEvent({ title: 'Test', calendarId: 'cal-1', dtStart: '2026-07-14T09:00:00', dtEnd: '2026-07-14T10:00:00' });
      if (manualRequests[0]?.url.includes('outlook')) failures.push('createEvent must not use writeback endpoint');
      if (manualRequests[0]?.method !== 'POST') failures.push('createEvent must use POST');

      await updateEvent('evt-1', { title: 'Updated' });
      if (manualRequests[1]?.url.includes('outlook')) failures.push('updateEvent must not use writeback endpoint');
      if (manualRequests[1]?.method !== 'PUT') failures.push('updateEvent must use PUT');

      await deleteEvent('evt-1');
      if (manualRequests[2]?.url.includes('outlook')) failures.push('deleteEvent must not use writeback endpoint');
      if (manualRequests[2]?.method !== 'DELETE') failures.push('deleteEvent must use DELETE');
    } finally {
      globalThis.fetch = manualFetch;
    }
  }

  if (failures.length > 0) {
    throw new AggregateError(failures, 'outlookEventWritebackUi contract tests failed');
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
