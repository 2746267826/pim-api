import assert from 'node:assert/strict';
import {
  buildMapDisplayModel,
  dedupePathPoints,
  distanceMeters,
  isJumpPoint,
  pathCentroid,
  scatterRadiusMeters,
} from '../../src/client-web/src/components/mobile/mobileMapModel';
import type {
  MobileLocationPathPoint,
  MobileLocationSegment,
  MobileLocationTrack,
} from '../../src/client-web/src/api/mobile';

function test(_name: string, run: () => void) {
  run();
}

function pathPoint(
  id: string,
  latitude: number,
  longitude: number,
  qualityFlags: string[] = [],
  recordedAtUtc: string | null = null,
): MobileLocationPathPoint {
  return {
    id,
    latitude,
    longitude,
    recordedAtUtc,
    horizontalAccuracyMeters: 15,
    quality: 'usable',
    qualityFlags,
  };
}

function segment(
  id: string,
  kind: string,
  points: MobileLocationPathPoint[],
  extra: Partial<MobileLocationSegment> = {},
): MobileLocationSegment {
  return {
    id,
    trackId: 'track-1',
    deviceId: 'pixel-8',
    kind,
    startUtc: '2026-08-13T07:01:14Z',
    endUtc: '2026-08-13T07:09:20Z',
    localStart: '2026-08-13 15:01',
    localEnd: '2026-08-13 15:09',
    durationSeconds: 486,
    distanceMeters: 0,
    pointCount: points.length,
    averageSpeedMetersPerSecond: 0,
    averageAccuracyMeters: 15,
    maxAccuracyMeters: 40,
    quality: 'usable',
    qualityFlags: [],
    bounds: null,
    path: points,
    ...extra,
  };
}

function track(id: string, segments_: MobileLocationSegment[]): MobileLocationTrack {
  return {
    id,
    deviceId: 'pixel-8',
    startUtc: '2026-08-13T07:01:14Z',
    endUtc: '2026-08-13T07:09:20Z',
    distanceMeters: 0,
    durationSeconds: 486,
    pointCount: segments_.reduce((sum, item) => sum + item.path.length, 0),
    segmentCount: segments_.length,
    bounds: null,
    qualityFlags: [],
    segments: segments_,
  };
}

test('dedupePathPoints collapses same timestamp+coordinate duplicates', () => {
  const points = [
    pathPoint('a1', 36.6499244, 116.9693454, [], '2026-08-13T07:01:14Z'),
    pathPoint('a2', 36.6499244, 116.9693454, [], '2026-08-13T07:01:14Z'),
    pathPoint('a3', 36.650488, 116.969812, [], '2026-08-13T07:07:25Z'),
  ];
  const deduped = dedupePathPoints(points);
  assert.equal(deduped.length, 2);
  assert.equal(deduped[0].id, 'a1');
});

test('isJumpPoint detects the server jump-point flag', () => {
  assert.equal(isJumpPoint(pathPoint('x', 1, 1)), false);
  assert.equal(isJumpPoint(pathPoint('x', 1, 1, ['jump-point'])), true);
});

test('distanceMeters approximates haversine distance', () => {
  const meters = distanceMeters(31.230416, 121.473701, 31.231416, 121.473701);
  assert.ok(meters > 100 && meters < 120, `expected ~111m, got ${meters}`);
});

test('pathCentroid and scatterRadiusMeters use regular points', () => {
  const points = [
    pathPoint('p1', 31.230416, 121.473701),
    pathPoint('p2', 31.230820, 121.473701),
  ];
  const center = pathCentroid(points);
  assert.ok(Math.abs(center[0] - 31.230618) < 0.0001);
  const radius = scatterRadiusMeters(points, center);
  assert.ok(radius > 20 && radius < 25, `expected ~22m, got ${radius}`);
});

test('stay segment aggregates into a single centroid marker without polylines', () => {
  const stay = segment('stay-1', 'stay', [
    pathPoint('a1', 36.6499244, 116.9693454, [], '2026-08-13T07:01:14Z'),
    pathPoint('a2', 36.6499244, 116.9693454, [], '2026-08-13T07:01:14Z'),
    pathPoint('a3', 36.6504880, 116.9698120, [], '2026-08-13T07:07:25Z'),
    pathPoint('a4', 36.6503990, 116.9694860, [], '2026-08-13T07:07:43Z'),
    pathPoint('a5', 36.6503990, 116.9694860, [], '2026-08-13T07:07:43Z'),
    pathPoint('a6', 36.6501050, 116.9697910, [], '2026-08-13T07:09:09Z'),
  ]);
  const model = buildMapDisplayModel([track('t1', [stay])], null);

  assert.equal(model.stayMarkers.length, 1);
  assert.equal(model.movePolylines.length, 0);
  assert.equal(model.pointMarkers.length, 0);
  const marker = model.stayMarkers[0];
  assert.equal(marker.segmentId, 'stay-1');
  assert.equal(marker.pointCount, 4, 'auto+manual duplicates deduped before counting');
  assert.ok(marker.scatterRadiusMeters > 20, `scatter radius should reflect jitter, got ${marker.scatterRadiusMeters}`);
  assert.ok(Math.abs(marker.position[0] - (36.6499244 + 36.650488 + 36.650399 + 36.650105) / 4) < 0.00001);
});

