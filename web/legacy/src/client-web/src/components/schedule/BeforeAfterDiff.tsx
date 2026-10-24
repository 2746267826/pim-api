import {
  LEGACY_SENSITIVE_KEY_PATTERN,
  diffEventFields,
  diffGenericFields,
  formatFieldValue,
  type EventFieldDiffEntry,
  type EventFieldDiffInput,
} from '../../utils/eventFieldDiff';

export interface WritebackDiffMeta {
  operation?: string | null;
  accountName?: string | null;
  calendarName?: string | null;
  scope?: string | null;
}

interface BeforeAfterDiffProps {
  before?: EventFieldDiffInput | null;
  after?: EventFieldDiffInput | null;
  diffs?: EventFieldDiffEntry[] | null;
  meta?: WritebackDiffMeta | null;
  /** @deprecated Compatibility for audit/confirmation pages. Values are parsed and rendered as safe rows. */
  beforeJson?: string | null;
  /** @deprecated Compatibility for audit/confirmation pages. Values are parsed and rendered as safe rows. */
  afterJson?: string | null;
}

const OPERATION_LABELS: Record<string, string> = {
  create: '创建',
  update: '更新',
  delete: '删除',
};

const SCOPE_LABELS: Record<string, string> = {
  instance: '仅此实例',
  series: '整个系列',
};

const KIND_LABELS: Record<EventFieldDiffEntry['kind'], string> = {
  added: '新增',
  removed: '删除',
  modified: '修改',
};

const KIND_VALUE_CLASSES: Record<EventFieldDiffEntry['kind'], string> = {
  added: 'bg-emerald-50 text-emerald-800',
  removed: 'bg-red-50 text-red-700 line-through',
  modified: 'bg-amber-50 text-amber-800',
};

const KIND_BORDER_CLASSES: Record<EventFieldDiffEntry['kind'], string> = {
  added: 'border-emerald-200',
  removed: 'border-red-200',
  modified: 'border-amber-200',
};

function parseLegacyRecord(value?: string | null): EventFieldDiffInput {
  if (!value) return {};
  try {
    const parsed: unknown = JSON.parse(value);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
    return Object.fromEntries(
      Object.entries(parsed as Record<string, unknown>)
        .filter(([key]) => !LEGACY_SENSITIVE_KEY_PATTERN.test(key)),
    );
  } catch {
    return {};
  }
}

function legacyEntries(
  beforeJson?: string | null,
  afterJson?: string | null,
): EventFieldDiffEntry[] | null {
  const before = parseLegacyRecord(beforeJson);
  const after = parseLegacyRecord(afterJson);
  const beforeUnavailable = !beforeJson || Object.keys(before).length === 0;
  const afterUnavailable = !afterJson || Object.keys(after).length === 0;
  // Never fabricate rows when neither structured snapshot exists: the caller
  // shows an explicit no-snapshot state instead.
  if (beforeUnavailable && afterUnavailable) return null;
  return diffGenericFields(before, after)
    .filter(entry => !LEGACY_SENSITIVE_KEY_PATTERN.test(entry.key));
}

export default function BeforeAfterDiff({
  before,
  after,
  diffs,
  meta,
  beforeJson,
  afterJson,
}: BeforeAfterDiffProps) {
  const entries = diffs
    ?? (before !== undefined || after !== undefined
      ? diffEventFields(before ?? {}, after ?? {})
      : legacyEntries(beforeJson, afterJson));
  const operationLabel = meta?.operation ? (OPERATION_LABELS[meta.operation] ?? meta.operation) : undefined;
  const scopeLabel = meta?.scope ? (SCOPE_LABELS[meta.scope] ?? meta.scope) : undefined;

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-3" aria-label="变更前后对比">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-xs font-semibold text-slate-500">变更前后</h3>
        {meta && (
          <dl className="flex flex-wrap gap-x-4 gap-y-1">
            {operationLabel && (
              <div className="flex items-center gap-1 text-[11px] text-slate-500">
                <dt>操作</dt>
                <dd className="font-semibold text-slate-700">{operationLabel}</dd>
              </div>
            )}
            {meta.accountName && (
              <div className="flex items-center gap-1 text-[11px] text-slate-500">
                <dt>账户</dt>
                <dd className="max-w-48 truncate font-semibold text-slate-700">{meta.accountName}</dd>
              </div>
            )}
            {meta.calendarName && (
              <div className="flex items-center gap-1 text-[11px] text-slate-500">
                <dt>日历</dt>
                <dd className="max-w-48 truncate font-semibold text-slate-700">{meta.calendarName}</dd>
              </div>
            )}
            {scopeLabel && (
              <div className="flex items-center gap-1 text-[11px] text-slate-500">
                <dt>范围</dt>
                <dd className="font-semibold text-slate-700">{scopeLabel}</dd>
              </div>
            )}
          </dl>
        )}
      </div>
      {entries === null ? (
        <p className="mt-3 text-sm text-slate-500">没有结构化快照，无法展示字段变更。</p>
      ) : entries.length === 0 ? (
        <p className="mt-3 text-sm text-slate-500">没有字段变更</p>
      ) : (
        <ul className="mt-3 space-y-1">
          {entries.map(entry => (
            <BeforeAfterDiffRow key={entry.key} entry={entry} />
          ))}
        </ul>
      )}
    </section>
  );
}

function BeforeAfterDiffRow({ entry }: { entry: EventFieldDiffEntry }) {
  const beforeText = entry.before === undefined ? '—' : formatFieldValue(entry.before, entry.key);
  const afterText = entry.after === undefined ? '—' : formatFieldValue(entry.after, entry.key);
  const valueClass = `${KIND_VALUE_CLASSES[entry.kind]} ${KIND_BORDER_CLASSES[entry.kind]}`;

  return (
    <li
      className="flex min-w-0 items-center gap-2 rounded-lg border border-slate-100 bg-slate-50/60 px-2 py-1.5"
      aria-label={`${entry.label}：${KIND_LABELS[entry.kind]}，${beforeText} → ${afterText}`}
    >
      <span className="w-24 shrink-0 truncate text-xs font-semibold text-slate-500" title={entry.label}>
        {entry.label}
      </span>
      <span className={`min-w-0 flex-1 truncate rounded-md border px-2 py-1 text-xs ${valueClass}`}>
        {beforeText}
      </span>
      <span aria-hidden="true" className="shrink-0 text-xs text-slate-300">→</span>
      <span className={`min-w-0 flex-1 truncate rounded-md border px-2 py-1 text-xs ${valueClass}`}>
        {afterText}
      </span>
    </li>
  );
}
