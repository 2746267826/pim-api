/* eslint-disable @typescript-eslint/no-explicit-any, @typescript-eslint/no-require-imports */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const _tests: { name: string; fn: () => void | Promise<void> }[] = [];

function test(name: string, fn: () => void | Promise<void>) {
  _tests.push({ name, fn });
}

async function _runAll(): Promise<void> {
  let exitCode = 0;
  for (const { name, fn } of _tests) {
    try {
      await fn();
      console.error(`PASS: ${name}`);
    } catch (err) {
      console.error(`FAIL: ${name}`);
      console.error(err);
      exitCode = 1;
    }
  }
  process.exit(exitCode);
}

// ────────────────────────────────────────────────────────────────────
//  1. App.tsx – tracks route uses HistoricalLocationPage (no placeholder)
// ────────────────────────────────────────────────────────────────────

test('tracks embed route uses HistoricalLocationPage instead of placeholder', () => {
  const appSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/App.tsx'),
    'utf8',
  );
  assert.ok(!appSource.includes('轨迹页面'),
    'App.tsx must not contain placeholder text 轨迹页面');
  assert.ok(appSource.includes('HistoricalLocationPage'),
    'App.tsx must import HistoricalLocationPage');
  const embedLine = appSource.split('\n').find(l =>
    l.includes('/embed/android/tracks') && l.includes('<'));
  assert.ok(embedLine && embedLine.includes('HistoricalLocationPage'),
    'embed tracks route must render HistoricalLocationPage');
});

test('tracks embed route passes embedded prop to HistoricalLocationPage', () => {
  const appSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/App.tsx'),
    'utf8',
  );
  const embedLine = appSource.split('\n').find(l =>
    l.includes('/embed/android/tracks') && l.includes('<'));
  assert.ok(embedLine && (embedLine.includes('embedded') || embedLine.includes('embedded={true}')),
    'embed tracks route must pass embedded prop');
});

test('desktop /location-history route does not pass embedded prop', () => {
  const layoutSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/layout/AppLayout.tsx'),
    'utf8',
  );
  const desktopLine = layoutSource.split('\n').find(line =>
    line.includes('/location-history') && line.includes('<'));
  assert.ok(desktopLine, 'desktop location-history route must exist');
  assert.ok(desktopLine.includes('HistoricalLocationPage'),
    'desktop location-history route must render HistoricalLocationPage');
  assert.ok(!desktopLine.includes('embedded'),
    'desktop location-history route must not enable embedded layout');
});

// ────────────────────────────────────────────────────────────────────
//  2. URL filter parse/serialize
// ────────────────────────────────────────────────────────────────────

import {
  parseTracksUrlFilters,
  serializeTracksUrlFilters,
  tracksUrlFiltersToParams,
  canAdvanceRawPointPage,
  advanceRawPointCursorStack,
  type TracksUrlFilters,
} from '../../src/client-web/src/pages/historicalLocationQuery';

const SAMPLE_DATE = '2026-07-15';

test('parseTracksUrlFilters reads all params from URLSearchParams', () => {
  const sp = new URLSearchParams(
    'range=7d&start=2026-07-01&end=2026-07-08&device=pixel-8&accuracy=100&rejected=1'
  );
  const f = parseTracksUrlFilters(sp);
  assert.equal(f.range, '7d');
  assert.equal(f.startDate, '2026-07-01');
  assert.equal(f.endDate, '2026-07-08');
  assert.equal(f.deviceId, 'pixel-8');
  assert.equal(f.maxAccuracyMeters, 100);
  assert.equal(f.includeRejected, true);
});

test('parseTracksUrlFilters falls back to defaults for missing params', () => {
  const sp = new URLSearchParams('');
  const f = parseTracksUrlFilters(sp);
  assert.equal(f.range, '7d');
  assert.ok(f.startDate.length > 0, 'startDate must have default');
  assert.ok(f.endDate.length > 0, 'endDate must have default');
  assert.equal(f.deviceId, '');
  assert.equal(f.maxAccuracyMeters, 50);
  assert.equal(f.includeRejected, false);
});

test('parseTracksUrlFilters ignores invalid range value', () => {
  const sp = new URLSearchParams('range=invalid');
  const f = parseTracksUrlFilters(sp);
  assert.equal(f.range, '7d', 'invalid range falls back to 7d');
});

