/**
 * 历史位置地图增强的纯函数（渲染层）：常去地点精度圈数据与移动统计条数据。
 * 不放图表组件目录：二者都只产出数据，由 components/mobile 下的视图消费。
 */
import type {
  MobileFrequentPlace,
  MobileMovementStatsResponse,
} from '../../api/mobile';
import { chartColors } from '../charts/chartColors';

export interface FrequentPlaceCircle {
  center: [number, number];
  radiusMeters: number;
  pointCount: number;
  visitDayCount: number;
  isHome: boolean;
  color: string;
}

/** 常去地点 → 地图 Circle 数据：家（isHome）用主色 primary，其余用 activity 色。 */
export function buildFrequentPlaceCircles(places: MobileFrequentPlace[]): FrequentPlaceCircle[] {
  return places.map(place => ({
    center: [place.centerLatitude, place.centerLongitude],
    radiusMeters: place.radiusMeters,
    pointCount: place.pointCount,
    visitDayCount: place.visitDayCount,
    isHome: place.isHome,
    color: place.isHome ? chartColors.primary : chartColors.activity,
  }));
}

export interface MovementMetricItem {
  label: string;
  value: string;
}

function formatOutingDuration(seconds: number): string {
  const totalMinutes = Math.round(seconds / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return `${hours} 小时 ${minutes} 分`;
}

/**
 * 移动统计四格数据：出门次数 / 外出时长（X 小时 Y 分）/ 移动里程（X.X km）/ 速度峰值（X.X m/s）。
 * stats 为空或速度峰值为 null 时对应格显示「—」。
 */
export function buildMovementMetricStrip(
  stats: MobileMovementStatsResponse | null | undefined,
): MovementMetricItem[] {
  if (!stats) {
    return [
      { label: '出门次数', value: '—' },
      { label: '外出时长', value: '—' },
      { label: '移动里程', value: '—' },
      { label: '速度峰值', value: '—' },
    ];
  }
  return [
    { label: '出门次数', value: String(stats.outingCount) },
    { label: '外出时长', value: formatOutingDuration(stats.outingSeconds) },
    { label: '移动里程', value: `${(stats.distanceMeters / 1000).toFixed(1)} km` },
    {
      label: '速度峰值',
      value: stats.maxSpeedMetersPerSecond == null
        ? '—'
        : `${stats.maxSpeedMetersPerSecond.toFixed(1)} m/s`,
    },
  ];
}
