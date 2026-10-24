import type { MobileLifeCategory } from '../../api/mobile';
import { MOBILE_DEFAULT_TIMEZONE } from '../../api/mobile';
import type { PimHealthStatus } from '../../types';

export type MobileRangeShortcut = 'today' | '7d' | '30d' | 'custom';

export interface MobileAnalyticsDateRange {
  shortcut: MobileRangeShortcut;
  startDate: string;
  endDate: string;
}

export interface MobileAnalyticsUtcRange {
  rangeStartUtc: string;
  rangeEndUtc: string;
  timezone: string;
}

const SHANGHAI_UTC_OFFSET = '+08:00';
const DAY_MS = 24 * 60 * 60 * 1000;

function pad2(value: number) {
  return String(value).padStart(2, '0');
}

function dateInputFromUtcDate(date: Date) {
  return `${date.getUTCFullYear()}-${pad2(date.getUTCMonth() + 1)}-${pad2(date.getUTCDate())}`;
}

function parseShanghaiDate(value: string) {
  return new Date(`${value}T00:00:00${SHANGHAI_UTC_OFFSET}`);
}

export function formatShanghaiDateInput(now = new Date()) {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: MOBILE_DEFAULT_TIMEZONE,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now);
  const year = parts.find(part => part.type === 'year')?.value;
  const month = parts.find(part => part.type === 'month')?.value;
  const day = parts.find(part => part.type === 'day')?.value;
  return year && month && day ? `${year}-${month}-${day}` : dateInputFromUtcDate(now);
}

export function addShanghaiDays(dateInput: string, days: number) {
  return formatShanghaiDateInput(new Date(parseShanghaiDate(dateInput).getTime() + days * DAY_MS));
}

export function buildMobileAnalyticsDateRange(
  shortcut: MobileRangeShortcut = '7d',
  now = new Date(),
): MobileAnalyticsDateRange {
  const today = formatShanghaiDateInput(now);
  if (shortcut === 'today') return { shortcut, startDate: today, endDate: today };
  if (shortcut === '30d') return { shortcut, startDate: addShanghaiDays(today, -29), endDate: today };
  return { shortcut: shortcut === 'custom' ? 'custom' : '7d', startDate: addShanghaiDays(today, -6), endDate: today };
}

export function toMobileAnalyticsUtcRange(range: Pick<MobileAnalyticsDateRange, 'startDate' | 'endDate'>): MobileAnalyticsUtcRange {
  const startDate = range.startDate <= range.endDate ? range.startDate : range.endDate;
  const endDate = range.endDate >= range.startDate ? range.endDate : range.startDate;
  return {
    rangeStartUtc: parseShanghaiDate(startDate).toISOString(),
    rangeEndUtc: parseShanghaiDate(addShanghaiDays(endDate, 1)).toISOString(),
    timezone: MOBILE_DEFAULT_TIMEZONE,
  };
}

export function formatDuration(seconds: number | null | undefined) {
  const safeSeconds = Math.max(0, Math.round(seconds ?? 0));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  if (hours > 0) return minutes > 0 ? `${hours}小时${minutes}分钟` : `${hours}小时`;
  if (minutes > 0) return `${minutes}分钟`;
  return `${safeSeconds}秒`;
}

export function formatCompactDuration(seconds: number | null | undefined) {
  const safeSeconds = Math.max(0, Math.round(seconds ?? 0));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  if (hours > 0) return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
  if (minutes > 0) return `${minutes}m`;
  return `${safeSeconds}s`;
}

export function formatPercent(value: number | null | undefined) {
  if (value === null || value === undefined || !Number.isFinite(value)) return '0%';
  return `${Math.round(value * 100)}%`;
}

export function formatSignedPercent(value: number | null | undefined) {
  if (value === null || value === undefined || Number.isNaN(value)) return '-';
  const percent = Math.round(value * 100);
  return `${percent > 0 ? '+' : ''}${percent}%`;
}

export function formatNumber(value: number | null | undefined) {
  return Math.round(value ?? 0).toLocaleString('zh-CN');
}

export function formatDateTime(value: string | null | undefined) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { timeZone: MOBILE_DEFAULT_TIMEZONE, hour12: false });
}

export function formatShortTime(value: string | null | undefined) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleTimeString('zh-CN', {
    timeZone: MOBILE_DEFAULT_TIMEZONE,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

export function formatLocalDate(value: string | null | undefined) {
  if (!value) return '-';
  const date = new Date(`${value}T00:00:00${SHANGHAI_UTC_OFFSET}`);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString('zh-CN', {
    timeZone: MOBILE_DEFAULT_TIMEZONE,
    month: 'numeric',
    day: 'numeric',
  });
}

export function formatCategoryLabel(category: MobileLifeCategory | string | null | undefined) {
  return category || '未分类';
}

export function sourceLabel(source: string | null | undefined) {
  if (source === 'events') return '事件明细';
  if (source === 'fallback') return '回退汇总';
  return source || '未知来源';
}

export function statusLabel(status: string | null | undefined) {
  const normalized = status?.trim().toLowerCase();
  if (normalized === 'succeeded' || normalized === 'success' || normalized === 'completed') return '已完成';
  if (normalized === 'partial' || normalized === 'warning') return '部分接受';
  if (normalized === 'failed' || normalized === 'error') return '失败';
  if (normalized === 'pending') return '等待中';
  return status || '未知';
}

export function healthStatusLabel(status: PimHealthStatus | string | null | undefined) {
  if (status === 'Healthy') return '正常';
  if (status === 'Warning') return '需要关注';
  if (status === 'Critical') return '严重';
  if (status === 'Info') return '提示';
  return '未知';
}

export function healthToneClass(status: PimHealthStatus | string | null | undefined) {
  if (status === 'Healthy') return 'border-teal-200 bg-teal-50 text-teal-700';
  if (status === 'Warning') return 'border-amber-200 bg-amber-50 text-amber-800';
  if (status === 'Critical') return 'border-red-200 bg-red-50 text-red-700';
  if (status === 'Info') return 'border-blue-200 bg-blue-50 text-blue-700';
  return 'border-slate-200 bg-slate-50 text-slate-600';
}