test('parseTracksUrlFilters includeRejected true only for exact rejected=1', () => {
  assert.equal(parseTracksUrlFilters(new URLSearchParams('rejected=1')).includeRejected, true);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('rejected=0')).includeRejected, false);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('rejected=')).includeRejected, false);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('rejected=false')).includeRejected, false);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('rejected=x')).includeRejected, false);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('')).includeRejected, false);
});

test('parseTracksUrlFilters parses accuracy number, falls back to 50 for invalid', () => {
  assert.equal(parseTracksUrlFilters(new URLSearchParams('accuracy=abc')).maxAccuracyMeters, 50);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('accuracy=0')).maxAccuracyMeters, 50);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('accuracy=-1')).maxAccuracyMeters, 50);
  assert.equal(parseTracksUrlFilters(new URLSearchParams('accuracy=200')).maxAccuracyMeters, 200);
});

test('serializeTracksUrlFilters writes only non-default params', () => {
  const f: TracksUrlFilters = {
    range: '7d',
    startDate: SAMPLE_DATE,
    endDate: SAMPLE_DATE,
    deviceId: '',
    maxAccuracyMeters: 50,
    includeRejected: false,
  };
  const sp = serializeTracksUrlFilters(f);
  assert.equal(sp.get('range'), '7d', 'range always included');
  assert.equal(sp.get('start'), SAMPLE_DATE);
  assert.equal(sp.get('end'), SAMPLE_DATE);
  assert.equal(sp.has('device'), false, 'empty device omitted');
  assert.equal(sp.has('accuracy'), false, 'default accuracy omitted');
  assert.equal(sp.has('rejected'), false, 'rejected=false omitted');
});

test('serializeTracksUrlFilters includes non-default device and accuracy', () => {
  const f: TracksUrlFilters = {
    range: '30d',
    startDate: SAMPLE_DATE,
    endDate: SAMPLE_DATE,
    deviceId: 'pixel-9',
    maxAccuracyMeters: 200,
    includeRejected: true,
  };
  const sp = serializeTracksUrlFilters(f);
  assert.equal(sp.get('device'), 'pixel-9');
  assert.equal(sp.get('accuracy'), '200');
  assert.equal(sp.get('rejected'), '1');
});

test('serializeTracksUrlFilters encodes rejected as 1 only when true', () => {
  const fTrue: TracksUrlFilters = { range: '7d', startDate: SAMPLE_DATE, endDate: SAMPLE_DATE, deviceId: '', maxAccuracyMeters: 50, includeRejected: true };
  assert.equal(serializeTracksUrlFilters(fTrue).get('rejected'), '1');
  const fFalse: TracksUrlFilters = { range: '7d', startDate: SAMPLE_DATE, endDate: SAMPLE_DATE, deviceId: '', maxAccuracyMeters: 50, includeRejected: false };
  assert.equal(serializeTracksUrlFilters(fFalse).has('rejected'), false);
});

test('serializeTracksUrlFilters preserves unrelated existing query parameters', () => {
  const base = new URLSearchParams('foo=bar&token=abc&range=old&start=old&end=old&device=old&accuracy=1&rejected=1');
  const f: TracksUrlFilters = {
    range: '7d',
    startDate: SAMPLE_DATE,
    endDate: SAMPLE_DATE,
    deviceId: 'pixel-8',
    maxAccuracyMeters: 100,
    includeRejected: false,
  };
  const sp = serializeTracksUrlFilters(f, base);
  assert.equal(sp.get('foo'), 'bar', 'unrelated foo preserved');
  assert.equal(sp.get('token'), 'abc', 'unrelated token preserved');
  assert.equal(sp.get('range'), '7d');
  assert.equal(sp.get('start'), SAMPLE_DATE);
  assert.equal(sp.get('end'), SAMPLE_DATE);
  assert.equal(sp.get('device'), 'pixel-8');
  assert.equal(sp.get('accuracy'), '100');
  assert.equal(sp.has('rejected'), false, 'rejected deleted when false');
});

