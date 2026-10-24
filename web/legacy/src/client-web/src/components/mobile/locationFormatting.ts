export function formatAccuracyLabel(value: number | null | undefined) {
  if (value === null || value === undefined || Number.isNaN(value)) return '-';
  const rounded = Math.round(value * 10) / 10;
  return `${Number.isInteger(rounded) ? rounded.toFixed(0) : rounded.toFixed(1)} m`;
}

export function formatCoordinate(latitude: number, longitude: number) {
  return `${latitude.toFixed(6)}, ${longitude.toFixed(6)}`;
}

export function providerLabel(provider: string | null | undefined) {
  const normalized = provider?.trim().toLowerCase();
  if (normalized === 'gps') return 'GPS';
  if (normalized === 'network') return '网络定位';
  if (normalized === 'fused') return '融合定位';
  if (normalized === 'passive') return '被动定位';
  return provider || '未知';
}

export function sourceKindLabel(source: string | null | undefined) {
  const normalized = source?.trim().toLowerCase();
  if (normalized === 'android') return 'Android';
  if (normalized === 'manual') return '手动';
  if (normalized === 'auto') return '自动';
  return source || '未知';
}

export function locationQualityLabel(quality: string | null | undefined) {
  const normalized = quality?.trim().toLowerCase();
  if (normalized === 'high') return '可信';
  if (normalized === 'usable') return '可用';
  if (normalized === 'review') return '需复核';
  if (normalized === 'rejected') return '已拒绝';
  return quality || '未知';
}

export function formatDistanceMeters(meters: number | null | undefined) {
  const value = Math.max(0, meters ?? 0);
  if (value >= 1000) return `${(value / 1000).toFixed(1)} km`;
  return `${Math.round(value)} m`;
}

export function formatSpeedMetersPerSecond(value: number | null | undefined) {
  const safeValue = Math.max(0, value ?? 0);
  return `${(safeValue * 3.6).toFixed(1)} km/h`;
}

export function formatDurationSeconds(seconds: number | null | undefined) {
  const safeSeconds = Math.max(0, Math.round(seconds ?? 0));
  const days = Math.floor(safeSeconds / 86400);
  const hours = Math.floor((safeSeconds % 86400) / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  if (days > 0) return hours > 0 ? `${days}天${hours}小时` : `${days}天`;
  if (hours > 0) return minutes > 0 ? `${hours}小时${minutes}分钟` : `${hours}小时`;
  if (minutes > 0) return `${minutes}分钟`;
  return `${safeSeconds}秒`;
}

export function segmentKindLabel(kind: string | null | undefined) {
  const normalized = kind?.trim().toLowerCase();
  if (normalized === 'move') return '移动';
  if (normalized === 'stay') return '停留';
  if (normalized === 'gap') return '缺口';
  if (normalized === 'low-confidence') return '低可信';
  return kind || '未知';
}

export function qualityFlagLabel(flag: string | null | undefined) {
  const normalized = flag?.trim().toLowerCase();
  if (normalized === 'low-accuracy-cluster') return '低精度聚集';
  if (normalized === 'rejected-points') return '包含拒绝点';
  if (normalized === 'large-gap') return '存在时间缺口';
  if (normalized === 'single-point') return '单点片段';
  if (normalized === 'jump-point') return '跳点';
  if (normalized === 'low-accuracy') return '低精度';
  if (normalized === 'no-usable-points') return '无可用点';
  return flag || '正常';
}
