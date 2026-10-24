import { apiGet, apiPost } from './client';
import type { ApiResponse } from '../types';

// === 浏览器使用（time-tracker-4-browser 插件通道）===

export interface PcBrowserHostSummary {
  host: string;
  alias?: string | null;
  focusMs: number;
  visitCount: number;
}

export interface PcBrowserSummaryResponse {
  from: string;
  to: string;
  totalFocusMs: number;
  totalVisits: number;
  totalRunMs: number;
  totalMediaMs: number;
  siteCount: number;
  topHosts: PcBrowserHostSummary[];
}

export interface PcBrowserDailyItem {
  date: string;
  host: string;
  focusMs: number;
  visitCount: number;
  runMs: number;
  mediaMs: number;
}

export interface PcBrowserTimelineItem {
  host: string;
  startMs: number;
  durationMs: number;
}

export type PcBrowserImportMode = 'overwrite' | 'add';

export interface PcBrowserImportRequest {
  content: string;
  mode: PcBrowserImportMode;
  deviceId?: string;
}

export interface PcBrowserImportResult {
  rows: number;
  dates: number;
  hosts: number;
  skipped: number;
  format: string;
}

export interface PcBrowserRangeQuery {
  date?: string;
  from?: string;
  to?: string;
}

function buildBrowserQuery(params: PcBrowserRangeQuery): string {
  const searchParams = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v !== undefined && v !== null && v !== '') searchParams.set(k, String(v));
  });
  const query = searchParams.toString();
  return query ? `?${query}` : '';
}

export const pcBrowserSiteApiPaths = {
  summary: (params: PcBrowserRangeQuery = {}): string => `/pc/browser-tt/summary${buildBrowserQuery(params)}`,
  daily: (params: PcBrowserRangeQuery = {}): string => `/pc/browser-tt/daily${buildBrowserQuery(params)}`,
  timeline: (date: string): string => `/pc/browser-tt/timeline?date=${date}`,
  import: '/pc/browser-tt/import',
} as const;

export function getPcBrowserSummary(params: PcBrowserRangeQuery = {}) {
  return apiGet<ApiResponse<PcBrowserSummaryResponse>>(pcBrowserSiteApiPaths.summary(params)).then(r => r.data);
}

export function getPcBrowserDaily(params: PcBrowserRangeQuery = {}) {
  return apiGet<ApiResponse<PcBrowserDailyItem[]>>(pcBrowserSiteApiPaths.daily(params)).then(r => r.data);
}

export function getPcBrowserTimeline(date: string) {
  return apiGet<ApiResponse<PcBrowserTimelineItem[]>>(pcBrowserSiteApiPaths.timeline(date)).then(r => r.data);
}

export function importPcBrowserHistory(request: PcBrowserImportRequest) {
  return apiPost<ApiResponse<PcBrowserImportResult>>(pcBrowserSiteApiPaths.import, request).then(r => r.data);
}
