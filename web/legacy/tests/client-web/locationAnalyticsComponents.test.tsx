import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import HistoricalLocationDashboard from '../../src/client-web/src/components/mobile/HistoricalLocationDashboard';
import type {
  MobileDevice,
  MobileLocationAnalyticsOverview,
  MobileLocationPoint,
  MobileLocationTrack,
} from '../../src/client-web/src/api/mobile';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

function test(_name: string, run: () => void) {
  run();
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
  pointCount: 428,
  usablePointCount: 391,
  rejectedPointCount: 37,
  activeSpanSeconds: 583200,
  distanceMeters: 84600,
  stayCount: 12,
  longestStaySeconds: 12000,
  averageAccuracyMeters: 18,
  qualityIssueCount: 2,
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
        path: [
          {
            latitude: 31.230416,
            longitude: 121.473701,
            recordedAtUtc: '2026-07-07T10:00:00Z',
            horizontalAccuracyMeters: 12,
            quality: 'usable',
          },
        ],
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

test('historical location dashboard renders accepted Chinese workbench baseline', () => {
  const html = renderToStaticMarkup(
    React.createElement(HistoricalLocationDashboard, {
      rangeShortcut: '7d',
      rangeStartDate: '2026-07-02',
      rangeEndDate: '2026-07-08',
      selectedDeviceId: 'pixel-8',
      devices: [device],
      maxAccuracyMeters: 50,
      includeRejected: false,
      overview,
      tracks,
      selectedSegmentId: 'segment-move-1',
      selectedPointId: 'point-1',
      points,
      isLoading: false,
      isFetching: false,
      errorMessage: null,
      onShortcutChange: () => undefined,
      onCustomRangeChange: () => undefined,
      onDeviceChange: () => undefined,
      onMaxAccuracyChange: () => undefined,
      onIncludeRejectedChange: () => undefined,
      onRefresh: () => undefined,
      onSelectSegment: () => undefined,
      onSelectPoint: () => undefined,
    }),
  );

  for (const text of [
    '历史位置',
    '今天',
    '7天',
    '30天',
    '自定义',
    '北京时间',
    '设备',
    '范围',
    '最大误差',
    '质量',
    '定位点',
    '活跃跨度',
    '估算里程',
    '停留点',
    '平均误差',
    '质量提示',
    '轨迹地图',
    '选中片段',
    '停留与移动时间线',
    '原始点明细',
    '移动',
    'GPS',
  ]) {
    assert.equal(html.includes(text), true, `historical location UI should include: ${text}`);
  }

  assert.equal(html.includes('定位点列表'), false);
  assert.equal(html.includes('选中点详情'), false);
  assert.equal(html.includes('aria-label="结束日期"'), true);
});

test('historical location map renders segment layers and marker styles', () => {
  const leafletSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/components/mobile/HistoricalLocationLeafletMap.tsx'),
    'utf8',
  );
  const cssSource = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/index.css'),
    'utf8',
  );

  for (const text of [
    'Polyline',
    'selectedSegmentId',
    'pathOptions',
    '#2563eb',
    '#e11d48',
    '#14b8a6',
    'pim-location-marker-selected',
  ]) {
    assert.equal(leafletSource.includes(text), true, `Leaflet map source should include: ${text}`);
  }

  assert.equal(cssSource.includes('.pim-location-marker span'), true);
  assert.equal(cssSource.includes('.pim-location-marker-selected span'), true);
});
