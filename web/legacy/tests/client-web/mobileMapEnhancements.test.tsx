import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { simplifyPath } from '../../src/client-web/src/components/mobile/pathSmoothing';
import {
  buildFrequentPlaceCircles,
  buildMovementMetricStrip,
} from '../../src/client-web/src/components/mobile/mobileMapExtras';
import { chartColors } from '../../src/client-web/src/components/charts/chartColors';
import type {
  MobileFrequentPlace,
  MobileMovementStatsResponse,
} from '../../src/client-web/src/api/mobile';

function test(_name: string, run: () => void) {
  run();
}

test('simplifyPath collapses a middle point within tolerance (default 15m)', () => {
  const base: [number, number] = [31.230416, 121.473701];
  const end: [number, number] = [31.231416, 121.473701];
  const middle: [number, number] = [31.230916, 121.473716]; // ~1.4m east of the line
  const result = simplifyPath([base, middle, end]);
  assert.equal(result.length, 2);
  assert.deepEqual(result[0], base);
  assert.deepEqual(result[1], end);
});

test('simplifyPath keeps a middle point deviating beyond tolerance', () => {
  const base: [number, number] = [31.230416, 121.473701];
  const end: [number, number] = [31.231416, 121.473701];
  const middle: [number, number] = [31.230916, 121.475000]; // ~120m east of the line
  const result = simplifyPath([base, middle, end]);
  assert.equal(result.length, 3);
});

test('simplifyPath always keeps first and last points', () => {
  const points: [number, number][] = [
    [31.230416, 121.473701],
    [31.230520, 121.473710],
    [31.230620, 121.473720],
    [31.231416, 121.473701],
  ];
  const result = simplifyPath(points, 15);
  assert.equal(result.length, 2, 'near-line intermediate points collapse within tolerance');
  assert.deepEqual(result[0], points[0]);
  assert.deepEqual(result[result.length - 1], points[points.length - 1]);
});

test('simplifyPath returns empty, single and double point input unchanged', () => {
  assert.deepEqual(simplifyPath([]), []);
  const single: [number, number][] = [[31.230416, 121.473701]];
  assert.deepEqual(simplifyPath(single), single);
  const pair: [number, number][] = [
    [31.230416, 121.473701],
    [31.231416, 121.473701],
  ];
  assert.deepEqual(simplifyPath(pair), pair);
});

test('buildFrequentPlaceCircles maps places and colors home primary, others activity', () => {
  const places: MobileFrequentPlace[] = [
    {
      centerLatitude: 31.230416,
      centerLongitude: 121.473701,
      radiusMeters: 200,
      pointCount: 120,
      visitDayCount: 9,
      isHome: true,
    },
    {
      centerLatitude: 31.240416,
      centerLongitude: 121.483701,
      radiusMeters: 150,
      pointCount: 40,
      visitDayCount: 3,
      isHome: false,
    },
  ];
  const circles = buildFrequentPlaceCircles(places);
  assert.equal(circles.length, 2);
  assert.deepEqual(circles[0].center, [31.230416, 121.473701]);
  assert.equal(circles[0].radiusMeters, 200);
  assert.equal(circles[0].isHome, true);
  assert.equal(circles[0].color, chartColors.primary);
  assert.equal(circles[0].pointCount, 120);
  assert.equal(circles[0].visitDayCount, 9);
  assert.equal(circles[1].color, chartColors.activity);
});

test('buildMovementMetricStrip formats stats into four metric cards', () => {
  const stats: MobileMovementStatsResponse = {
    homeCenter: null,
    outingCount: 3,
    outingSeconds: 7320,
    outings: [],
    distanceMeters: 12500,
    maxSpeedMetersPerSecond: 12.34,
    perDay: [],
  };
  const items = buildMovementMetricStrip(stats);
  assert.equal(items.length, 4);
  assert.deepEqual(
    items.map(item => item.label),
    ['出门次数', '外出时长', '移动里程', '速度峰值'],
  );
  assert.equal(items[0].value, '3');
  assert.equal(items[1].value, '2 小时 2 分');
  assert.equal(items[2].value, '12.5 km');
  assert.equal(items[3].value, '12.3 m/s');
});

test('buildMovementMetricStrip renders dashes for null stats and null peak speed', () => {
  const empty = buildMovementMetricStrip(null);
  assert.ok(empty.every(item => item.value === '—'));
  const stats = {
    homeCenter: null,
    outingCount: 0,
    outingSeconds: 0,
    outings: [],
    distanceMeters: 0,
    maxSpeedMetersPerSecond: null,
    perDay: [],
  } as MobileMovementStatsResponse;
  const items = buildMovementMetricStrip(stats);
  assert.equal(items[3].value, '—');
});

test('buildMovementMetricStrip carries minute overflow into hours', () => {
  const stats = {
    homeCenter: null,
    outingCount: 1,
    outingSeconds: 3599,
    outings: [],
    distanceMeters: 1000,
    maxSpeedMetersPerSecond: 1,
    perDay: [],
  } as MobileMovementStatsResponse;
  const items = buildMovementMetricStrip(stats);
  assert.equal(items[1].value, '1 小时 0 分', '3599s rounds to 60 minutes which carries to 1 hour');
});

test('leaflet map source keeps CI identifiers and adds smoothing, circle layers', () => {
  const source = readFileSync(
    path.join(process.cwd(), 'src/client-web/src/components/mobile/HistoricalLocationLeafletMap.tsx'),
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
    assert.equal(source.includes(text), true, `source should keep CI identifier: ${text}`);
  }
  for (const text of ['Circle', 'simplifyPath', 'buildFrequentPlaceCircles']) {
    assert.equal(source.includes(text), true, `source should add: ${text}`);
  }
});
