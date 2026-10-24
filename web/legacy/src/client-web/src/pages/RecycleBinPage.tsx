import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { getRecycleBin, previewRecycleRestore, restoreRecycleItem } from '../api/calendar';
import MobilePageHeader from '../ui/MobilePageHeader';
import EmptyState from '../ui/EmptyState';
import OperationResultBanner from '../ui/OperationResultBanner';
import PageHeader from '../ui/PageHeader';
import type {
  CalendarOperationResult,
  CalendarOperationSample,
  CalendarRecycleBinItem,
  CalendarRestorePreviewResponse,
} from '../types';

type RecycleType = 'all' | 'event' | 'task' | 'calendar' | 'task-book';

const typeOptions: { value: RecycleType; label: string }[] = [
  { value: 'all', label: '全部' },
  { value: 'event', label: '日程' },
  { value: 'task', label: '任务' },
  { value: 'calendar', label: '日历本' },
  { value: 'task-book', label: '任务本' },
];

const invalidateAfterRestoreKeys = [
  ['calendar-recycle-bin'],
  ['events'],
  ['events-paged'],
  ['tasks'],
  ['calendars'],
  ['today-sections'],
  ['today-section'],
] as const;

function typeLabel(type: string) {
  if (type === 'event') return '日程';
  if (type === 'task') return '任务';
  if (type === 'calendar' || type === 'calendar-book') return '日历本';
  if (type === 'task-book') return '任务本';
  return type || '未知';
}

function canRestoreAsCopy(type: string) {
  return type === 'event' || type === 'task';
}

function recycleItemKey(item: Pick<CalendarRecycleBinItem, 'type' | 'id'>) {
  return `${item.type}:${item.id}`;
}

function getErrorMessage(error: unknown) {
  if (error instanceof Error && error.message) return error.message;
  return '操作失败，请稍后再试。';
}

