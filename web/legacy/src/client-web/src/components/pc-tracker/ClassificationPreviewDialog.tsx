import { useEffect, useId, useMemo, useRef, useState } from 'react';
import type {
  ActivityClassificationApplyRange,
  ActivityClassificationPreview,
  ActivityClassificationSuggestion,
  SuggestionClassificationApplyRequest,
  SuggestionClassificationPreviewRequest,
} from '../../types';
import type { CategoryTreeNode } from '../../api/pcTracker';
import RuleImpactPreviewPanel from './RuleImpactPreviewPanel';

export type PreviewLike = { preview: ActivityClassificationPreview };

interface Props {
  suggestion: ActivityClassificationSuggestion | null;
  date: string;
  preview: PreviewLike | null;
  isPreviewing: boolean;
  isApplying: boolean;
  errorMessage: string | null;
  categories?: CategoryTreeNode[];
  onClose: () => void;
  onPreview: (request: SuggestionClassificationPreviewRequest) => void;
  onApply: (request: SuggestionClassificationApplyRequest) => void;
}

interface CategoryOption {
  value: string;
  label: string;
}

function addCategoryOptions(
  nodes: CategoryTreeNode[] | undefined,
  options: CategoryOption[],
  seen: Set<string>,
  depth = 0
) {
  for (const node of nodes ?? []) {
    const name = node.name.trim();
    if (name && !seen.has(name.toLowerCase())) {
      seen.add(name.toLowerCase());
      options.push({
        value: name,
        label: `${'　'.repeat(depth)}${name}`,
      });
    }

    addCategoryOptions(node.children, options, seen, depth + 1);
  }
}

export function buildClassificationCategoryOptions(
  categories: CategoryTreeNode[] | undefined,
  extraNames: Array<string | null | undefined>
) {
  const options: CategoryOption[] = [];
  const seen = new Set<string>();
  addCategoryOptions(categories, options, seen);

  for (const value of extraNames) {
    const name = value?.trim();
    if (!name || seen.has(name.toLowerCase())) continue;
    seen.add(name.toLowerCase());
    options.push({ value: name, label: name });
  }

  if (!seen.has('其他')) {
    options.push({ value: '其他', label: '其他' });
  }

  return options;
}

export function classificationPreviewRequestKey(
  request: SuggestionClassificationPreviewRequest | SuggestionClassificationApplyRequest
) {
  return JSON.stringify({
    categoryName: request.categoryName?.trim() || null,
    projectTag: request.projectTag?.trim() || null,
    range: {
      mode: request.range.mode,
      dateFrom: request.range.dateFrom || null,
      dateTo: request.range.dateTo || null,
    },
  });
}

export function classificationPreviewConfirmationKey(
  suggestionId: string,
  request: SuggestionClassificationPreviewRequest | SuggestionClassificationApplyRequest
) {
  return `${suggestionId}:${classificationPreviewRequestKey(request)}`;
}

export function canApplyClassificationPreview(
  preview: ActivityClassificationPreview | null,
  confirmedPreviewConfirmationKey: string | null,
  suggestionId: string,
  request: SuggestionClassificationPreviewRequest | SuggestionClassificationApplyRequest,
  isPreviewing: boolean,
  isApplying: boolean
) {
  return Boolean(preview)
    && !isPreviewing
    && !isApplying
    && confirmedPreviewConfirmationKey === classificationPreviewConfirmationKey(suggestionId, request);
}

export function resolveConfirmedClassificationPreviewKey({
  previousPreview,
  nextPreview,
  pendingPreviewConfirmationKey,
  confirmedPreviewConfirmationKey,
}: {
  previousPreview: PreviewLike | null;
  nextPreview: PreviewLike | null;
  pendingPreviewConfirmationKey: string | null;
  confirmedPreviewConfirmationKey: string | null;
}) {
  if (!nextPreview) return null;
  if (nextPreview !== previousPreview && pendingPreviewConfirmationKey) {
    return pendingPreviewConfirmationKey;
  }

  return confirmedPreviewConfirmationKey;
}