test('move segment renders polyline only unless selected', () => {
  const move = segment('move-1', 'move', [
    pathPoint('m1', 31.230416, 121.473701),
    pathPoint('m2', 31.230820, 121.473701),
    pathPoint('m3', 31.231226, 121.473701),
  ]);

  const unselected = buildMapDisplayModel([track('t1', [move])], null);
  assert.equal(unselected.movePolylines.length, 1);
  assert.equal(unselected.movePolylines[0].positions.length, 3);
  assert.equal(unselected.pointMarkers.length, 0, 'move points hidden until segment selected');
  assert.equal(unselected.stayMarkers.length, 0);

  const selected = buildMapDisplayModel([track('t1', [move])], 'move-1');
  assert.equal(selected.movePolylines.length, 1);
  assert.equal(selected.pointMarkers.length, 3, 'selected move segment reveals its points');
});

test('jump points are always gray markers and excluded from lines and centroid', () => {
  const move = segment('move-jump', 'move', [
    pathPoint('m1', 31.230416, 121.473701),
    pathPoint('m2', 31.230820, 121.473701),
    pathPoint('m3', 31.240000, 121.490000, ['jump-point']),
    pathPoint('m4', 31.231226, 121.473701),
  ]);
  const model = buildMapDisplayModel([track('t1', [move])], null);

  assert.equal(model.movePolylines.length, 1);
  assert.deepEqual(
    model.movePolylines[0].positions,
    [[31.230416, 121.473701], [31.230820, 121.473701], [31.231226, 121.473701]],
    'jump point excluded from polyline vertices',
  );
  const jumps = model.pointMarkers.filter(point => point.isJump);
  assert.equal(jumps.length, 1);
  assert.equal(jumps[0].pointId, 'm3');
});

test('jump points excluded from stay marker centroid and radius', () => {
  const stay = segment('stay-jump', 'stay', [
    pathPoint('s1', 31.230416, 121.473701),
    pathPoint('s2', 31.230820, 121.473701, ['jump-point']),
    pathPoint('s3', 31.231226, 121.473701),
  ]);
  const model = buildMapDisplayModel([track('t1', [stay])], null);

  assert.equal(model.stayMarkers.length, 1);
  const marker = model.stayMarkers[0];
  assert.ok(Math.abs(marker.position[0] - 31.230821) < 0.0001, 'centroid uses regular points only');
  assert.ok(marker.scatterRadiusMeters < 50, 'radius must not include the jump point');
  assert.equal(model.pointMarkers.filter(point => point.isJump).length, 1);
});

test('single point segment renders as a plain marker', () => {
  const single = segment('single-1', 'stay', [pathPoint('s1', 31.230416, 121.473701)]);
  const model = buildMapDisplayModel([track('t1', [single])], null);

  assert.equal(model.stayMarkers.length, 0);
  assert.equal(model.movePolylines.length, 0);
  assert.equal(model.pointMarkers.length, 1);
  assert.equal(model.pointMarkers[0].pointId, 's1');
  assert.equal(model.pointMarkers[0].isJump, false);
});

test('mixed tracks combine stay aggregation, move polylines and jump markers', () => {
  const stay = segment('stay-1', 'stay', [
    pathPoint('a1', 36.6499244, 116.9693454),
    pathPoint('a2', 36.6504880, 116.9698120),
  ]);
  const move = segment('move-1', 'move', [
    pathPoint('m1', 36.6600000, 116.9800000),
    pathPoint('m2', 36.6700000, 116.9900000),
  ]);
  const model = buildMapDisplayModel([track('t1', [stay, move])], null);

  assert.equal(model.stayMarkers.length, 1);
  assert.equal(model.movePolylines.length, 1);
  assert.equal(model.movePolylines[0].positions.length, 2);
  assert.equal(model.pointMarkers.length, 0);
});
