import type { MobileLocationAnalyticsParams } from '../api/mobile';
import type { MobileRangeShortcut } from '../components/mobile/mobileFormatting';
import { buildMobileAnalyticsDateRange } from '../components/mobile/mobileFormatting';

export interface TracksUrlFilters {
  range: MobileRangeShortcut;
  startDate: string;
  endDate: string;
  deviceId: string;
  maxAccuracyMeters: number;
  includeRejected: boolean;
}

const VALID_RANGES: ReadonlySet<string> = new Set(['today', '7d', '30d', 'custom']);
const FILTER_PARAM_KEYS = ['range', 'start', 'end', 'device', 'accuracy', 'rejected'] as const;

function safeRange(value: string | null): MobileRangeShortcut {
  if (value && VALID_RANGES.has(value)) return value as MobileRangeShortcut;
  return '7d';
}

function safeAccuracy(value: string | null): number {
  if (!value) return 50;
  const n = Number(value);
  if (!Number.isFinite(n) || n < 1) return 50;
  return Math.round(n);
}

function safeRejected(value: string | null): boolean {
  return value === '1';
}

function safeDate(value: string | null, fallback: string): string {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return fallback;
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (
    date.getUTCFullYear() !== year
    || date.getUTCMonth() !== month - 1
    || date.getUTCDate() !== day
  ) {
    return fallback;
  }
  return value;
}

export function parseTracksUrlFilters(searchParams: URLSearchParams): TracksUrlFilters {
  const range = safeRange(searchParams.get('range'));
  const defaults = buildMobileAnalyticsDateRange(range);
  const startParam = searchParams.get('start');
  const endParam = searchParams.get('end');
  return {
    range,
    startDate: safeDate(startParam, defaults.startDate),
    endDate: safeDate(endParam, defaults.endDate),
    deviceId: searchParams.get('device') || '',
    maxAccuracyMeters: safeAccuracy(searchParams.get('accuracy')),
    includeRejected: safeRejected(searchParams.get('rejected')),
  };
}

export function serializeTracksUrlFilters(
  filters: TracksUrlFilters,
  baseParams?: URLSearchParams,
): URLSearchParams {
  const sp = baseParams ? new URLSearchParams(baseParams) : new URLSearchParams();
  for (const key of FILTER_PARAM_KEYS) {
    sp.delete(key);
  }
  sp.set('range', filters.range);
  sp.set('start', filters.startDate);
  sp.set('end', filters.endDate);
  if (filters.deviceId) sp.set('device', filters.deviceId);
  if (filters.maxAccuracyMeters !== 50) sp.set('accuracy', String(filters.maxAccuracyMeters));
  if (filters.includeRejected) sp.set('rejected', '1');
  return sp;
}

export function tracksUrlFiltersToParams(filters: TracksUrlFilters): MobileLocationAnalyticsParams {
  return {
    deviceId: filters.deviceId || undefined,
    maxAccuracyMeters: filters.maxAccuracyMeters,
    includeRejected: filters.includeRejected,
  };
}

export function canAdvanceRawPointPage(input: {
  hasMore: boolean;
  nextCursor: string | null | undefined;
}): boolean {
  return Boolean(input.hasMore && input.nextCursor);
}

export function advanceRawPointCursorStack(input: {
  cursorStack: readonly string[];
  pageIndex: number;
  hasMore: boolean;
  nextCursor: string | null | undefined;
}): { didAdvance: boolean; nextPageIndex: number; cursorStack: string[] } {
  const cursorStack = [...input.cursorStack];
  if (!canAdvanceRawPointPage({ hasMore: input.hasMore, nextCursor: input.nextCursor })) {
    return {
      didAdvance: false,
      nextPageIndex: input.pageIndex,
      cursorStack,
    };
  }
  cursorStack[input.pageIndex] = input.nextCursor as string;
  return {
    didAdvance: true,
    nextPageIndex: input.pageIndex + 1,
    cursorStack,
  };
}
