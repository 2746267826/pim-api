import { DateTime } from 'luxon';
import { normalizeZoneId } from './dateTimeInput';
import { htmlToTextSummary, looksLikeHtml } from './safeHtml';

export const EVENT_FIELD_LABELS: Record<string, string> = {
  title: '标题',
  description: '描述',
  descriptionFormat: '描述格式',
  location: '地点',
  dtStart: '开始时间',
  dtEnd: '结束时间',
  isAllDay: '全天事件',
  timeZoneId: '时区',
  showAs: '显示状态',
  importance: '重要性',
  sensitivity: '敏感度',
  categories: '分类',
  isReminderOn: '提醒',
  reminderMinutesBeforeStart: '提醒提前量',
  organizer: '组织者',
  attendees: '参会者',
  isOnlineMeeting: '在线会议',
  onlineMeetingProvider: '会议提供方',
  onlineMeetingUrl: '会议链接',
  externalLink: '外部链接',
  attachmentReferences: '附件',
};

const FIELD_KEYS = Object.keys(EVENT_FIELD_LABELS);

export type EventFieldDiffKind = 'added' | 'removed' | 'modified';

export interface EventFieldDiffEntry {
  key: string;
  label: string;
  kind: EventFieldDiffKind;
  before: unknown;
  after: unknown;
}

export type EventFieldDiffInput = Record<string, unknown>;

function isEmptyValue(value: unknown): boolean {
  if (value === null || value === undefined || value === '') return true;
  if (Array.isArray(value)) return value.length === 0;
  if (typeof value === 'object') return Object.keys(value as object).length === 0;
  return false;
}

function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    return a.every((item, index) => deepEqual(item, b[index]));
  }
  if (a !== null && b !== null && typeof a === 'object' && typeof b === 'object') {
    const aKeys = Object.keys(a as object);
    const bKeys = Object.keys(b as object);
    if (aKeys.length !== bKeys.length) return false;
    return aKeys.every((key) =>
      deepEqual((a as Record<string, unknown>)[key], (b as Record<string, unknown>)[key])
    );
  }
  return false;
}

function normalizeFieldValue(record: EventFieldDiffInput, key: string, value: unknown): unknown {
  if (key !== 'description' || typeof value !== 'string') return value;
  if (value.trim() === '') return '';
  const format = typeof record.descriptionFormat === 'string'
    ? record.descriptionFormat.trim().toLowerCase()
    : '';
  if (format === 'plain') return value;
  if (format === 'html' || looksLikeHtml(value)) return htmlToTextSummary(value);
  return value;
}

const DATETIME_KEYS = new Set(['dtStart', 'dtEnd']);

const EXPLICIT_OFFSET_RE = /(?:Z|z|[+-]\d{2}:?\d{2})$/;

function hasExplicitOffset(value: string): boolean {
  return EXPLICIT_OFFSET_RE.test(value);
}

function recordZone(record: EventFieldDiffInput): string | null {
  const zone = normalizeZoneId(
    typeof record.timeZoneId === 'string' ? record.timeZoneId : null,
  );
  if (typeof zone !== 'string' || !zone) return null;
  return DateTime.local().setZone(zone).isValid ? zone : null;
}

function naiveInstant(value: string, zone: string | null): DateTime | null {
  if (!zone) return null;
  const dt = DateTime.fromISO(value, { zone });
  return dt.isValid ? dt : null;
}