test('URL roundtrip preserves filters', () => {
  const original = 'range=30d&start=2026-07-01&end=2026-07-08&device=pixel-8&accuracy=100&rejected=1';
  const sp = new URLSearchParams(original);
  const f = parseTracksUrlFilters(sp);
  const roundtrip = serializeTracksUrlFilters(f).toString();
  for (const key of ['range', 'start', 'end', 'device', 'accuracy', 'rejected']) {
    assert.ok(roundtrip.includes(key), `roundtrip should include ${key}`);
  }
});

test('tracksUrlFiltersToParams maps to MobileLocationAnalyticsParams without device when empty', () => {
  const f: TracksUrlFilters = {
    range: '7d', startDate: SAMPLE_DATE, endDate: SAMPLE_DATE,
    deviceId: '', maxAccuracyMeters: 50, includeRejected: false,
  };
  const p = tracksUrlFiltersToParams(f);
  assert.equal(p.deviceId, undefined, 'empty deviceId becomes undefined');
  assert.equal(p.maxAccuracyMeters, 50);
  assert.equal(p.includeRejected, false);
});

test('tracksUrlFiltersToParams includes deviceId when non-empty', () => {
  const f: TracksUrlFilters = {
    range: '7d', startDate: SAMPLE_DATE, endDate: SAMPLE_DATE,
    deviceId: 'pixel-8', maxAccuracyMeters: 100, includeRejected: true,
  };
  const p = tracksUrlFiltersToParams(f);
  assert.equal(p.deviceId, 'pixel-8');
  assert.equal(p.maxAccuracyMeters, 100);
  assert.equal(p.includeRejected, true);
});

test('parseTracksUrlFilters preserves custom start/end dates', () => {
  const sp = new URLSearchParams('range=custom&start=2026-06-01&end=2026-06-15');
  const f = parseTracksUrlFilters(sp);
  assert.equal(f.range, 'custom');
  assert.equal(f.startDate, '2026-06-01');
  assert.equal(f.endDate, '2026-06-15');
});

test('parseTracksUrlFilters falls back when URL dates are invalid', () => {
  const defaults = parseTracksUrlFilters(new URLSearchParams('range=7d'));
  const parsed = parseTracksUrlFilters(
    new URLSearchParams('range=7d&start=not-a-date&end=2026-02-31'),
  );
  assert.equal(parsed.startDate, defaults.startDate);
  assert.equal(parsed.endDate, defaults.endDate);
});

// ────────────────────────────────────────────────────────────────────
//  3. Cursor pagination pure helper
// ────────────────────────────────────────────────────────────────────

test('canAdvanceRawPointPage requires both hasMore and nextCursor', () => {
  assert.equal(canAdvanceRawPointPage({ hasMore: true, nextCursor: 'c1' }), true);
  assert.equal(canAdvanceRawPointPage({ hasMore: true, nextCursor: null }), false);
  assert.equal(canAdvanceRawPointPage({ hasMore: true, nextCursor: '' }), false);
  assert.equal(canAdvanceRawPointPage({ hasMore: false, nextCursor: 'c1' }), false);
  assert.equal(canAdvanceRawPointPage({ hasMore: false, nextCursor: null }), false);
});

test('advanceRawPointCursorStack pushes cursor and advances page only when allowed', () => {
  const stack: string[] = [];
  const denied = advanceRawPointCursorStack({
    cursorStack: stack,
    pageIndex: 0,
    hasMore: true,
    nextCursor: null,
  });
  assert.equal(denied.didAdvance, false);
  assert.equal(denied.nextPageIndex, 0);
  assert.deepEqual(denied.cursorStack, []);

  const allowed = advanceRawPointCursorStack({
    cursorStack: stack,
    pageIndex: 0,
    hasMore: true,
    nextCursor: 'cursor-page-2',
  });
  assert.equal(allowed.didAdvance, true);
  assert.equal(allowed.nextPageIndex, 1);
  assert.deepEqual(allowed.cursorStack, ['cursor-page-2']);

  const page2 = advanceRawPointCursorStack({
    cursorStack: allowed.cursorStack,
    pageIndex: 1,
    hasMore: true,
    nextCursor: 'cursor-page-3',
  });
  assert.equal(page2.didAdvance, true);
  assert.equal(page2.nextPageIndex, 2);
  assert.deepEqual(page2.cursorStack, ['cursor-page-2', 'cursor-page-3']);
});

