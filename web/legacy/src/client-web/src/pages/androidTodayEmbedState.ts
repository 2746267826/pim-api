import type {
  MobileLocationAnalyticsOverview,
  MobileLocationTrack,
  MobileAnalyticsOverview,
  MobileSummary,
} from '../api/mobile';

export const NATIVE_STATE_REFRESH_INTERVAL_MS = 30_000;

export function hasRealData(
  locationOverview: MobileLocationAnalyticsOverview | null | undefined,
  tracks: MobileLocationTrack[] | null | undefined,
  usageOverview: MobileAnalyticsOverview | null | undefined,
  summary: MobileSummary | null | undefined,
): boolean {
  return (
    (locationOverview?.pointCount ?? 0) > 0 ||
    (tracks?.length ?? 0) > 0 ||
    (usageOverview?.totalForegroundSeconds ?? 0) > 0 ||
    (summary?.appRanking?.length ?? 0) > 0 ||
    (summary?.totalForegroundSeconds ?? 0) > 0
  );
}

export function latestGeneratedAt(
  items: Array<{ generatedAt?: string } | null | undefined>,
): string | null {
  const dates = items
    .filter((i): i is { generatedAt: string } => !!i?.generatedAt)
    .map(i => new Date(i.generatedAt).getTime())
    .filter(t => !Number.isNaN(t));
  if (dates.length === 0) return null;
  return new Date(Math.max(...dates)).toISOString();
}

export interface GeneratedAtEntry {
  label: string;
  generatedAt: string | null;
}

export function generatedAtEntries(
  locationOverview: MobileLocationAnalyticsOverview | null | undefined,
  usageOverview: MobileAnalyticsOverview | null | undefined,
  summary: MobileSummary | null | undefined,
): GeneratedAtEntry[] {
  const entries: GeneratedAtEntry[] = [];

  if (locationOverview?.generatedAt) {
    entries.push({ label: '位置概况', generatedAt: locationOverview.generatedAt });
  }
  if (usageOverview?.generatedAt) {
    entries.push({ label: '手机使用', generatedAt: usageOverview.generatedAt });
  }
  if (summary?.generatedAt) {
    entries.push({ label: 'App 摘要', generatedAt: summary.generatedAt });
  }

  return entries;
}

export interface PageReportInput {
  locationOverview?: MobileLocationAnalyticsOverview | null;
  tracks?: MobileLocationTrack[] | null;
  usageOverview?: MobileAnalyticsOverview | null;
  summary?: MobileSummary | null;
  locationError?: Error | null;
  tracksError?: Error | null;
  usageError?: Error | null;
  summaryError?: Error | null;
}

export interface PageReport {
  hasServerData: boolean;
  generatedAt: string | null;
  error: string | null;
}

export function buildPageReport(input: PageReportInput): PageReport {
  const errors: string[] = [];
  if (input.locationError) errors.push('位置数据获取失败');
  if (input.tracksError) errors.push('轨迹数据获取失败');
  if (input.usageError) errors.push('使用数据获取失败');
  if (input.summaryError) errors.push('摘要数据获取失败');

  const data = hasRealData(
    input.locationOverview,
    input.tracks,
    input.usageOverview,
    input.summary,
  );

  const gen = latestGeneratedAt([
    input.locationOverview,
    input.usageOverview,
    input.summary,
  ]);

  return {
    hasServerData: data,
    generatedAt: gen,
    error: errors.length > 0 ? errors.join('；') : null,
  };
}

export function formatNativeBoolean(value: boolean | null | undefined): string {
  if (value === true) return '已开启';
  if (value === false) return '已关闭';
  return '暂无';
}

export function formatNativeField(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') return '暂无';
  return value;
}

export function staleStatusLabel(isStale: boolean | undefined | null): string | null {
  if (isStale === true) return '可能过期';
  return null;
}

export function shouldShowSummaryMetricsFallback(
  summary: MobileSummary | null | undefined,
  usageOverview: MobileAnalyticsOverview | null | undefined,
): boolean {
  const usageHasData = (usageOverview?.totalForegroundSeconds ?? 0) > 0;
  const summaryHasDuration = (summary?.totalForegroundSeconds ?? 0) > 0;
  return !usageHasData && summaryHasDuration;
}

export function nativeErrorMessage(_error: unknown): string {
  void _error;
  return '无法读取原生采集状态';
}