export default function ClassificationPreviewDialog({
  suggestion,
  date,
  preview,
  isPreviewing,
  isApplying,
  errorMessage,
  categories = [],
  onClose,
  onPreview,
  onApply,
}: Props) {
  const titleId = useId();
  const [categoryName, setCategoryName] = useState('');
  const [projectTag, setProjectTag] = useState('');
  const [mode, setMode] = useState<ActivityClassificationApplyRange['mode']>('today');
  const [dateFrom, setDateFrom] = useState(date);
  const [dateTo, setDateTo] = useState(date);
  const [pendingPreviewConfirmationKey, setPendingPreviewConfirmationKey] = useState<string | null>(null);
  const [confirmedPreviewConfirmationKey, setConfirmedPreviewConfirmationKey] = useState<string | null>(null);
  const previousPreviewRef = useRef<PreviewLike | null>(preview);

  useEffect(() => {
    if (!suggestion) return;
    setCategoryName(suggestion.suggestedCategory || suggestion.currentCategory || '其他');
    setProjectTag(suggestion.suggestedProjectTag || '');
    setMode('today');
    setDateFrom(date);
    setDateTo(date);
    setPendingPreviewConfirmationKey(null);
    setConfirmedPreviewConfirmationKey(null);
  }, [date, suggestion]);

  useEffect(() => {
    const nextConfirmedPreviewConfirmationKey = resolveConfirmedClassificationPreviewKey({
      previousPreview: previousPreviewRef.current,
      nextPreview: preview,
      pendingPreviewConfirmationKey,
      confirmedPreviewConfirmationKey,
    });

    if (nextConfirmedPreviewConfirmationKey !== confirmedPreviewConfirmationKey) {
      setConfirmedPreviewConfirmationKey(nextConfirmedPreviewConfirmationKey);
    }

    if (preview && preview !== previousPreviewRef.current && pendingPreviewConfirmationKey) {
      setPendingPreviewConfirmationKey(null);
    }

    previousPreviewRef.current = preview;
  }, [confirmedPreviewConfirmationKey, pendingPreviewConfirmationKey, preview]);

  const request = useMemo<SuggestionClassificationPreviewRequest>(() => {
    const range: ActivityClassificationApplyRange = {
      mode,
      dateFrom: mode === 'range' ? dateFrom : date,
      dateTo: mode === 'range' ? dateTo : date,
    };

    return {
      categoryName: categoryName.trim() || null,
      projectTag: projectTag.trim() || null,
      range,
    };
  }, [categoryName, date, dateFrom, dateTo, mode, projectTag]);

  const categoryOptions = useMemo(() => buildClassificationCategoryOptions(categories, [
    suggestion?.suggestedCategory,
    suggestion?.currentCategory,
    categoryName,
  ]), [categories, categoryName, suggestion]);

  if (!suggestion) return null;

  const canApply = canApplyClassificationPreview(
    preview?.preview ?? null,
    confirmedPreviewConfirmationKey,
    suggestion.id,
    request,
    isPreviewing,
    isApplying
  );

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center px-3 py-6">
      <div className="absolute inset-0 bg-slate-950/25" onClick={onClose} />
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="relative flex max-h-full w-full max-w-[680px] flex-col overflow-hidden rounded-lg border border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 id={titleId} className="text-base font-semibold text-slate-950">
                App 知识库写入预览
              </h2>
              <p className="mt-1 truncate text-sm text-slate-500">{suggestion.clusterKey}</p>
            </div>
            <button
              type="button"
              onClick={onClose}
              className="pim-button-secondary h-9 shrink-0 px-3 text-sm"
            >
              关闭
            </button>
          </div>
        </header>

        <div className="min-h-0 flex-1 space-y-4 overflow-auto px-5 py-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="min-w-0 text-sm">
              <span className="mb-1 block text-xs font-medium text-slate-500">目标分类</span>
              <select
                value={categoryName}
                onChange={event => setCategoryName(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 outline-none focus:border-cyan-300 focus:ring-2 focus:ring-cyan-100"
              >
                {categoryOptions.map(option => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="min-w-0 text-sm">
              <span className="mb-1 block text-xs font-medium text-slate-500">项目标签</span>
              <input
                value={projectTag}
                onChange={event => setProjectTag(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 outline-none focus:border-cyan-300 focus:ring-2 focus:ring-cyan-100"
              />
            </label>
          </div>

          <div className="grid grid-cols-2 gap-2">
            {(['today', 'range'] as const).map(value => (
              <button
                key={value}
                type="button"
                onClick={() => setMode(value)}
                className={value === mode ? 'pim-button-primary h-9 text-sm' : 'pim-button-secondary h-9 text-sm'}
              >
                {value === 'today' ? '今天' : '日期范围'}
              </button>
            ))}
          </div>

          {mode === 'range' && (
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="min-w-0 text-sm">
                <span className="mb-1 block text-xs font-medium text-slate-500">开始日期</span>
                <input
                  type="date"
                  value={dateFrom}
                  onChange={event => setDateFrom(event.target.value)}
                  className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 outline-none focus:border-cyan-300 focus:ring-2 focus:ring-cyan-100"
                />
              </label>
              <label className="min-w-0 text-sm">
                <span className="mb-1 block text-xs font-medium text-slate-500">结束日期</span>
                <input
                  type="date"
                  value={dateTo}
                  onChange={event => setDateTo(event.target.value)}
                  className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm text-slate-900 outline-none focus:border-cyan-300 focus:ring-2 focus:ring-cyan-100"
                />
              </label>
            </div>
          )}

          {errorMessage && (
            <div className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
              {errorMessage}
            </div>
          )}

          {preview && <RuleImpactPreviewPanel preview={preview.preview} />}
        </div>

        <footer className="flex flex-col gap-2 border-t border-slate-200 px-5 py-4 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={() => {
              setConfirmedPreviewConfirmationKey(null);
              setPendingPreviewConfirmationKey(classificationPreviewConfirmationKey(suggestion.id, request));
              onPreview(request);
            }}
            disabled={isPreviewing || isApplying}
            className="pim-button-secondary h-10 px-4 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isPreviewing ? '预览中' : '预览影响'}
          </button>
          <button
            type="button"
            onClick={() => {
              if (canApply) onApply(request);
            }}
            disabled={!canApply}
            className="pim-button-primary h-10 px-4 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isApplying ? '写入中' : '写入 App 知识库'}
          </button>
        </footer>
      </section>
    </div>
  );
}
