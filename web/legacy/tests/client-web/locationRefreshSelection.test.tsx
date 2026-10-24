import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import HistoricalLocationDashboard from '../../src/client-web/src/components/mobile/HistoricalLocationDashboard';
import LocationSegmentDetail from '../../src/client-web/src/components/mobile/LocationSegmentDetail';
import { mobileApiPaths } from '../../src/client-web/src/api/mobile';
import type {
  MobileDevice,
  MobileLocationAnalyticsOverview,
  MobileLocationPoint,
  MobileLocationTrack,
} from '../../src/client-web/src/api/mobile';

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

function trackWithSegment(segmentId: string, kind: string): MobileLocationTrack {
  return {
    id: `track-${segmentId}`,
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
        id: segmentId,
        trackId: `track-${segmentId}`,
        deviceId: 'pixel-8',
        kind,
        startUtc: '2026-07-07T10:00:00Z',
        endUtc: '2026-07-07T11:00:00Z',
        localStart: '2026-07-07 18:00',
        localEnd: '2026-07-07 19:00',
        durationSeconds: 3600,
        distanceMeters: 3600,
        pointCount: 3,
        averageSpeedMetersPerSecond: kind === 'move' ? 1 : null,
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
  };
}

const points: MobileLocationPoint[] = [];

test('analytics paths append force=true only when force is true', () => {
  const forced = mobileApiPaths.locationAnalyticsTracks({
    rangeStartUtc: '2026-07-01T16:00:00Z',
    rangeEndUtc: '2026-07-08T16:00:00Z',
    force: true,
  });
  assert.equal(forced.includes('force=true'), true);

  const plain = mobileApiPaths.locationAnalyticsTracks({
    rangeStartUtc: '2026-07-01T16:00:00Z',
    rangeEndUtc: '2026-07-08T16:00:00Z',
  });
  assert.equal(plain.includes('force='), false);

  const explicitFalse = mobileApiPaths.locationAnalyticsTracks({
    rangeStartUtc: '2026-07-01T16:00:00Z',
    rangeEndUtc: '2026-07-08T16:00:00Z',
    force: false,
  });
  assert.equal(explicitFalse.includes('force='), false);

  const forcedOverview = mobileApiPaths.locationAnalyticsOverview({ force: true });
  assert.equal(forcedOverview.includes('force=true'), true);
});

test('page keeps user-selected segment after refresh and only defaults to the first when never selected', () => {
  const source = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/pages/HistoricalLocationPage.tsx'),
    'utf8',
  );

  for (const text of [
    'hasUserSelectedSegment',
    'forceRef',
    'requestReposition',
    'getDeferredAutoRefreshInterval',
    'if (hasUserSelectedSegment || selectionCleared)',
    'force: forceRef.current',
    "return segments[0]?.id ?? null;",
  ]) {
    assert.equal(source.includes(text), true, `HistoricalLocationPage should include: ${text}`);
  }
});

test('segment detail keeps rendering for the selected segment after refresh', () => {
  const tracks = [trackWithSegment('segment-move-1', 'move')];

  const tree = LocationSegmentDetail({
    tracks,
    selectedSegmentId: 'segment-move-1',
  });

  const allText = textContent(tree);
  assert.equal(allText.includes('估算里程'), true, 'kept selection should render segment detail');
  assert.equal(allText.includes('当前范围没有可展示的轨迹片段。'), false);
  assert.equal(allText.includes('在地图上点击轨迹或停留点以查看片段详情。'), false);
});

test('segment detail shows an empty-selection hint when no segment is selected', () => {
  const tracks = [trackWithSegment('segment-move-1', 'move')];

  const tree = LocationSegmentDetail({
    tracks,
    selectedSegmentId: null,
  });

  const allText = textContent(tree);
  assert.equal(allText.includes('在地图上点击轨迹或停留点以查看片段详情。'), true);
  assert.equal(allText.includes('估算里程'), false);
});

test('dashboard accepts repositionKey without breaking its props contract', () => {
  const tree = HistoricalLocationDashboard({
    rangeShortcut: '7d',
    rangeStartDate: '2026-07-02',
    rangeEndDate: '2026-07-08',
    selectedDeviceId: 'pixel-8',
    devices: [device],
    maxAccuracyMeters: 50,
    includeRejected: false,
    overview,
    tracks: [trackWithSegment('segment-move-1', 'move')],
    selectedSegmentId: 'segment-move-1',
    selectedPointId: null,
    repositionKey: 3,
    points,
    isLoading: false,
    isFetching: false,
    errorMessage: null,
    rawPointsLoading: false,
    rawPointsError: null,
    rawPointsCurrentPage: 1,
    rawPointsHasNextPage: false,
    rawPointsHasPreviousPage: false,
    onShortcutChange: () => undefined,
    onCustomRangeChange: () => undefined,
    onDeviceChange: () => undefined,
    onMaxAccuracyChange: () => undefined,
    onIncludeRejectedChange: () => undefined,
    onRefresh: () => undefined,
    onSelectSegment: () => undefined,
    onSelectPoint: () => undefined,
    onRawPointsPreviousPage: () => undefined,
    onRawPointsNextPage: () => undefined,
    onRawPointsRetry: () => undefined,
  });

  assert.equal(typeof tree, 'object');
});

test('map receives a repositionKey prop and its contract stays compatible', () => {
  const mapSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/components/mobile/LocationHistoryMap.tsx'),
    'utf8',
  );
  assert.equal(mapSource.includes('repositionKey'), true);

  const leafletSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/components/mobile/HistoricalLocationLeafletMap.tsx'),
    'utf8',
  );
  for (const text of ['repositionKey', 'MapRepositioner', 'MapInteractionNotifier', 'fitBounds']) {
    assert.equal(leafletSource.includes(text), true, `Leaflet map should include: ${text}`);
  }
});
