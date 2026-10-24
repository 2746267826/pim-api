/* eslint-disable @typescript-eslint/no-explicit-any, @typescript-eslint/no-require-imports */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

// ── helpers ────────────────────────────────────────────────────────
const _tests: { name: string; fn: () => void | Promise<void> }[] = [];

function test(name: string, fn: () => void | Promise<void>) {
  _tests.push({ name, fn });
}

async function _runAll(): Promise<void> {
  let exitCode = 0;
  for (const { name, fn } of _tests) {
    const restoreGlobals: Array<() => void> = [];
    for (const key of ['window', 'fetch', 'localStorage'] as const) {
      const desc = Object.getOwnPropertyDescriptor(globalThis, key);
      if (desc) {
        restoreGlobals.push(() => Object.defineProperty(globalThis, key, desc));
      } else {
        restoreGlobals.push(() => { delete (globalThis as Record<string, unknown>)[key]; });
      }
    }
    try {
      await fn();
      console.error(`PASS: ${name}`);
    } catch (err) {
      console.error(`FAIL: ${name}`);
      console.error(err);
      exitCode = 1;
    } finally {
      for (const restore of restoreGlobals) restore();
    }
  }
  process.exit(exitCode);
}

// ────────────────────────────────────────────────────────────────────
//  Import production pure functions from state module
// ────────────────────────────────────────────────────────────────────
import {
  hasRealData,
  latestGeneratedAt,
  buildPageReport,
  formatNativeBoolean,
  formatNativeField,
  staleStatusLabel,
  nativeErrorMessage,
  generatedAtEntries,
  shouldShowSummaryMetricsFallback,
  NATIVE_STATE_REFRESH_INTERVAL_MS,
} from '../../src/client-web/src/pages/androidTodayEmbedState';
import type { PageReportInput } from '../../src/client-web/src/pages/androidTodayEmbedState';

// ────────────────────────────────────────────────────────────────────
//  Constants
// ────────────────────────────────────────────────────────────────────

test('NATIVE_STATE_REFRESH_INTERVAL_MS equals 30000', () => {
  assert.equal(NATIVE_STATE_REFRESH_INTERVAL_MS, 30_000);
});

test('page uses NATIVE_STATE_REFRESH_INTERVAL_MS as refetchInterval for native state query', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('refetchInterval'),
    'page must use refetchInterval');
  assert.ok(pageSource.includes('NATIVE_STATE_REFRESH_INTERVAL_MS'),
    'page must reference NATIVE_STATE_REFRESH_INTERVAL_MS');
  assert.ok(pageSource.includes('native-state') || pageSource.includes('nativeState'),
    'page must have React Query for native state');
});

// ────────────────────────────────────────────────────────────────────
//  hasRealData – real/empty data determination
// ────────────────────────────────────────────────────────────────────

test('hasRealData true when pointCount > 0', () => {
  assert.equal(hasRealData({ pointCount: 5 } as any, [], {} as any, {} as any), true);
});

test('hasRealData true when tracks have content', () => {
  assert.equal(hasRealData({} as any, [{ id: 't1' }] as any, {} as any, {} as any), true);
});

test('hasRealData true when totalForegroundSeconds > 0', () => {
  assert.equal(hasRealData({} as any, [] as any, { totalForegroundSeconds: 100 } as any, {} as any), true);
});

test('hasRealData true when appRanking has entries', () => {
  assert.equal(hasRealData({} as any, [] as any, {} as any, { appRanking: [{ packageName: 'x' }] } as any), true);
});

test('hasRealData false when all fields empty', () => {
  assert.equal(hasRealData(null, null, null, null), false);
  assert.equal(hasRealData({ pointCount: 0 } as any, [], { totalForegroundSeconds: 0 } as any, { appRanking: [] } as any), false);
});

test('hasRealData treats undefined as empty', () => {
  assert.equal(hasRealData(undefined, undefined, undefined, undefined), false);
});

test('hasRealData true when summary.totalForegroundSeconds > 0 but no appRanking', () => {
  assert.equal(hasRealData({} as any, [] as any, {} as any, { totalForegroundSeconds: 100, appRanking: [] } as any), true);
});

// ────────────────────────────────────────────────────────────────────
//  shouldShowSummaryMetricsFallback – usage overview missing, summary total exists
// ────────────────────────────────────────────────────────────────────