test('HistoricalLocationPage uses pageSize 200 and resets on effectiveSelectedSegmentId change', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/HistoricalLocationPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('pageSize = 200') || pageSource.includes('pageSize: 200'),
    'pageSize must be 200');
  assert.ok(pageSource.includes('canAdvanceRawPointPage') || pageSource.includes('currentNextCursor'),
    'must gate next page on cursor');
  assert.ok(
    pageSource.includes('effectiveSelectedSegmentId')
    && (pageSource.includes('useEffect') || pageSource.includes('useLayoutEffect')),
    'must reset pagination when effectiveSelectedSegmentId changes via effect',
  );
  assert.ok(
    pageSource.includes('serializeTracksUrlFilters')
    && !pageSource.includes('if (!embedded) return'),
    'URL sync must persist filters for both embedded and desktop',
  );
  assert.ok(pageSource.includes('tracksUrlFiltersToParams'),
    'page should use the shared URL-to-API filter mapping');
  assert.ok(!/const \[nextCursor,\s*setNextCursor\]/.test(pageSource),
    'unused nextCursor state must be removed');
});

// ────────────────────────────────────────────────────────────────────
//  4. Raw point table states
// ────────────────────────────────────────────────────────────────────

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

import LocationRawPointTable from '../../src/client-web/src/components/mobile/LocationRawPointTable';
import type { MobileLocationPoint } from '../../src/client-web/src/api/mobile';

type ReactNodeLike = {
  props?: Record<string, unknown> & { children?: unknown };
  type?: unknown;
};

function flattenChildren(children: unknown): unknown[] {
  if (Array.isArray(children)) return children.flatMap(flattenChildren);
  if (children === null || children === undefined || children === false) return [];
  return [children];
}

function textContent(node: unknown): string {
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (!node || typeof node !== 'object') return '';
  return flattenChildren((node as ReactNodeLike).props?.children).map(textContent).join('');
}

function findElement(node: unknown, predicate: (node: ReactNodeLike) => boolean): ReactNodeLike {
  if (node && typeof node === 'object') {
    const element = node as ReactNodeLike;
    if (predicate(element)) return element;
    for (const child of flattenChildren(element.props?.children)) {
      try {
        return findElement(child, predicate);
      } catch {
        // Continue searching siblings.
      }
    }
  }
  throw new Error('Expected element was not found.');
}

const samplePoint: MobileLocationPoint = {
  id: 'p1', deviceId: 'd1', recordedAtUtc: '2026-07-07T10:00:00Z',
  submittedAtUtc: '2026-07-07T10:00:10Z', latitude: 31.23, longitude: 121.47,
  horizontalAccuracyMeters: 12, provider: 'gps', sourceKind: 'auto',
  altitudeMeters: null, verticalAccuracyMeters: null,
  speedMetersPerSecond: null, speedAccuracyMetersPerSecond: null,
  bearingDegrees: null, bearingAccuracyDegrees: null,
  isAutoSubmitted: true, quality: 'usable', rawJson: '{}',
};

const baseTableProps = {
  selectedSegmentId: 'seg-1' as string | null,
  currentPage: 1,
  hasNextPage: false,
  hasPreviousPage: false,
  isFetching: false,
  error: null as string | null,
  selectedPointId: null as string | null,
  onSelectPoint: undefined as ((pointId: string) => void) | undefined,
};

test('LocationRawPointTable shows no-segment state when no segment selected', () => {
  const html = renderToStaticMarkup(
    React.createElement(LocationRawPointTable, {
      ...baseTableProps,
      points: [],
      selectedSegmentId: null,
      onPreviousPage: () => undefined,
      onNextPage: () => undefined,
      onRetry: () => undefined,
    }),
  );
  assert.ok(html.includes('选择片段'), 'should show select-segment message');
  assert.ok(!html.includes('上一页'), 'should not show pagination when no segment');
});

test('LocationRawPointTable shows loading state when isFetching with selected segment', () => {
  const html = renderToStaticMarkup(
    React.createElement(LocationRawPointTable, {
      ...baseTableProps,
      points: [],
      isFetching: true,
      onPreviousPage: () => undefined,
      onNextPage: () => undefined,
      onRetry: () => undefined,
    }),
  );
  assert.ok(html.includes('加载'), 'should show loading text');
  assert.ok(!html.includes('选择片段'), 'should not show select-segment message when segment selected');
});

