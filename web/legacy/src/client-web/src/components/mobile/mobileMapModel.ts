import type { MobileLocationPathPoint, MobileLocationTrack } from '../../api/mobile';

export const JUMP_POINT_FLAG = 'jump-point';

const EARTH_RADIUS_METERS = 6371000;

export function dedupePathPoints(points: MobileLocationPathPoint[]): MobileLocationPathPoint[] {
  const seen = new Set<string>();
  const result: MobileLocationPathPoint[] = [];
  for (const point of points) {
    const key = `${point.recordedAtUtc ?? ''}|${point.latitude.toFixed(6)}|${point.longitude.toFixed(6)}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(point);
  }
  return result;
}

export function isJumpPoint(point: MobileLocationPathPoint): boolean {
  return point.qualityFlags?.includes(JUMP_POINT_FLAG) ?? false;
}

export function distanceMeters(
  lat1: number,
  lon1: number,
  lat2: number,
  lon2: number,
): number {
  const toRadians = (degrees: number) => (degrees * Math.PI) / 180;
  const lat1Rad = toRadians(lat1);
  const lat2Rad = toRadians(lat2);
  const deltaLat = toRadians(lat2 - lat1);
  const deltaLon = toRadians(lon2 - lon1);
  const a = Math.sin(deltaLat / 2) ** 2
    + Math.cos(lat1Rad) * Math.cos(lat2Rad) * Math.sin(deltaLon / 2) ** 2;
  return 2 * EARTH_RADIUS_METERS * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

export function pathCentroid(points: MobileLocationPathPoint[]): [number, number] {
  const latitude = points.reduce((sum, point) => sum + point.latitude, 0) / points.length;
  const longitude = points.reduce((sum, point) => sum + point.longitude, 0) / points.length;
  return [latitude, longitude];
}

export function scatterRadiusMeters(
  points: MobileLocationPathPoint[],
  center: [number, number],
): number {
  return Math.max(
    0,
    ...points.map(point => distanceMeters(center[0], center[1], point.latitude, point.longitude)),
  );
}

export interface StayAggregateMarker {
  segmentId: string;
  position: [number, number];
  pointCount: number;
  durationSeconds: number;
  scatterRadiusMeters: number;
  maxAccuracyMeters: number;
}

export interface MoveSegmentPolyline {
  segmentId: string;
  positions: [number, number][];
}

export interface PathPointMarker {
  segmentId: string;
  segmentKind: string;
  pointId: string;
  position: [number, number];
  recordedAtUtc: string | null;
  horizontalAccuracyMeters: number | null;
  quality: string | null;
  isJump: boolean;
}

export interface MobileMapDisplayModel {
  stayMarkers: StayAggregateMarker[];
  movePolylines: MoveSegmentPolyline[];
  pointMarkers: PathPointMarker[];
}

function pathPosition(point: MobileLocationPathPoint): [number, number] {
  return [point.latitude, point.longitude];
}

function toPointMarker(segmentId: string, segmentKind: string, point: MobileLocationPathPoint): PathPointMarker {
  return {
    segmentId,
    segmentKind,
    pointId: point.id ?? `${segmentId}-point`,
    position: pathPosition(point),
    recordedAtUtc: point.recordedAtUtc ?? null,
    horizontalAccuracyMeters: point.horizontalAccuracyMeters ?? null,
    quality: point.quality ?? null,
    isJump: isJumpPoint(point),
  };
}

export function buildMapDisplayModel(
  tracks: MobileLocationTrack[],
  selectedSegmentId: string | null,
): MobileMapDisplayModel {
  const stayMarkers: StayAggregateMarker[] = [];
  const movePolylines: MoveSegmentPolyline[] = [];
  const pointMarkers: PathPointMarker[] = [];

  for (const segment of tracks.flatMap(track => track.segments)) {
    const path = dedupePathPoints(segment.path);
    if (path.length === 0) continue;

    const jumpPoints = path.filter(isJumpPoint);
    const regularPoints = path.filter(point => !isJumpPoint(point));

    if (regularPoints.length === 1) {
      pointMarkers.push(toPointMarker(segment.id, segment.kind, regularPoints[0]));
    } else if (segment.kind === 'stay') {
      const center = pathCentroid(regularPoints);
      stayMarkers.push({
        segmentId: segment.id,
        position: center,
        pointCount: path.length,
        durationSeconds: segment.durationSeconds,
        scatterRadiusMeters: scatterRadiusMeters(regularPoints, center),
        maxAccuracyMeters: segment.maxAccuracyMeters,
      });
    } else {
      movePolylines.push({
        segmentId: segment.id,
        positions: regularPoints.map(pathPosition),
      });
      if (segment.id === selectedSegmentId) {
        pointMarkers.push(...regularPoints.map(point => toPointMarker(segment.id, segment.kind, point)));
      }
    }

    pointMarkers.push(...jumpPoints.map(point => toPointMarker(segment.id, segment.kind, point)));
  }

  return { stayMarkers, movePolylines, pointMarkers };
}