function formatDateTime(value?: string) {
  if (!value) return '-';

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;

  return parsed.toLocaleString('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function formatSampleTime(sample: CalendarOperationSample) {
  if (sample.start && sample.end) return `${formatDateTime(sample.start)} - ${formatDateTime(sample.end)}`;
  return formatDateTime(sample.start || sample.end);
}

interface RestorePreviewDialogProps {
  item: CalendarRecycleBinItem;
  preview: CalendarRestorePreviewResponse | null;
  isLoading: boolean;
  previewError: unknown;
  restoreError: unknown;
  isRestoring: boolean;
  onCancel: () => void;
  onRetryPreview: () => void;
  onRestore: (restoreAsCopy: boolean) => void;
}

function RestorePreviewDialog({
  item,
  preview,
  isLoading,
  previewError,
  restoreError,
  isRestoring,
  onCancel,
  onRetryPreview,
  onRestore,
}: RestorePreviewDialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const titleId = useId();
  const hasConflicts = (preview?.conflicts.length ?? 0) > 0;
  const copyAllowed = canRestoreAsCopy(item.type);
  const canRestoreNormally = Boolean(preview?.canRestoreWithoutConflict);

  useEffect(() => {
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    dialogRef.current?.focus();

    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, []);

  function getFocusableElements() {
    const dialog = dialogRef.current;
    if (!dialog) return [];

    return Array.from(
      dialog.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ),
    ).filter(element => !element.hasAttribute('aria-hidden'));
  }

  function handleKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      onCancel();
      return;
    }

    if (e.key !== 'Tab') return;

    const focusableElements = getFocusableElements();
    if (focusableElements.length === 0) {
      e.preventDefault();
      dialogRef.current?.focus();
      return;
    }

    const firstElement = focusableElements[0];
    const lastElement = focusableElements[focusableElements.length - 1];
    const activeElement = document.activeElement;

    if (e.shiftKey && (activeElement === firstElement || activeElement === dialogRef.current)) {
      e.preventDefault();
      lastElement.focus();
    } else if (!e.shiftKey && (activeElement === lastElement || activeElement === dialogRef.current)) {
      e.preventDefault();
      firstElement.focus();
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/30 px-4 py-6">
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="w-full max-w-2xl rounded-lg border border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4">
          <p className="text-xs font-semibold uppercase text-blue-600">恢复预览</p>
          <h2 id={titleId} className="mt-1 text-base font-semibold text-slate-950">
            恢复“{item.title}”
          </h2>
          <p className="mt-2 text-sm text-slate-600">
            {typeLabel(item.type)}
            {item.bookName ? ` · 原本所属：${item.bookName}` : ''}
          </p>
        </header>

        <section className="max-h-[60vh] overflow-auto px-5 py-4">
          {isLoading && (
            <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-6 text-center text-sm text-slate-500">
              正在检查恢复影响...
            </div>
          )}

          {!isLoading && Boolean(previewError) && (
            <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              <p className="font-medium">恢复预览加载失败</p>
              <p className="mt-1">{getErrorMessage(previewError)}</p>
              <button
                type="button"
                onClick={onRetryPreview}
                className="mt-3 rounded-md border border-red-200 bg-white px-3 py-1.5 text-xs font-medium text-red-700 hover:bg-red-100"
              >
                重新检查
              </button>
            </div>
          )}

          {!isLoading && preview && (
            <div className="space-y-4">
              <div
                className={`rounded-lg border px-4 py-3 text-sm ${
                  hasConflicts
                    ? 'border-amber-200 bg-amber-50 text-amber-900'
                    : 'border-teal-200 bg-teal-50 text-teal-900'
                }`}
              >
                {hasConflicts ? (
                  <>
                    <p className="font-medium">发现 {preview.conflicts.length} 个冲突，不能直接恢复。</p>
                    <p className="mt-1">
                      {copyAllowed
                        ? '可以恢复为副本，避免覆盖或合并现有项目。'
                        : '日历本和任务本暂不支持恢复为副本，请先处理冲突后再恢复。'}
                    </p>
                  </>
                ) : (
                  <p className="font-medium">未发现冲突，可恢复 {preview.restoreCount} 项。</p>
                )}
              </div>

              {preview.samples.length > 0 && (
                <div>
                  <div className="mb-2 flex items-center justify-between gap-3">
                    <h3 className="text-sm font-medium text-slate-800">将恢复的项目</h3>
                    <span className="rounded-md bg-slate-100 px-2 py-1 text-xs font-medium text-slate-600">
                      共 {preview.restoreCount} 项
                    </span>
                  </div>
                  <ul className="space-y-2">
                    {preview.samples.map(sample => (
                      <li key={`${sample.type}:${sample.id}`} className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2">
                        <div className="flex flex-wrap items-start justify-between gap-2">
                          <div className="min-w-0">
                            <p className="truncate text-sm font-medium text-slate-900">{sample.title}</p>
                            <p className="mt-0.5 text-xs text-slate-500">
                              {typeLabel(sample.type)}
                              {sample.bookName ? ` · ${sample.bookName}` : ''}
                            </p>
                          </div>
                          {(sample.start || sample.end) && (
                            <span className="shrink-0 text-xs text-slate-500">{formatSampleTime(sample)}</span>
                          )}
                        </div>
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              {hasConflicts && (
                <div>
                  <h3 className="mb-2 text-sm font-medium text-slate-800">冲突详情</h3>
                  <ul className="space-y-2">
                    {preview.conflicts.map(conflict => (
                      <li
                        key={`${conflict.deletedType}:${conflict.deletedId}:${conflict.activeId}`}
                        className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900"
                      >
                        <p className="font-medium">{conflict.title}</p>
                        <p className="mt-1 text-xs">
                          {typeLabel(conflict.deletedType)} 与现有 {typeLabel(conflict.activeType)} 冲突：{conflict.reason}
                        </p>
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              {Boolean(restoreError) && (
                <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                  {getErrorMessage(restoreError)}
                </div>
              )}
            </div>
          )}
        </section>

        <footer className="flex flex-wrap items-center justify-end gap-2 border-t border-slate-200 px-5 py-4">
          <button
            type="button"
            onClick={onCancel}
            className="pim-button-secondary px-4 py-2 text-sm"
          >
            取消
          </button>
          {preview && hasConflicts && copyAllowed && (
            <button
              type="button"
              onClick={() => onRestore(true)}
              disabled={isRestoring}
              className="pim-button-secondary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            >
              {isRestoring ? '恢复中...' : '恢复为副本'}
            </button>
          )}
          <button
            type="button"
            onClick={() => onRestore(false)}
            disabled={!preview || !canRestoreNormally || isRestoring}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isRestoring ? '恢复中...' : '恢复'}
          </button>
        </footer>
      </div>
    </div>
  );
}

export default function RecycleBinPage() {
  const queryClient = useQueryClient();
  const [type, setType] = useState<RecycleType>('all');
  const [search, setSearch] = useState('');
  const [selectedItem, setSelectedItem] = useState<CalendarRecycleBinItem | null>(null);
  const [preview, setPreview] = useState<CalendarRestorePreviewResponse | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  const [isPreviewLoading, setIsPreviewLoading] = useState(false);
  const [result, setResult] = useState<CalendarOperationResult | null>(null);
  const selectedItemKeyRef = useRef<string | null>(null);
  const activePreviewRequestRef = useRef<{ itemKey: string; requestId: number } | null>(null);
  const nextPreviewRequestIdRef = useRef(0);

  const normalizedSearch = search.trim();

  const listQuery = useQuery({
    queryKey: ['calendar-recycle-bin', type, normalizedSearch],
    queryFn: () =>
      getRecycleBin({
        type,
        search: normalizedSearch || undefined,
        page: 1,
        pageSize: 50,
      }),
  });

  const previewMutation = useMutation({
    mutationFn: (item: CalendarRecycleBinItem) => previewRecycleRestore(item.type, item.id),
  });

  const restoreMutation = useMutation({
    mutationFn: ({ item, restoreAsCopy }: { item: CalendarRecycleBinItem; restoreAsCopy: boolean }) =>
      restoreRecycleItem(item.type, item.id, restoreAsCopy),
    onSuccess: data => {
      setResult(data);
      selectedItemKeyRef.current = null;
      activePreviewRequestRef.current = null;
      setSelectedItem(null);
      setPreview(null);
      setPreviewError(null);
      setIsPreviewLoading(false);
      for (const queryKey of invalidateAfterRestoreKeys) {
        void queryClient.invalidateQueries({ queryKey });
      }
    },
  });

  const items = listQuery.data?.items ?? [];

  function isActivePreviewRequest(item: CalendarRecycleBinItem, requestId: number) {
    const itemKey = recycleItemKey(item);
    return selectedItemKeyRef.current === itemKey
      && activePreviewRequestRef.current?.itemKey === itemKey
      && activePreviewRequestRef.current.requestId === requestId;
  }

  function startPreviewRequest(item: CalendarRecycleBinItem) {
    const itemKey = recycleItemKey(item);
    const requestId = nextPreviewRequestIdRef.current + 1;

    nextPreviewRequestIdRef.current = requestId;
    selectedItemKeyRef.current = itemKey;
    activePreviewRequestRef.current = { itemKey, requestId };
    setPreview(null);
    setPreviewError(null);
    setIsPreviewLoading(true);
    previewMutation.reset();
    previewMutation.mutate(item, {
      onSuccess: (data, variables) => {
        if (isActivePreviewRequest(variables, requestId)) {
          setPreview(data);
        }
      },
      onError: (error, variables) => {
        if (isActivePreviewRequest(variables, requestId)) {
          setPreviewError(error);
        }
      },
      onSettled: (_data, _error, variables) => {
        if (isActivePreviewRequest(variables, requestId)) {
          setIsPreviewLoading(false);
        }
      },
    });
  }

  function openRestorePreview(item: CalendarRecycleBinItem) {
    setResult(null);
    setSelectedItem(item);
    restoreMutation.reset();
    startPreviewRequest(item);
  }

  function closeRestorePreview() {
    selectedItemKeyRef.current = null;
    activePreviewRequestRef.current = null;
    setSelectedItem(null);
    setPreview(null);
    setPreviewError(null);
    setIsPreviewLoading(false);
    previewMutation.reset();
    restoreMutation.reset();
  }

  function retryRestorePreview() {
    if (!selectedItem) return;

    startPreviewRequest(selectedItem);
  }

  function restoreSelected(restoreAsCopy: boolean) {
    if (!selectedItem) return;
    restoreMutation.mutate({ item: selectedItem, restoreAsCopy });
  }

  return (
    <div className="mx-auto max-w-6xl space-y-4 overflow-auto pb-20 md:pb-4">
      <MobilePageHeader title="回收站" />
      <PageHeader
        title="回收站"
        subtitle="恢复已删除的日程、任务、日历本和任务本"
        actions={
          <Link to="/settings" className="pim-button-secondary px-3 py-1.5 text-sm">
            返回设置
          </Link>
        }
      />

      <OperationResultBanner result={result} onDismiss={() => setResult(null)} />

      <section className="pim-panel flex flex-wrap items-center gap-3 p-4">
        <label className="flex min-w-44 flex-col gap-1 text-sm">
          <span className="text-xs font-medium text-slate-500">类型</span>
          <select
            value={type}
            onChange={event => setType(event.target.value as RecycleType)}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
          >
            {typeOptions.map(option => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>

        <label className="flex min-w-64 flex-1 flex-col gap-1 text-sm">
          <span className="text-xs font-medium text-slate-500">搜索</span>
          <input
            type="search"
            value={search}
            onChange={event => setSearch(event.target.value)}
            placeholder="搜索标题或原本所属"
            className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
          />
        </label>

        <div className="ml-auto self-end text-sm text-slate-500">
          {listQuery.isFetching ? '正在刷新...' : `共 ${listQuery.data?.totalCount ?? 0} 项`}
        </div>
      </section>

      <section className="pim-panel overflow-hidden">
        {listQuery.isLoading ? (
          <div className="px-4 py-10 text-center text-sm text-slate-500">正在加载回收站...</div>
        ) : listQuery.isError ? (
          <div className="p-4">
            <EmptyState
              title="回收站加载失败"
              description={getErrorMessage(listQuery.error)}
              action={
                <button
                  type="button"
                  onClick={() => void listQuery.refetch()}
                  className="pim-button-secondary px-3 py-1.5 text-sm"
                >
                  重新加载
                </button>
              }
            />
          </div>
        ) : items.length === 0 ? (
          <div className="p-4">
            <EmptyState title="回收站为空" description="删除的日程、任务、日历本和任务本会显示在这里。" />
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full text-left text-sm">
              <thead className="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase text-slate-500">
                <tr>
                  <th className="px-4 py-3">类型</th>
                  <th className="px-4 py-3">标题</th>
                  <th className="px-4 py-3">原本所属</th>
                  <th className="px-4 py-3">删除时间</th>
                  <th className="px-4 py-3 text-right">操作</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {items.map(item => (
                  <tr key={`${item.type}:${item.id}`} className="transition-colors hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 text-slate-600">{typeLabel(item.type)}</td>
                    <td className="min-w-56 px-4 py-3">
                      <p className="font-medium text-slate-950">{item.title || '未命名项目'}</p>
                      {(item.start || item.end) && (
                        <p className="mt-1 text-xs text-slate-500">{formatDateTime(item.start || item.end)}</p>
                      )}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-600">{item.bookName || '-'}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-600">{formatDateTime(item.deletedAt)}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <button
                        type="button"
                        onClick={() => openRestorePreview(item)}
                        disabled={previewMutation.isPending || restoreMutation.isPending}
                        className="pim-button-secondary min-h-[44px] px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        恢复
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {selectedItem && (
        <RestorePreviewDialog
          item={selectedItem}
          preview={preview}
          isLoading={isPreviewLoading}
          previewError={previewError}
          restoreError={restoreMutation.error}
          isRestoring={restoreMutation.isPending}
          onCancel={closeRestorePreview}
          onRetryPreview={retryRestorePreview}
          onRestore={restoreSelected}
        />
      )}
    </div>
  );
}