test('shouldShowSummaryMetricsFallback true when usageOverview has zero duration and summary has positive duration', () => {
  const result = shouldShowSummaryMetricsFallback(
    { totalForegroundSeconds: 100, fallbackForegroundSeconds: 50 } as any,
    { totalForegroundSeconds: 0 } as any,
  );
  assert.equal(result, true);
});

test('shouldShowSummaryMetricsFallback false when usageOverview already has positive duration', () => {
  const result = shouldShowSummaryMetricsFallback(
    { totalForegroundSeconds: 200 } as any,
    { totalForegroundSeconds: 100 } as any,
  );
  assert.equal(result, false);
});

test('shouldShowSummaryMetricsFallback false when summary duration is zero', () => {
  const result = shouldShowSummaryMetricsFallback(
    { totalForegroundSeconds: 0 } as any,
    { totalForegroundSeconds: 0 } as any,
  );
  assert.equal(result, false);
});

test('shouldShowSummaryMetricsFallback false when usageOverview and summary are null', () => {
  assert.equal(shouldShowSummaryMetricsFallback(null, null), false);
});

// ────────────────────────────────────────────────────────────────────
//  latestGeneratedAt
// ────────────────────────────────────────────────────────────────────

test('latestGeneratedAt null for empty input', () => {
  assert.equal(latestGeneratedAt([]), null);
  assert.equal(latestGeneratedAt([null, undefined, {}]), null);
});

test('latestGeneratedAt picks the latest time', () => {
  const result = latestGeneratedAt([
    { generatedAt: '2026-07-17T01:00:00Z' },
    { generatedAt: '2026-07-17T02:00:00Z' },
    { generatedAt: '2026-07-17T00:00:00Z' },
  ]);
  assert.equal(result, '2026-07-17T02:00:00.000Z');
});

test('latestGeneratedAt ignores items without generatedAt', () => {
  const result = latestGeneratedAt([
    null,
    { generatedAt: '2026-07-17T01:00:00Z' },
    {} as any,
    undefined,
  ]);
  assert.equal(result, '2026-07-17T01:00:00.000Z');
});

// ────────────────────────────────────────────────────────────────────
//  generatedAtEntries
// ────────────────────────────────────────────────────────────────────

test('generatedAtEntries returns entries for each query that has generatedAt', () => {
  const entries = generatedAtEntries(
    { generatedAt: '2026-07-17T01:00:00Z', pointCount: 5 } as any,
    { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 3600, isStale: false } as any,
    { generatedAt: '2026-07-17T00:00:00Z', appRanking: [] } as any,
  );
  assert.equal(entries.length, 3);
  assert.ok(entries[0].label.includes('位置概况'));
  assert.ok(entries[1].label.includes('手机使用'));
  assert.ok(entries[2].label.includes('App 摘要'));
  assert.equal(entries[1].generatedAt, '2026-07-17T02:00:00Z');
});

test('generatedAtEntries null for missing generatedAt', () => {
  const entries = generatedAtEntries(
    null,
    { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 100, isStale: false } as any,
    null,
  );
  assert.equal(entries.length, 1);
  assert.ok(entries[0].label.includes('手机使用'));
});

test('generatedAtEntries no longer appends stale text to label', () => {
  const entries = generatedAtEntries(
    null,
    { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 100, isStale: true } as any,
    null,
  );
  assert.equal(entries.length, 1);
  assert.equal(entries[0].label, '手机使用');
});

test('generatedAtEntries does NOT mark stale for location or summary', () => {
  const entries = generatedAtEntries(
    { generatedAt: '2026-07-17T01:00:00Z', pointCount: 5 } as any,
    null,
    { generatedAt: '2026-07-17T00:00:00Z', appRanking: [] } as any,
  );
  const labels = entries.map(e => e.label).join(' ');
  // neither label should contain "过期"
  assert.ok(!labels.includes('过期'));
});

// ────────────────────────────────────────────────────────────────────
//  buildPageReport
// ────────────────────────────────────────────────────────────────────

test('buildPageReport hasServerData=true no error when all data present', () => {
  const input: PageReportInput = {
    locationOverview: { generatedAt: '2026-07-17T01:00:00Z', pointCount: 10 } as any,
    tracks: [{ id: 't1' }] as any,
    usageOverview: { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 3600 } as any,
    summary: { generatedAt: '2026-07-17T00:00:00Z', appRanking: [{ packageName: 'com.t' }] } as any,
  };
  const r = buildPageReport(input);
  assert.equal(r.hasServerData, true);
  assert.equal(r.generatedAt, '2026-07-17T02:00:00.000Z');
  assert.equal(r.error, null);
});