function sameMoment(
  before: EventFieldDiffInput,
  after: EventFieldDiffInput,
  a: unknown,
  b: unknown,
): boolean {
  if (typeof a !== 'string' || typeof b !== 'string' || !a || !b) return false;
  const explicitA = hasExplicitOffset(a);
  const explicitB = hasExplicitOffset(b);
  // Explicit offsets always compare as instants; a naive value can match an
  // explicit value only through its own record's normalized timeZoneId.
  if (explicitA && explicitB) {
    const dtA = DateTime.fromISO(a);
    const dtB = DateTime.fromISO(b);
    return dtA.isValid && dtB.isValid && dtA.toMillis() === dtB.toMillis();
  }
  if (explicitA !== explicitB) {
    const explicit = explicitA ? a : b;
    const naive = explicitA ? b : a;
    const zone = explicitA ? after : before;
    const dtExplicit = DateTime.fromISO(explicit);
    const dtNaive = naiveInstant(naive, recordZone(zone));
    return dtExplicit.isValid && dtNaive !== null && dtExplicit.toMillis() === dtNaive.toMillis();
  }
  // Both values are naive: interpret each in its own record zone when it is
  // valid, otherwise fall back to literal equality. Never use the host zone.
  const dtA = naiveInstant(a, recordZone(before));
  const dtB = naiveInstant(b, recordZone(after));
  if (dtA === null || dtB === null) return a === b;
  return dtA.toMillis() === dtB.toMillis();
}

// Writeback drafts always carry explicit false for toggle fields while the
// stored event may omit them; false and unset are equivalent for diffing.
function togglesEquivalent(a: unknown, b: unknown): boolean {
  return (a === undefined || a === null || a === false)
    && (b === undefined || b === null || b === false);
}

export function diffEventFields(
  before: EventFieldDiffInput,
  after: EventFieldDiffInput,
): EventFieldDiffEntry[] {
  const entries: EventFieldDiffEntry[] = [];
  for (const key of FIELD_KEYS) {
    const beforeNormalized = normalizeFieldValue(before, key, before[key]);
    const afterNormalized = normalizeFieldValue(after, key, after[key]);
    const beforeValue = isEmptyValue(beforeNormalized) ? undefined : beforeNormalized;
    const afterValue = isEmptyValue(afterNormalized) ? undefined : afterNormalized;
    if (beforeValue === undefined && afterValue === undefined) continue;
    if (togglesEquivalent(beforeValue, afterValue)) continue;
    if (DATETIME_KEYS.has(key) && sameMoment(before, after, beforeValue, afterValue)) continue;
    if (deepEqual(beforeValue, afterValue)) continue;
    let kind: EventFieldDiffKind;
    if (beforeValue === undefined) kind = 'added';
    else if (afterValue === undefined) kind = 'removed';
    else kind = 'modified';
    entries.push({ key, label: EVENT_FIELD_LABELS[key], kind, before: beforeValue, after: afterValue });
  }
  return entries;
}

/**
 * Keys that identify internal provider metadata and must never surface as
 * diff rows or preview content. Matches case/camel/Pascal variants such as
 * changeKey, ChangeKey, change_key, OutlookEtag, @odata.etag, iCalUId,
 * recurrenceId and sourceIcsComponent.
 */
export const LEGACY_SENSITIVE_KEY_PATTERN = /(metadata|raw|body|header|secret|token|password|etag|change[_-]?key|outlook.*id|graph|ical[-_]?uid|recurrence[-_]?id|source[-_]?ics[-_]?component)/i;

// Minimal deterministic canonical mapping from PascalCase / provider casing
// variants to the canonical business keys used by the typed diff.
const LEGACY_KEY_ALIASES: Record<string, string> = {
  Title: 'title',
  Subject: 'title',
  subject: 'title',
  Description: 'description',
  Location: 'location',
  Start: 'dtStart',
  DtStart: 'dtStart',
  dtStart: 'dtStart',
  StartsAt: 'dtStart',
  startsAt: 'dtStart',
  End: 'dtEnd',
  DtEnd: 'dtEnd',
  dtEnd: 'dtEnd',
  EndsAt: 'dtEnd',
  endsAt: 'dtEnd',
  AllDay: 'isAllDay',
  IsAllDay: 'isAllDay',
  TimeZoneId: 'timeZoneId',
  Timezone: 'timeZoneId',
  ShowAs: 'showAs',
  Importance: 'importance',
  Sensitivity: 'sensitivity',
  Categories: 'categories',
  IsReminderOn: 'isReminderOn',
  ReminderMinutesBeforeStart: 'reminderMinutesBeforeStart',
  Organizer: 'organizer',
  Attendees: 'attendees',
  IsOnlineMeeting: 'isOnlineMeeting',
  OnlineMeetingProvider: 'onlineMeetingProvider',
  OnlineMeetingUrl: 'onlineMeetingUrl',
  ExternalLink: 'externalLink',
  AttachmentReferences: 'attachmentReferences',
  DescriptionFormat: 'descriptionFormat',
};

