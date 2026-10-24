/**
 * 轨迹路径简化（渲染层纯函数）：标准 Douglas-Peucker 算法。
 *
 * 消费格式说明：mobileMapModel.buildMapDisplayModel 产出的 movePolylines[].positions
 * 是 [lat, lng] 二元组二维数组（见 pathPosition），因此本模块直接消费二维数组，
 * 而非 { lat: number; lng: number } 对象形式。顺序：model 层已剔除 jump 点 → 渲染层简化。
 */

export type LatLngTuple = [number, number];

const EARTH_RADIUS_METERS = 6371000;

function toRadians(degrees: number): number {
  return (degrees * Math.PI) / 180;
}

/** haversine 两点球面距离（米）。 */
export function haversineMeters(lat1: number, lng1: number, lat2: number, lng2: number): number {
  const lat1Rad = toRadians(lat1);
  const lat2Rad = toRadians(lat2);
  const deltaLat = toRadians(lat2 - lat1);
  const deltaLng = toRadians(lng2 - lng1);
  const a = Math.sin(deltaLat / 2) ** 2
    + Math.cos(lat1Rad) * Math.cos(lat2Rad) * Math.sin(deltaLng / 2) ** 2;
  return 2 * EARTH_RADIUS_METERS * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

function initialBearingMeters(lat1: number, lng1: number, lat2: number, lng2: number): number {
  const lat1Rad = toRadians(lat1);
  const lat2Rad = toRadians(lat2);
  const deltaLng = toRadians(lng2 - lng1);
  const y = Math.sin(deltaLng) * Math.cos(lat2Rad);
  const x = Math.cos(lat1Rad) * Math.sin(lat2Rad)
    - Math.sin(lat1Rad) * Math.cos(lat2Rad) * Math.cos(deltaLng);
  return Math.atan2(y, x);
}

/** haversine 点到线段 AB 的垂距（米）：跨轨距 + 沿轨距，投影落在线段外时取端点到点距离。 */
export function pointToSegmentMeters(
  lat: number,
  lng: number,
  latA: number,
  lngA: number,
  latB: number,
  lngB: number,
): number {
  const dAB = haversineMeters(latA, lngA, latB, lngB);
  if (dAB < 1e-9) return haversineMeters(lat, lng, latA, lngA);
  const dAP = haversineMeters(latA, lngA, lat, lng);
  if (dAP < 1e-9) return 0;

  let delta = initialBearingMeters(latA, lngA, lat, lng)
    - initialBearingMeters(latA, lngA, latB, lngB);
  while (delta > Math.PI) delta -= 2 * Math.PI;
  while (delta < -Math.PI) delta += 2 * Math.PI;

  // 跨轨距（垂直距离，有向，符号表示偏向左右）。
  const crossTrack = Math.asin(Math.sin(dAP / EARTH_RADIUS_METERS) * Math.sin(delta))
    * EARTH_RADIUS_METERS;
  // 沿轨距（A 到垂足在 AB 大圆上的投影距离）：|delta| <= 90° 时垂足在 A 前方。
  const alongRatio = Math.cos(dAP / EARTH_RADIUS_METERS)
    / Math.cos(crossTrack / EARTH_RADIUS_METERS);
  const alongTrack = Math.acos(Math.min(1, Math.max(-1, alongRatio))) * EARTH_RADIUS_METERS;
  const signedAlong = Math.abs(delta) <= Math.PI / 2 ? alongTrack : -alongTrack;

  if (signedAlong <= 0) return dAP;
  if (signedAlong >= dAB) return haversineMeters(lat, lng, latB, lngB);
  return Math.abs(crossTrack);
}

function simplifySegment(
  points: LatLngTuple[],
  start: number,
  end: number,
  toleranceMeters: number,
  keep: Set<number>,
) {
  let maxDistance = 0;
  let maxIndex = -1;
  const [latA, lngA] = points[start];
  const [latB, lngB] = points[end];
  for (let i = start + 1; i < end; i += 1) {
    const [lat, lng] = points[i];
    const distance = pointToSegmentMeters(lat, lng, latA, lngA, latB, lngB);
    if (distance > maxDistance) {
      maxDistance = distance;
      maxIndex = i;
    }
  }
  if (maxDistance > toleranceMeters && maxIndex !== -1) {
    keep.add(maxIndex);
    simplifySegment(points, start, maxIndex, toleranceMeters, keep);
    simplifySegment(points, maxIndex, end, toleranceMeters, keep);
  }
}

/**
 * Douglas-Peucker 轨迹简化：保留超过 toleranceMeters 垂距的顶点，首尾恒保留，
 * 空 / 单点 / 两点原样返回。默认 15m（GPS 精度中位 8m 量级）。
 */
export function simplifyPath(points: LatLngTuple[], toleranceMeters = 15): LatLngTuple[] {
  if (points.length <= 2) return points.slice();
  const keep = new Set<number>([0, points.length - 1]);
  simplifySegment(points, 0, points.length - 1, toleranceMeters, keep);
  return points.filter((_, index) => keep.has(index));
}