test('buildPageReport hasServerData=false when no real content', () => {
  const input: PageReportInput = {
    locationOverview: { generatedAt: '2026-07-17T01:00:00Z', pointCount: 0 } as any,
    tracks: [],
    usageOverview: { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 0 } as any,
    summary: { generatedAt: '2026-07-17T00:00:00Z', appRanking: [] } as any,
  };
  const r = buildPageReport(input);
  assert.equal(r.hasServerData, false);
});

test('buildPageReport Chinese error on partial failures', () => {
  const input: PageReportInput = {
    locationError: new Error('timeout'),
    tracksError: new Error('network'),
    usageError: new Error('server error'),
  };
  const r = buildPageReport(input);
  assert.equal(r.hasServerData, false);
  assert.ok(r.error!.includes('位置数据获取失败'), `should mention 位置, got: ${r.error}`);
  assert.ok(r.error!.includes('使用数据获取失败'), `should mention 使用, got: ${r.error}`);
  assert.ok(r.error!.includes('轨迹数据获取失败'), `should mention 轨迹, got: ${r.error}`);
});

test('buildPageReport error null when no failures', () => {
  const input: PageReportInput = {
    locationOverview: { generatedAt: '2026-07-17T01:00:00Z', pointCount: 0 } as any,
    tracks: [],
    usageOverview: { generatedAt: '2026-07-17T02:00:00Z', totalForegroundSeconds: 0 } as any,
    summary: { generatedAt: '2026-07-17T00:00:00Z', appRanking: [] } as any,
  };
  const r = buildPageReport(input);
  assert.equal(r.error, null);
});

test('buildPageReport generatedAt null when all queries fail', () => {
  const input: PageReportInput = {
    locationError: new Error('x'),
    tracksError: new Error('x'),
    usageError: new Error('x'),
    summaryError: new Error('x'),
  };
  const r = buildPageReport(input);
  assert.equal(r.generatedAt, null);
});

test('buildPageReport picks latest generatedAt across sources', () => {
  const input: PageReportInput = {
    locationOverview: { generatedAt: '2026-07-17T01:00:00Z', pointCount: 5 } as any,
    tracks: [],
    usageOverview: { generatedAt: '2026-07-17T05:00:00Z', totalForegroundSeconds: 0 } as any,
    summary: { generatedAt: '2026-07-16T23:00:00Z', appRanking: [] } as any,
  };
  const r = buildPageReport(input);
  assert.equal(r.generatedAt, '2026-07-17T05:00:00.000Z');
});

// ────────────────────────────────────────────────────────────────────
//  formatNativeBoolean / formatNativeField
// ────────────────────────────────────────────────────────────────────

test('formatNativeBoolean correct text', () => {
  assert.equal(formatNativeBoolean(true), '已开启');
  assert.equal(formatNativeBoolean(false), '已关闭');
  assert.equal(formatNativeBoolean(null), '暂无');
  assert.equal(formatNativeBoolean(undefined), '暂无');
});

test('formatNativeField shows value or 暂无', () => {
  assert.equal(formatNativeField('GPS'), 'GPS');
  assert.equal(formatNativeField(null), '暂无');
  assert.equal(formatNativeField(undefined), '暂无');
  assert.equal(formatNativeField(''), '暂无');
});

// ────────────────────────────────────────────────────────────────────
//  staleStatusLabel – 根据 isStale 返回中文标签
// ────────────────────────────────────────────────────────────────────

test('staleStatusLabel true returns 可能过期', () => {
  assert.equal(staleStatusLabel(true), '可能过期');
});

test('staleStatusLabel false returns null', () => {
  assert.equal(staleStatusLabel(false), null);
});

test('staleStatusLabel undefined returns null', () => {
  assert.equal(staleStatusLabel(undefined), null);
});

// ────────────────────────────────────────────────────────────────────
//  nativeErrorMessage – 接受 unknown 始终返回固定中文
// ────────────────────────────────────────────────────────────────────

test('nativeErrorMessage accepts unknown and returns Chinese', () => {
  assert.equal(nativeErrorMessage('bridge_unavailable'), '无法读取原生采集状态');
  assert.equal(nativeErrorMessage(null), '无法读取原生采集状态');
  assert.equal(nativeErrorMessage(undefined), '无法读取原生采集状态');
  assert.equal(nativeErrorMessage(new Error('timeout')), '无法读取原生采集状态');
});

