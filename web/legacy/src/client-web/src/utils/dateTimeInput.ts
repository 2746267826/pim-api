import { DateTime } from 'luxon';

const FORMAT = "yyyy-MM-dd'T'HH:mm";

const WINDOWS_TO_IANA: Record<string, string> = {
  'China Standard Time': 'Asia/Shanghai',
  'Taipei Standard Time': 'Asia/Taipei',
  'Tokyo Standard Time': 'Asia/Tokyo',
  'Korea Standard Time': 'Asia/Seoul',
  'Singapore Standard Time': 'Asia/Singapore',
  'Hong Kong Standard Time': 'Asia/Hong_Kong',
  'Malay Peninsula Standard Time': 'Asia/Kuala_Lumpur',
  'India Standard Time': 'Asia/Kolkata',
  'W. Europe Standard Time': 'Europe/Berlin',
  'Central Europe Standard Time': 'Europe/Budapest',
  'Eastern Standard Time': 'America/New_York',
  'Central Standard Time': 'America/Chicago',
  'Mountain Standard Time': 'America/Denver',
  'Pacific Standard Time': 'America/Los_Angeles',
  'UTC': 'Etc/UTC',
};

export function normalizeZoneId(timeZoneId: string | null | undefined): string | null | undefined {
  if (!timeZoneId) return timeZoneId;
  return WINDOWS_TO_IANA[timeZoneId] ?? timeZoneId;
}

function resolveZone(timeZoneId?: string | null): string {
  const normalized = normalizeZoneId(timeZoneId);
  if (normalized && DateTime.local().setZone(normalized).isValid) return normalized;
  return DateTime.local().zoneName;
}

export function isoToDatetimeLocal(iso: string, timeZoneId?: string): string {
  if (!iso) return '';
  const dt = DateTime.fromISO(iso);
  if (!dt.isValid) return '';
  const zoned = dt.setZone(resolveZone(timeZoneId));
  if (!zoned.isValid) return '';
  return zoned.toFormat(FORMAT);
}

export function datetimeLocalToUtcIso(local: string, timeZoneId?: string): string {
  if (!local) return '';
  const dt = DateTime.fromFormat(local, FORMAT, { zone: resolveZone(timeZoneId) });
  if (!dt.isValid) return '';
  return dt.toUTC().toFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'");
}

export function minimumEndValue(startValue: string): string {
  if (!startValue) return '';
  const dt = DateTime.fromFormat(startValue, FORMAT);
  if (!dt.isValid) return '';
  return dt.plus({ minutes: 1 }).toFormat(FORMAT);
}

export function isEndAfterStart(startValue: string, endValue: string): boolean {
  if (!startValue || !endValue) return false;
  const start = DateTime.fromFormat(startValue, FORMAT);
  const end = DateTime.fromFormat(endValue, FORMAT);
  if (!start.isValid || !end.isValid) return false;
  return end > start;
}