test('LocationRawPointTable shows error state with retry button', () => {
  const html = renderToStaticMarkup(
    React.createElement(LocationRawPointTable, {
      ...baseTableProps,
      points: [],
      error: '加载失败',
      onPreviousPage: () => undefined,
      onNextPage: () => undefined,
      onRetry: () => undefined,
    }),
  );
  assert.ok(html.includes('加载失败'), 'should show error message');
  assert.ok(html.includes('RotateCw') || html.includes('重试'), 'should show retry action');
});

test('LocationRawPointTable shows distinct empty state when selected segment has zero points', () => {
  const html = renderToStaticMarkup(
    React.createElement(LocationRawPointTable, {
      ...baseTableProps,
      points: [],
      onPreviousPage: () => undefined,
      onNextPage: () => undefined,
      onRetry: () => undefined,
    }),
  );
  assert.ok(html.includes('当前片段没有原始点。'), 'should show zero-points message');
  assert.ok(!html.includes('<table'), 'should not render empty table');
  assert.ok(!html.includes('上一页'), 'should not show pagination in zero-points state');
});

test('LocationRawPointTable uses factual subtitle for current segment points', () => {
  const html = renderToStaticMarkup(
    React.createElement(LocationRawPointTable, {
      ...baseTableProps,
      points: [samplePoint],
      hasNextPage: true,
      hasPreviousPage: true,
      currentPage: 2,
      onPreviousPage: () => undefined,
      onNextPage: () => undefined,
      onRetry: () => undefined,
    }),
  );
  assert.ok(html.includes('当前片段定位点'), 'should use factual subtitle');
  assert.ok(!html.includes('完整原始数据继续分页读取'), 'must not use instructional feature copy');
  assert.ok(html.includes('第 2 页'), 'should show current page number');
  assert.ok(html.includes('1 点'), 'should show point count');
});

test('LocationRawPointTable disables previous on page 1 and next when no next', () => {
  const tree = LocationRawPointTable({
    ...baseTableProps,
    points: [samplePoint],
    currentPage: 1,
    hasNextPage: false,
    hasPreviousPage: false,
    onPreviousPage: () => undefined,
    onNextPage: () => undefined,
    onRetry: () => undefined,
  });
  const prev = findElement(tree, node => textContent(node).includes('上一页') && typeof node.props?.onClick === 'function');
  const next = findElement(tree, node => textContent(node).includes('下一页') && typeof node.props?.onClick === 'function');
  assert.equal(prev.props?.disabled, true, 'previous disabled on page 1');
  assert.equal(next.props?.disabled, true, 'next disabled when no next page');
});

test('LocationRawPointTable previous enabled and next enabled when both available', () => {
  const tree = LocationRawPointTable({
    ...baseTableProps,
    points: [samplePoint],
    currentPage: 2,
    hasNextPage: true,
    hasPreviousPage: true,
    onPreviousPage: () => undefined,
    onNextPage: () => undefined,
    onRetry: () => undefined,
  });
  const prev = findElement(tree, node => textContent(node).includes('上一页') && typeof node.props?.onClick === 'function');
  const next = findElement(tree, node => textContent(node).includes('下一页') && typeof node.props?.onClick === 'function');
  assert.equal(prev.props?.disabled, false, 'previous enabled when has previous');
  assert.equal(next.props?.disabled, false, 'next enabled when has next');
});

test('LocationRawPointTable invokes previous/next/retry onClick callbacks from element tree', () => {
  let prevCalled = 0;
  let nextCalled = 0;
  let retryCalled = 0;
  const tree = LocationRawPointTable({
    ...baseTableProps,
    points: [samplePoint],
    currentPage: 2,
    hasNextPage: true,
    hasPreviousPage: true,
    error: '加载失败',
    onPreviousPage: () => { prevCalled += 1; },
    onNextPage: () => { nextCalled += 1; },
    onRetry: () => { retryCalled += 1; },
  });
  const prev = findElement(tree, node => textContent(node).includes('上一页') && typeof node.props?.onClick === 'function');
  const next = findElement(tree, node => textContent(node).includes('下一页') && typeof node.props?.onClick === 'function');
  const retry = findElement(tree, node => textContent(node).includes('重试') && typeof node.props?.onClick === 'function');
  (prev.props?.onClick as () => void)();
  (next.props?.onClick as () => void)();
  (retry.props?.onClick as () => void)();
  assert.equal(prevCalled, 1, 'previous onClick invoked');
  assert.equal(nextCalled, 1, 'next onClick invoked');
  assert.equal(retryCalled, 1, 'retry onClick invoked');
});