/**
 * Normalizes a legacy snapshot record for safe generic diffing: sensitive
 * provider keys are filtered before normalization and again after mapping,
 * and known PascalCase/casing variants collapse onto canonical business keys.
 */
export function canonicalizeLegacyRecord(record: EventFieldDiffInput): EventFieldDiffInput {
  const result: EventFieldDiffInput = {};
  for (const [key, value] of Object.entries(record)) {
    if (LEGACY_SENSITIVE_KEY_PATTERN.test(key)) continue;
    const canonical = LEGACY_KEY_ALIASES[key] ?? key;
    if (LEGACY_SENSITIVE_KEY_PATTERN.test(canonical)) continue;
    result[canonical] = value;
  }
  return result;
}

/**
 * Diffs a generic record such as an audit snapshot. Legacy records are
 * canonicalized first: internal metadata keys are filtered before and after
 * normalization, and PascalCase/casing variants merge onto canonical business
 * keys. Outlook/event writeback uses diffEventFields so unknown provider
 * metadata never enters that path.
 */
export function diffGenericFields(
  before: EventFieldDiffInput,
  after: EventFieldDiffInput,
): EventFieldDiffEntry[] {
  const canonicalBefore = canonicalizeLegacyRecord(before);
  const canonicalAfter = canonicalizeLegacyRecord(after);
  const entries: EventFieldDiffEntry[] = [];
  const keys = [...new Set([...Object.keys(canonicalBefore), ...Object.keys(canonicalAfter)])];
  for (const key of keys) {
    const beforeValue = isEmptyValue(canonicalBefore[key]) ? undefined : canonicalBefore[key];
    const afterValue = isEmptyValue(canonicalAfter[key]) ? undefined : canonicalAfter[key];
    if (beforeValue === undefined && afterValue === undefined) continue;
    if (deepEqual(beforeValue, afterValue)) continue;
    const kind: EventFieldDiffKind = beforeValue === undefined
      ? 'added'
      : afterValue === undefined
        ? 'removed'
        : 'modified';
    entries.push({
      key,
      label: EVENT_FIELD_LABELS[key] ?? key,
      kind,
      before: beforeValue,
      after: afterValue,
    });
  }
  return entries;
}

const MAX_VALUE_LENGTH = 120;

const ATTENDEE_TYPE_LABELS: Record<string, string> = {
  required: '必须',
  optional: '可选',
  resource: '资源',
};

function personText(person: { name?: string | null; email?: string | null }): string {
  return [person.name, person.email].filter(Boolean).join(' ');
}

function safeJoin(parts: Array<string | undefined>): string {
  return parts.filter(part => part && part !== '—').join('、');
}

const HIDDEN_STRUCTURED_DATA = '（结构化数据已隐藏）';

export function formatFieldValue(value: unknown, key: string): string {
  if (value === undefined || value === null || value === '') return '—';
  if (typeof value === 'boolean') return value ? '是' : '否';
  if (typeof value === 'number') return String(value);
  if (typeof value === 'string') return safeExternalEffectText(value);
  if (Array.isArray(value)) {
    if (key === 'attendees') {
      return safeJoin(value.map(item => {
        if (!item || typeof item !== 'object') return undefined;
        const attendee = item as { name?: string | null; email?: string | null; type?: string };
        const type = attendee.type ? `（${ATTENDEE_TYPE_LABELS[attendee.type] ?? attendee.type}）` : '';
        const text = personText(attendee);
        return text ? `${text}${type}` : undefined;
      }));
    }
    if (key === 'attachmentReferences') {
      return safeJoin(value.map(item => {
        if (!item || typeof item !== 'object') return undefined;
        return (item as { name?: string | null }).name || undefined;
      }));
    }
    if (!value.every(item => typeof item === 'string')) return HIDDEN_STRUCTURED_DATA;
    return safeJoin(value as string[]);
  }
  if (typeof value === 'object') {
    if (key === 'organizer') {
      const text = personText(value as { name?: string | null; email?: string | null });
      return text || '—';
    }
    return HIDDEN_STRUCTURED_DATA;
  }
  return '—';
}