// ────────────────────────────────────────────────────────────────────
//  App.tsx – embed route must NOT reference TodayPage directly
// ────────────────────────────────────────────────────────────────────

test('/embed/android/today route no longer references TodayPage', () => {
  const appSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/App.tsx'),
    'utf8',
  );
  const lines = appSource.split('\n');
  const embedTodayLine = lines.find(line =>
    line.includes('/embed/android/today') && line.includes('<'));
  assert.ok(embedTodayLine, 'embed today route must exist');
  assert.ok(!embedTodayLine!.includes('TodayPage'),
    `embed route should not reference TodayPage, got: ${embedTodayLine}`);
});

// ────────────────────────────────────────────────────────────────────
//  AndroidTodayEmbedPage only exports the component (no warning fix)
// ────────────────────────────────────────────────────────────────────

// ────────────────────────────────────────────────────────────────────
//  Task 2: buildPageReport all four errors use Chinese separator
// ────────────────────────────────────────────────────────────────────

test('buildPageReport all four errors produce Chinese text separated by Chinese semicolon', () => {
  const input: PageReportInput = {
    locationError: new Error('timeout'),
    tracksError: new Error('network error'),
    usageError: new Error('server error'),
    summaryError: new Error('parse error'),
  };
  const r = buildPageReport(input);
  assert.equal(r.hasServerData, false);
  assert.equal(r.generatedAt, null);
  assert.ok(r.error!.includes('位置数据获取失败'));
  assert.ok(r.error!.includes('轨迹数据获取失败'));
  assert.ok(r.error!.includes('使用数据获取失败'));
  assert.ok(r.error!.includes('摘要数据获取失败'));
  assert.ok(r.error!.includes('；'));
});

// ────────────────────────────────────────────────────────────────────
//  Task 2: source check – page no longer extracts raw Error.message
// ────────────────────────────────────────────────────────────────────

test('page no longer maps query errors with .message for display', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(!pageSource.includes('.error as Error)?.message'),
    'page must not extract raw Error.message for display');
  assert.ok(pageSource.includes('buildPageReport('),
    'page should use buildPageReport for error display');
});

// ────────────────────────────────────────────────────────────────────
//  Task 5.2: source check – page no longer shows （最新
// ────────────────────────────────────────────────────────────────────

test('page no longer shows latestGeneratedAt in UI', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(!pageSource.includes('（最新 '),
    'page should not display latestGeneratedAt in UI');
});

// ────────────────────────────────────────────────────────────────────
//  Task 5.5: source check – stable key for genEntries
// ────────────────────────────────────────────────────────────────────

test('generatedAtEntries uses stable label as key', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('key={entry.label}'),
    'genEntries should use stable label as key');
});

test('AndroidTodayEmbedPage only exports default component', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  // Should not have named exports of helpers (those moved to state module)
  const namedExportCount = (pageSource.match(/^export (function|const|interface|type) /gm) || []).length;
  assert.equal(namedExportCount, 0,
    `expected 0 named exports (only default), got ${namedExportCount}`);
  assert.ok(pageSource.includes('export default function AndroidTodayEmbedPage'));
});

// ────────────────────────────────────────────────────────────────────
//  Issue 3: page has timer-based date refresh
// ────────────────────────────────────────────────────────────────────

test('page imports useState and has date refresh key', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  const reactImport = pageSource.split('\n').find(l => l.includes("from 'react'"));
  assert.ok(reactImport && reactImport.includes('useState'),
    'react import must include useState for date refresh');
  assert.ok(pageSource.includes('dateRefreshKey') || pageSource.includes('refreshKey'),
    'page must have a date refresh key');
});

test('utcRange and localDate depend on period timer refresh', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('dateRefreshKey'),
    'page must have dateRefreshKey for periodic refresh');
  assert.ok(pageSource.includes('setInterval'),
    'page must use setInterval to refresh date key');
  const utcUseMemo = pageSource.includes('utcRange') && pageSource.includes('useMemo');
  assert.ok(utcUseMemo, 'utcRange must use useMemo');
  const localUseMemo = pageSource.includes('localDate') && pageSource.includes('useMemo');
  assert.ok(localUseMemo, 'localDate must use useMemo');
});

// ────────────────────────────────────────────────────────────────────
//  Issue 4: semantic data gating for location/usage sections
// ────────────────────────────────────────────────────────────────────

