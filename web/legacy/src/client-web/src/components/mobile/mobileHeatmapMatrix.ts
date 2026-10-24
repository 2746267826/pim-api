import type { MobileHeatmapBucket } from '../../api/mobile';

export interface HeatmapCategorySlice {
  lifeCategory: string;
  foregroundSeconds: number;
}

export interface HeatmapMatrixCell {
  localDate: string;
  localHour: number;
  bucketStartUtc: string | null;
  bucketEndUtc: string | null;
  foregroundSeconds: number;
  qualityFlags: string[];
  categories: HeatmapCategorySlice[];
  sourceBuckets: MobileHeatmapBucket[];
}

export interface HeatmapMatrixDay {
  localDate: string;
  label: string;
  cells: HeatmapMatrixCell[];
}

export interface HeatmapMatrix {
  hours: number[];
  days: HeatmapMatrixDay[];
  maxSeconds: number;
}

function localTodayKey() {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date());
  const year = parts.find(part => part.type === 'year')?.value;
  const month = parts.find(part => part.type === 'month')?.value;
  const day = parts.find(part => part.type === 'day')?.value;
  return year && month && day ? `${year}-${month}-${day}` : '';
}

function dateLabel(localDate: string) {
  const [year, month, day] = localDate.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  const weekdays = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
  return `${month}月${day}日 ${localDate === localTodayKey() ? '今天' : weekdays[date.getUTCDay()]}`;
}

function emptyCell(localDate: string, localHour: number): HeatmapMatrixCell {
  return {
    localDate,
    localHour,
    bucketStartUtc: null,
    bucketEndUtc: null,
    foregroundSeconds: 0,
    qualityFlags: [],
    categories: [],
    sourceBuckets: [],
  };
}

export function buildHeatmapMatrix(buckets: MobileHeatmapBucket[]): HeatmapMatrix {
  const byDate = new Map<string, HeatmapMatrixCell[]>();

  for (const bucket of buckets) {
    if (bucket.localHour < 0 || bucket.localHour > 23) continue;
    if (!byDate.has(bucket.localDate)) {
      byDate.set(bucket.localDate, Array.from({ length: 24 }, (_, hour) => emptyCell(bucket.localDate, hour)));
    }

    const cell = byDate.get(bucket.localDate)![bucket.localHour];
    cell.bucketStartUtc = cell.bucketStartUtc ?? bucket.bucketStartUtc;
    cell.bucketEndUtc = bucket.bucketEndUtc;
    cell.foregroundSeconds += bucket.foregroundSeconds;
    cell.sourceBuckets.push(bucket);

    for (const flag of bucket.qualityFlags) {
      if (!cell.qualityFlags.includes(flag)) cell.qualityFlags.push(flag);
    }

    const existing = cell.categories.find(item => item.lifeCategory === bucket.lifeCategory);
    if (existing) existing.foregroundSeconds += bucket.foregroundSeconds;
    else cell.categories.push({ lifeCategory: bucket.lifeCategory, foregroundSeconds: bucket.foregroundSeconds });
    cell.categories.sort((a, b) => b.foregroundSeconds - a.foregroundSeconds);
  }

  const days = [...byDate.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([localDate, cells]) => ({ localDate, label: dateLabel(localDate), cells }));

  return {
    hours: Array.from({ length: 24 }, (_, hour) => hour),
    days,
    maxSeconds: Math.max(1, ...days.flatMap(day => day.cells.map(cell => cell.foregroundSeconds))),
  };
}