// ────────────────────────────────────────────────────────────────────
//  5. HistoricalLocationPage accepts embedded prop
// ────────────────────────────────────────────────────────────────────

test('HistoricalLocationPage default export accepts embedded prop', () => {
  const pageSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/HistoricalLocationPage.tsx'),
    'utf8',
  );
  assert.ok(pageSource.includes('embedded') || pageSource.includes('embedded?'),
    'HistoricalLocationPage must accept embedded prop');
  assert.ok(pageSource.includes('HistoricalLocationDashboard'),
    'page must still render the dashboard');
});

test('HistoricalLocationDashboard interface accepts embedded prop', () => {
  const dasSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/components/mobile/HistoricalLocationDashboard.tsx'),
    'utf8',
  );
  assert.ok(dasSource.includes('embedded'),
    'Dashboard must accept embedded prop');
});

// ────────────────────────────────────────────────────────────────────
//  6. Android TracksScreen source expectations
// ────────────────────────────────────────────────────────────────────

test('TracksScreen uses PimWebViewScreen with /embed/android/tracks', () => {
  const kotlinSource = readFileSync(
    path.join(process.cwd(), 'src/client-android/app/src/main/java/com/pim/app/ui/tracks/TracksScreen.kt'),
    'utf8',
  );
  assert.ok(kotlinSource.includes('PimWebViewScreen'),
    'TracksScreen must use PimWebViewScreen');
  assert.ok(kotlinSource.includes('/embed/android/tracks'),
    'TracksScreen must use /embed/android/tracks route');
  assert.ok(kotlinSource.includes('serverUrl'),
    'TracksScreen must reference serverUrl');
});

test('TracksScreen reuses shared bridge from hiltViewModel', () => {
  const kotlinSource = readFileSync(
    path.join(process.cwd(), 'src/client-android/app/src/main/java/com/pim/app/ui/tracks/TracksScreen.kt'),
    'utf8',
  );
  assert.ok(kotlinSource.includes('hiltViewModel') || kotlinSource.includes('viewModel.bridge'),
    'TracksScreen should use hiltViewModel for shared bridge');
});

test('TracksScreen checks androidEmbedV1 capability', () => {
  const kotlinSource = readFileSync(
    path.join(process.cwd(), 'src/client-android/app/src/main/java/com/pim/app/ui/tracks/TracksScreen.kt'),
    'utf8',
  );
  assert.ok(kotlinSource.includes('embedSupported') ||
    kotlinSource.includes('EmbedUnsupported') ||
    kotlinSource.includes('androidEmbedV1'),
    'TracksScreen must check androidEmbedV1 capability');
});

test('TracksScreen shows unsupported message with 打开设置 action', () => {
  const kotlinSource = readFileSync(
    path.join(process.cwd(), 'src/client-android/app/src/main/java/com/pim/app/ui/tracks/TracksScreen.kt'),
    'utf8',
  );
  assert.ok(kotlinSource.includes('服务器版本不支持嵌入页面'),
    'TracksScreen must show Chinese unsupported message');
  assert.ok(kotlinSource.includes('打开设置'),
    'TracksScreen must show 打开设置 button');
});

test('PimRootScreen passes onOpenSettings to TracksScreen', () => {
  const rootSource = readFileSync(
    path.join(process.cwd(), 'src/client-android/app/src/main/java/com/pim/app/ui/root/PimRootScreen.kt'),
    'utf8',
  );
  const tracksBranch = rootSource.split('PimDestination.Tracks -> ')[1].split('PimDestination.Schedule ->')[0];
  assert.ok(tracksBranch.includes('TracksScreen(') && tracksBranch.includes('onOpenSettings ='),
    'PimRootScreen must pass onOpenSettings to TracksScreen');
});

void _runAll();