test('location section gated on pointCount > 0', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('pointCount'),
    'location section must reference pointCount');
  assert.ok(pageSource.includes('LocationMetricStrip'),
    'LocationMetricStrip must be rendered');
  // The JSX usage of LocationMetricStrip is after the return, not the import
  const jsxStart = pageSource.indexOf('return');
  const afterReturn = pageSource.slice(jsxStart);
  assert.ok(afterReturn.includes('pointCount'),
    'JSX must conditionally render based on pointCount');
  // pointCount must appear before LocationMetricStrip in JSX
  assert.ok(afterReturn.indexOf('pointCount') < afterReturn.indexOf('LocationMetricStrip'),
    'pointCount condition must come before LocationMetricStrip in JSX');
});

test('usage section gated on totalForegroundSeconds > 0', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('totalForegroundSeconds'),
    'usage section must reference totalForegroundSeconds');
  assert.ok(pageSource.includes('MobileInsightStrip'),
    'MobileInsightStrip must be rendered');
  const jsxStart = pageSource.indexOf('return');
  const afterReturn = pageSource.slice(jsxStart);
  assert.ok(afterReturn.includes('totalForegroundSeconds'),
    'JSX must conditionally render based on totalForegroundSeconds');
  assert.ok(afterReturn.indexOf('totalForegroundSeconds') < afterReturn.indexOf('MobileInsightStrip'),
    'totalForegroundSeconds condition must come before MobileInsightStrip in JSX');
});

// ────────────────────────────────────────────────────────────────────
//  Issue 5: prevReportRef stored only after sendPageReport succeeds
// ────────────────────────────────────────────────────────────────────

test('prevReportRef.current is set only after successful sendPageReport', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  const sendIdx = pageSource.indexOf('sendPageReport(report)');
  const assignIdx = pageSource.indexOf('prevReportRef.current =');
  assert.ok(sendIdx >= 0, 'sendPageReport call must exist');
  assert.ok(assignIdx >= 0, 'prevReportRef.current assignment must exist');
  assert.ok(assignIdx > sendIdx,
    'prevReportRef.current assignment must appear textually after sendPageReport');
  // The old guard check (prevReportRef.current without =) is still there but NOT an assignment
  assert.ok(pageSource.includes('prevReportRef.current'),
    'guard comparison with prevReportRef.current must exist');
});

// ────────────────────────────────────────────────────────────────────
//  Gap 3: report retry on transient failure
// ────────────────────────────────────────────────────────────────────

test('page has report retry with 30s timeout on failure', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );

  // Must have a state for retry
  const hasRetryState = pageSource.includes('reportRetryKey') || pageSource.includes('retryKey');
  assert.ok(hasRetryState, 'page must have a state key for report retry');

  // Must have useState for retry state
  assert.ok(pageSource.includes('setReportRetryKey') || pageSource.includes('setRetryKey'),
    'page must have setter for retry state');

  // Must schedule retry with setTimeout on failure
  assert.ok(pageSource.includes('setTimeout'),
    'page must use setTimeout for report retry');

  // Must use 30000ms timeout for retry
  const has30s = pageSource.includes('30000') || pageSource.includes('30_000') || pageSource.includes('30 * 1000');
  assert.ok(has30s, 'retry timeout should be ~30 seconds');

  // retry state must be an effect dependency
  const effectLines = pageSource.split('\n').filter(l =>
    l.includes(']') && (l.includes('report') || l.includes('retry')));
  const depsLine = effectLines.find(l => l.includes('reportRetryKey'));
  assert.ok(depsLine, 'retry state must appear in effect dependency array');
});

test('page report effect ignores async completion after cleanup', () => {
  let pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/AndroidTodayEmbedPage.tsx'),
    'utf8',
  );
  pageSource = pageSource.replace(/\r\n/g, '\n');

  assert.ok(pageSource.includes('let cancelled = false'),
    'effect must track whether its async delivery was cancelled');
  assert.ok(pageSource.includes('if (!cancelled) {\n            prevReportRef.current = key;'),
    'successful delivery must not update the dedupe key after cleanup');
  assert.ok(pageSource.includes('if (!cancelled && !retryTimerRef.current)'),
    'failed delivery must not schedule a retry after cleanup');
  assert.ok(pageSource.includes('cancelled = true;'),
    'effect cleanup must mark the in-flight delivery as cancelled');
});

void _runAll();