export function summarizeEventFields(record: EventFieldDiffInput): EventFieldDiffEntry[] {
  const entries: EventFieldDiffEntry[] = [];
  for (const key of FIELD_KEYS) {
    const normalized = normalizeFieldValue(record, key, record[key]);
    if (isEmptyValue(normalized)) continue;
    entries.push({
      key,
      label: EVENT_FIELD_LABELS[key],
      kind: 'modified',
      before: undefined,
      after: normalized,
    });
  }
  return entries;
}

export function toDiffRecord(source: Record<string, unknown>): EventFieldDiffInput {
  const result: EventFieldDiffInput = {};
  for (const key of FIELD_KEYS) {
    if (source[key] !== undefined) result[key] = source[key];
  }
  return result;
}

/**
 * Safely renders a confirmation/audit changedFields list: only canonical
 * business keys with Chinese labels survive, sensitive provider keys and
 * unknown keys are dropped, and duplicates collapse in input order.
 */
export function safeChangedFields(
  changedFields: string[] | null | undefined,
): Array<{ key: string; label: string }> {
  if (!Array.isArray(changedFields)) return [];
  const seen = new Set<string>();
  const result: Array<{ key: string; label: string }> = [];
  for (const raw of changedFields) {
    if (typeof raw !== 'string' || raw === '') continue;
    if (LEGACY_SENSITIVE_KEY_PATTERN.test(raw)) continue;
    const canonical = LEGACY_KEY_ALIASES[raw] ?? raw;
    if (LEGACY_SENSITIVE_KEY_PATTERN.test(canonical)) continue;
    const label = EVENT_FIELD_LABELS[canonical];
    if (label === undefined) continue;
    if (seen.has(canonical)) continue;
    seen.add(canonical);
    result.push({ key: canonical, label });
  }
  return result;
}

const SAFE_EXTERNAL_EFFECT_ID = '（外部标识已隐藏）';
const SAFE_EXTERNAL_EFFECT_URL = '（外部链接已隐藏）';

// Explicit provider-material patterns only; never a broad id matcher.
const EXTERNAL_URL_RE = /https?:\/\/\S+/i;
const GRAPH_EVENT_ID_RE = /\bgraph-[a-z0-9_-]+\b/i;
const GRAPH_EVENT_ID_AAMK_RE = /\bAAMk[A-Za-z0-9+/=_-]{6,}/i;
const CHANGE_KEY_RE = /\bck-[a-z0-9_-]+\b/i;
const ETAG_RE = /\betag-?[a-z0-9_-]+\b|W\/"[^"]*"/i;
const PROVIDER_GUID_RE = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;

/**
 * Formats an external-effect value for display: Graph event ids, change
 * keys, ETags, provider GUIDs and http(s) URLs collapse to a static Chinese
 * safe summary; ordinary text stays readable and length-bounded.
 */
export function safeExternalEffectText(value: unknown): string {
  if (value === undefined || value === null) return '';
  if (typeof value === 'number') return String(value);
  if (typeof value === 'boolean') return value ? '是' : '否';
  if (typeof value !== 'string') return String(value);
  const trimmed = value.trim();
  if (trimmed === '') return '';
  if (EXTERNAL_URL_RE.test(trimmed)) return SAFE_EXTERNAL_EFFECT_URL;
  if (
    GRAPH_EVENT_ID_RE.test(trimmed)
    || GRAPH_EVENT_ID_AAMK_RE.test(trimmed)
    || CHANGE_KEY_RE.test(trimmed)
    || ETAG_RE.test(trimmed)
    || PROVIDER_GUID_RE.test(trimmed)
  ) {
    return SAFE_EXTERNAL_EFFECT_ID;
  }
  return trimmed.length > MAX_VALUE_LENGTH ? `${trimmed.slice(0, MAX_VALUE_LENGTH)}…` : trimmed;
}
