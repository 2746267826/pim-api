import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import HistoricalLocationDashboard from '../../src/client-web/src/components/mobile/HistoricalLocationDashboard';
import LocationRawPointTable from '../../src/client-web/src/components/mobile/LocationRawPointTable';
import LocationStayMoveTimeline from '../../src/client-web/src/components/mobile/LocationStayMoveTimeline';
import type {
  MobileDevice,
  MobileLocationAnalyticsOverview,
  MobileLocationPoint,
  MobileLocationTrack,
} from '../../src/client-web/src/api/mobile';
import type { MobileRangeShortcut } from '../../src/client-web/src/components/mobile/mobileFormatting';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

function test(_name: string, run: () => void) {
  run();
}

type ReactNodeLike = {
  props?: Record<string, unknown> & { children?: unknown };
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

const device: MobileDevice = {
  id: 'device-row-1',
  deviceId: 'pixel-8',
  androidIdHash: 'hash-1',
  displayName: 'Pixel 8 Pro',
  manufacturer: 'Google',
  brand: 'google',
  model: 'Pixel 8 Pro',
  androidVersion: '16',
  sdkInt: 36,
  appVersion: '1.0.0',
  metadataJson: '{}',
  firstSeenAt: '2026-07-01T00:00:00Z',
  lastSeenAt: '2026-07-08T00:12:00Z',
  lastHeartbeatAt: '2026-07-08T00:12:00Z',
  lastSyncAt: '2026-07-08T00:12:00Z',
  isActive: true,
};

const overview: MobileLocationAnalyticsOverview = {
  range: {
    rangeStartUtc: '2026-07-01T16:00:00Z',
    rangeEndUtc: '2026-07-08T16:00:00Z',
    timezone: 'Asia/Shanghai',
    localStartDate: '2026-07-02',
    localEndDate: '2026-07-08',
  },
  generatedAt: '2026-07-08T00:12:00Z',
  pointCount: 12,
  usablePointCount: 10,
  rejectedPointCount: 2,
  activeSpanSeconds: 7200,
  distanceMeters: 3600,
  stayCount: 1,
  longestStaySeconds: 1200,
  averageAccuracyMeters: 18,
  qualityIssueCount: 1,
  qualityFlags: ['low-accuracy-cluster'],
};

const tracks: MobileLocationTrack[] = [
  {
    id: 'track-1',
    deviceId: 'pixel-8',
    startUtc: '2026-07-07T10:00:00Z',
    endUtc: '2026-07-07T11:00:00Z',
    distanceMeters: 3600,
    durationSeconds: 3600,
    pointCount: 3,
    segmentCount: 1,
    bounds: {
      minLatitude: 31.230416,
      minLongitude: 121.473701,
      maxLatitude: 31.240000,
      maxLongitude: 121.490000,
    },
    qualityFlags: [],
    segments: [
      {
        id: 'segment-move-1',
        trackId: 'track-1',
        deviceId: 'pixel-8',
        kind: 'move',
        startUtc: '2026-07-07T10:00:00Z',
        endUtc: '2026-07-07T11:00:00Z',
        localStart: '2026-07-07 18:00',
        localEnd: '2026-07-07 19:00',
        durationSeconds: 3600,
        distanceMeters: 3600,
        pointCount: 3,
        averageSpeedMetersPerSecond: 1,
        averageAccuracyMeters: 18,
        maxAccuracyMeters: 31,
        quality: 'usable',
        qualityFlags: [],
        bounds: {
          minLatitude: 31.230416,
          minLongitude: 121.473701,
          maxLatitude: 31.240000,
          maxLongitude: 121.490000,
        },
        path: [],
      },
    ],
  },
];

const points: MobileLocationPoint[] = [
  {
    id: 'point-1',
    deviceId: 'pixel-8',
    recordedAtUtc: '2026-07-07T10:00:00Z',
    submittedAtUtc: '2026-07-07T10:00:10Z',
    latitude: 31.230416,
    longitude: 121.473701,
    horizontalAccuracyMeters: 12,
    provider: 'gps',
    sourceKind: 'auto',
    altitudeMeters: null,
    verticalAccuracyMeters: null,
    speedMetersPerSecond: null,
    speedAccuracyMetersPerSecond: null,
    bearingDegrees: null,
    bearingAccuracyDegrees: null,
    isAutoSubmitted: true,
    quality: 'usable',
    rawJson: '{}',
  },
];

test('historical location dashboard controls emit range, segment, and raw point callbacks', () => {
  const shortcutChanges: MobileRangeShortcut[] = [];
  const customRangeChanges: Array<{ startDate: string; endDate: string }> = [];
  const segmentSelections: string[] = [];
  const pointSelections: string[] = [];
  const includeRejectedChanges: boolean[] = [];

  const tree = HistoricalLocationDashboard({
    rangeShortcut: '7d',
    rangeStartDate: '2026-07-02',
    rangeEndDate: '2026-07-08',
    selectedDeviceId: 'pixel-8',
    devices: [device],
    maxAccuracyMeters: 50,
    includeRejected: false,
    overview,
    tracks,
    selectedSegmentId: null,
    selectedPointId: null,
    points,
    isLoading: false,
    isFetching: false,
    errorMessage: null,
    rawPointsLoading: false,
    rawPointsError: null,
    rawPointsCurrentPage: 1,
    rawPointsHasNextPage: false,
    rawPointsHasPreviousPage: false,
    onShortcutChange: value => shortcutChanges.push(value),
    onCustomRangeChange: value => customRangeChanges.push(value),
    onDeviceChange: () => undefined,
    onMaxAccuracyChange: () => undefined,
    onIncludeRejectedChange: value => includeRejectedChanges.push(value),
    onRefresh: () => undefined,
    onSelectSegment: value => segmentSelections.push(value),
    onSelectPoint: value => pointSelections.push(value),
    onRawPointsPreviousPage: () => undefined,
    onRawPointsNextPage: () => undefined,
    onRawPointsRetry: () => undefined,
  });

  const thirtyDayButton = findElement(tree, node => textContent(node) === '30天');
  (thirtyDayButton.props?.onClick as () => void)();

  const startDateInput = findElement(tree, node => node.props?.['aria-label'] === '开始日期');
  (startDateInput.props?.onChange as (event: { target: { value: string } }) => void)({
    target: { value: '2026-07-03' },
  });

  const endDateInput = findElement(tree, node => node.props?.['aria-label'] === '结束日期');
  (endDateInput.props?.onChange as (event: { target: { value: string } }) => void)({
    target: { value: '2026-07-09' },
  });

  const includeRejectedToggle = findElement(tree, node => node.props?.['aria-label'] === '隐藏已拒绝点');
  (includeRejectedToggle.props?.onChange as (event: { target: { checked: boolean } }) => void)({
    target: { checked: false },
  });

  const timelineTree = LocationStayMoveTimeline({
    tracks,
    selectedSegmentId: null,
    onSelectSegment: value => segmentSelections.push(value),
  });
  const segmentButton = findElement(timelineTree, node => node.props?.['data-segment-id'] === 'segment-move-1');
  (segmentButton.props?.onClick as () => void)();

  const pointTableTree = LocationRawPointTable({
    points,
    selectedSegmentId: 'segment-move-1',
    currentPage: 1,
    hasNextPage: false,
    hasPreviousPage: false,
    isFetching: false,
    error: null,
    selectedPointId: null,
    onSelectPoint: value => pointSelections.push(value),
    onPreviousPage: () => undefined,
    onNextPage: () => undefined,
    onRetry: () => undefined,
  });
  const pointButton = findElement(pointTableTree, node => node.props?.['data-point-id'] === 'point-1');
  (pointButton.props?.onClick as () => void)();

  assert.deepEqual(shortcutChanges, ['30d']);
  assert.deepEqual(customRangeChanges, [
    { startDate: '2026-07-03', endDate: '2026-07-08' },
    { startDate: '2026-07-02', endDate: '2026-07-09' },
  ]);
  assert.deepEqual(includeRejectedChanges, [true]);
  assert.deepEqual(segmentSelections, ['segment-move-1']);
  assert.deepEqual(pointSelections, ['point-1']);
});

test('historical location page uses analytics APIs and Beijing 7 day defaults', () => {
  const source = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/HistoricalLocationPage.tsx'),
    'utf8',
  );

  for (const text of [
    'getMobileLocationAnalyticsOverview',
    'getMobileLocationAnalyticsTracks',
    'getMobileLocationAnalyticsSegmentPoints',
    'buildMobileAnalyticsDateRange',
    'toMobileAnalyticsUtcRange',
    'enabled: Boolean(effectiveSelectedSegmentId)',
    'setSelectedSegmentId',
    'parseTracksUrlFilters',
    'pageIndex',
    'cursorStack',
  ]) {
    assert.equal(source.includes(text), true, `HistoricalLocationPage should include: ${text}`);
  }

  assert.equal(source.includes('getMobileLocationHistory'), false);
  assert.equal(source.includes('startOfTodayInput'), false);
});
