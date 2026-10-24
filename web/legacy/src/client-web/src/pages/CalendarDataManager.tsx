import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient, useMutation } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { getCalendars, getEventsPaged, exportIcs, importIcs, batchDeleteEvents } from '../api/calendar';
import type { CalendarOperationResult, EventResponse, ImportReport } from '../types';
import OperationResultBanner from '../ui/OperationResultBanner';
import ConfirmActionDialog, { type DeleteConfirmationInput } from '../ui/ConfirmActionDialog';

function pruneSelectedIds(selected: Set<string>, visibleIds: string[]) {
  const visibleIdSet = new Set(visibleIds);
  return new Set(Array.from(selected).filter(id => visibleIdSet.has(id)));
}

function hasStaleSelection(selected: Set<string>, visibleIds: string[]) {
  if (selected.size === 0) return false;
  const visibleIdSet = new Set(visibleIds);
  return Array.from(selected).some(id => !visibleIdSet.has(id));
}

export default function CalendarDataManager() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // Filters
  const [search, setSearch] = useState('');
  const [calendarId, setCalendarId] = useState('');
  const [dateRange, setDateRange] = useState('all');
  const [customStart, setCustomStart] = useState('');
  const [customEnd, setCustomEnd] = useState('');
  const [page, setPage] = useState(1);

  // Selection
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());

  // Detail dialog
  const [detailEvent, setDetailEvent] = useState<EventResponse | null>(null);

  // Operation feedback
  const [operationResult, setOperationResult] = useState<CalendarOperationResult | ImportReport | null>(null);
  const [operationError, setOperationError] = useState<string | null>(null);
  const [deleteInput, setDeleteInput] = useState<DeleteConfirmationInput | null>(null);
  const [pendingDeleteIds, setPendingDeleteIds] = useState<string[]>([]);

  const { data: calendars } = useQuery({
    queryKey: ['calendars', 'calendar'],
    queryFn: () => getCalendars('calendar')
  });

  const dateParams = (() => {
    const now = new Date();
    switch (dateRange) {
      case '7d': return { start: new Date(now.getTime() - 7*86400000).toISOString(), end: now.toISOString() };
      case '30d': return { start: new Date(now.getTime() - 30*86400000).toISOString(), end: now.toISOString() };
      case 'month': return { start: new Date(now.getFullYear(), now.getMonth(), 1).toISOString(), end: now.toISOString() };
      case 'custom': return { start: customStart || undefined, end: customEnd || undefined };
      default: return {};
    }
  })();

  const { data, isLoading } = useQuery({
    queryKey: ['events-paged', search, calendarId, dateRange, customStart, customEnd, page],
    queryFn: () => getEventsPaged({
      search: search || undefined,
      calendarId: calendarId || undefined,
      start: dateParams.start,
      end: dateParams.end,
      page,
      pageSize: 50
    })
  });

  const currentIds = useMemo(() => data?.items.map(event => event.id) ?? [], [data?.items]);
  const visibleSelectedIds = useMemo(
    () => currentIds.filter(id => selectedIds.has(id)),
    [currentIds, selectedIds],
  );
  const allCurrentSelected = currentIds.length > 0 && currentIds.every(id => selectedIds.has(id));

  useEffect(() => {
    if (!hasStaleSelection(selectedIds, currentIds)) return;

    let cancelled = false;
    window.queueMicrotask(() => {
      if (!cancelled) setSelectedIds(current => pruneSelectedIds(current, currentIds));
    });

    return () => {
      cancelled = true;
    };
  }, [currentIds, selectedIds]);

  const [importCalendarId, setImportCalendarId] = useState('');

  const importMut = useMutation({
    mutationFn: (file: File) => importIcs(file, importCalendarId || undefined),
    onMutate: () => {
      setOperationResult(null);
      setOperationError(null);
    },
    onSuccess: (result) => {
      setOperationResult(result);
      setOperationError(null);
      queryClient.invalidateQueries({ queryKey: ['events-paged'] });
      queryClient.invalidateQueries({ queryKey: ['events'] });
    },
    onError: (err: Error) => {
      setOperationError(`导入失败: ${err.message}`);
      setOperationResult(null);
    }
  });

  const deleteMut = useMutation({
    mutationFn: batchDeleteEvents,
    onMutate: () => {
      setOperationResult(null);
      setOperationError(null);
    },
    onSuccess: (result) => {
      setOperationResult(result);
      setSelectedIds(new Set());
      setDeleteInput(null);
      setPendingDeleteIds([]);
      setOperationError(null);
      queryClient.invalidateQueries({ queryKey: ['events-paged'] });
      queryClient.invalidateQueries({ queryKey: ['events'] });
      queryClient.invalidateQueries({ queryKey: ['calendar-recycle-bin'] });
    },
    onError: (err: Error) => {
      setOperationError(`删除失败: ${err.message}`);
      setOperationResult(null);
      setDeleteInput(null);
      setPendingDeleteIds([]);
    }
  });

  function handleBatchDelete() {
    if (!data || visibleSelectedIds.length === 0) return;
    const selectedEvents = data.items.filter(event => selectedIds.has(event.id));
    if (selectedEvents.length === 0) return;
    const originalIds = selectedEvents.map(event => event.originalEventId ?? event.id);
    const uniqueIds = Array.from(new Set(originalIds));
    if (uniqueIds.length === 0) return;

    setOperationResult(null);
    setOperationError(null);
    setPendingDeleteIds(uniqueIds);
    setDeleteInput({
      targetType: 'event',
      title: uniqueIds.length === 1 ? selectedEvents[0]?.title ?? '选中的日程' : '选中的日程',
      affectedCount: uniqueIds.length,
      samples: selectedEvents.slice(0, 5).map(event => ({
        id: event.originalEventId ?? event.id,
        type: 'event',
        title: event.title,
        start: event.dtStart,
        end: event.dtEnd,
        bookName: calendars?.find(calendar => calendar.id === event.calendarId)?.name,
      })),
    });
  }

  function handleConfirmDelete() {
    if (pendingDeleteIds.length === 0) return;
    deleteMut.mutate(pendingDeleteIds);
  }

  function handleCancelDelete() {
    if (deleteMut.isPending) return;
    setDeleteInput(null);
    setPendingDeleteIds([]);
  }

  function toggleSelect(id: string) {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  function toggleSelectAll() {
    if (currentIds.length === 0) return;

    setSelectedIds(prev => {
      const next = new Set(prev);
      if (allCurrentSelected) {
        currentIds.forEach(id => next.delete(id));
      } else {
        currentIds.forEach(id => next.add(id));
      }
      return next;
    });
  }

  function handleExportSelected() {
    if (!data) return;
    const selectedEvents = data.items.filter(event => selectedIds.has(event.id));
    if (selectedEvents.length === 0) return;

    // Map selected occurrence IDs back to original event IDs for export
    const originalIds = selectedEvents.map(event => event.originalEventId ?? event.id);
    const uniqueIds = Array.from(new Set(originalIds));
    exportIcs(uniqueIds);
  }

  function handleExportAll() {
    exportIcs(undefined, dateParams.start, dateParams.end);
  }

  function handleImport() {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.ics';
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file) importMut.mutate(file);
    };
    input.click();
  }

  const rruleLabel = (rrule?: string) => {
    if (!rrule) return '—';
    if (rrule.includes('DAILY')) return '每日';
    if (rrule.includes('WEEKLY')) return '每周';
    if (rrule.includes('MONTHLY')) return '每月';
    return '重复';
  };

  return (
    <div className="max-w-5xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <button onClick={() => navigate('/settings')} className="text-gray-400 hover:text-gray-600">← 返回</button>
          <h2 className="text-xl font-bold">📅 管理日程数据</h2>
        </div>
        <div className="flex gap-2 items-center">
          <select value={importCalendarId} onChange={e => setImportCalendarId(e.target.value)}
            className="border rounded px-2 py-1.5 text-sm">
            <option value="">导入到: 默认日历</option>
            {calendars?.map(cal => (
              <option key={cal.id} value={cal.id}>{cal.name}</option>
            ))}
          </select>
          <button onClick={handleImport} disabled={importMut.isPending}
            className="px-3 py-1.5 text-sm border rounded hover:bg-gray-50 disabled:opacity-50">
            {importMut.isPending ? '导入中...' : '📥 导入 ICS'}
          </button>
          <button onClick={handleExportSelected} disabled={visibleSelectedIds.length === 0}
            className="px-3 py-1.5 text-sm border rounded hover:bg-gray-50 disabled:opacity-50">
            📤 导出选中({visibleSelectedIds.length})
          </button>
          <button onClick={handleExportAll}
            className="px-3 py-1.5 text-sm border rounded hover:bg-gray-50">
            📤 导出全部
          </button>
          <button onClick={handleBatchDelete} disabled={visibleSelectedIds.length === 0 || deleteMut.isPending}
            className="px-3 py-1.5 text-sm border border-red-200 rounded text-red-600 hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed">
            {deleteMut.isPending ? '删除中...' : `🗑 删除选中(${visibleSelectedIds.length})`}
          </button>
        </div>
      </div>

      {/* Operation result message */}
      <div className="mb-3 space-y-3">
        <OperationResultBanner result={operationResult} onDismiss={() => setOperationResult(null)} />
        {operationError && (
          <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700" role="alert">
            <div className="flex items-start justify-between gap-3">
              <span>{operationError}</span>
              <button type="button" onClick={() => setOperationError(null)} className="rounded-md px-2 py-1 text-xs font-medium text-red-700 hover:bg-red-100">
                关闭
              </button>
            </div>
          </div>
        )}
      </div>

      {/* Filter bar */}
      <div className="flex items-center gap-3 mb-3 bg-white border rounded-lg p-3">
        <input
          type="text" placeholder="搜索标题..."
          value={search} onChange={e => { setSearch(e.target.value); setPage(1); }}
          className="border rounded px-3 py-1.5 text-sm w-48"
        />
        <select value={calendarId} onChange={e => { setCalendarId(e.target.value); setPage(1); }}
          className="border rounded px-2 py-1.5 text-sm">
          <option value="">全部日历</option>
          {calendars?.map(cal => (
            <option key={cal.id} value={cal.id}>{cal.name}</option>
          ))}
        </select>
        <select value={dateRange} onChange={e => { setDateRange(e.target.value); setPage(1); }}
          className="border rounded px-2 py-1.5 text-sm">
          <option value="all">全部时间</option>
          <option value="7d">最近 7 天</option>
          <option value="30d">最近 30 天</option>
          <option value="month">本月</option>
          <option value="custom">自定义范围</option>
        </select>
        {dateRange === 'custom' && (
          <>
            <input type="date" value={customStart} onChange={e => setCustomStart(e.target.value)}
              className="border rounded px-2 py-1.5 text-sm" />
            <span className="text-sm text-gray-400">—</span>
            <input type="date" value={customEnd} onChange={e => setCustomEnd(e.target.value)}
              className="border rounded px-2 py-1.5 text-sm" />
          </>
        )}
        <span className="ml-auto text-sm text-gray-500">
          共 {data?.totalCount ?? '—'} 条
        </span>
      </div>

      {/* Table */}
      <div className="bg-white border rounded-lg overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-gray-50 border-b text-left">
              <th className="p-3 w-8">
                <input type="checkbox"
                  checked={allCurrentSelected}
                  onChange={toggleSelectAll} />
              </th>
              <th className="p-3">标题</th>
              <th className="p-3 w-20">日历</th>
              <th className="p-3 w-36">开始时间</th>
              <th className="p-3 w-36">结束时间</th>
              <th className="p-3 w-16">重复</th>
              <th className="p-3 w-16">操作</th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr><td colSpan={7} className="p-8 text-center text-gray-400">加载中...</td></tr>
            ) : !data || data.items.length === 0 ? (
              <tr><td colSpan={7} className="p-8 text-center text-gray-400">无日程数据</td></tr>
            ) : (
              data.items.map(event => {
                const cal = calendars?.find(c => c.id === event.calendarId);
                return (
                  <tr key={event.id} className="border-b hover:bg-gray-50">
                    <td className="p-3">
                      <input type="checkbox" checked={selectedIds.has(event.id)}
                        onChange={() => toggleSelect(event.id)} />
                    </td>
                    <td className="p-3 font-medium">{event.title}</td>
                    <td className="p-3">
                      {cal && (
                        <span className="inline-flex items-center gap-1 text-xs">
                          <span className="w-2 h-2 rounded-full" style={{ backgroundColor: cal.color }} />
                          {cal.name}
                        </span>
                      )}
                    </td>
                    <td className="p-3 text-gray-600">{new Date(event.dtStart).toLocaleString('zh-CN')}</td>
                    <td className="p-3 text-gray-600">{new Date(event.dtEnd).toLocaleString('zh-CN')}</td>
                    <td className="p-3 text-gray-500 text-xs">{rruleLabel(event.rrule)}</td>
                    <td className="p-3">
                      <button onClick={() => setDetailEvent(event)}
                        className="text-blue-600 hover:underline text-xs">详情</button>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {data && data.totalPages > 1 && (
        <div className="flex justify-center gap-1 mt-3">
          <button onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page <= 1}
            className="px-3 py-1.5 text-sm border rounded disabled:opacity-30 hover:bg-gray-50">
            上一页
          </button>
          {Array.from({ length: data.totalPages }, (_, i) => i + 1)
            .filter(p => p === 1 || p === data.totalPages || Math.abs(p - page) <= 2)
            .map((p, i, arr) => (
              <span key={p}>
                {i > 0 && arr[i - 1] !== p - 1 && <span className="px-1 text-gray-300">...</span>}
                <button onClick={() => setPage(p)}
                  className={`px-3 py-1.5 text-sm border rounded ${
                    p === page ? 'bg-blue-600 text-white' : 'hover:bg-gray-50'
                  }`}>
                  {p}
                </button>
              </span>
            ))}
          <button onClick={() => setPage(p => Math.min(data.totalPages, p + 1))} disabled={page >= data.totalPages}
            className="px-3 py-1.5 text-sm border rounded disabled:opacity-30 hover:bg-gray-50">
            下一页
          </button>
        </div>
      )}

      {/* Detail Dialog */}
      {detailEvent && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50" onClick={() => setDetailEvent(null)}>
          <div className="bg-white rounded-lg p-6 max-w-lg w-full mx-4 max-h-[90vh] overflow-auto" onClick={e => e.stopPropagation()}>
            <div className="flex items-center justify-between mb-4">
              <h3 className="font-semibold text-lg">日程详情</h3>
              <button onClick={() => setDetailEvent(null)} className="text-gray-400 hover:text-gray-600 text-xl leading-none">&times;</button>
            </div>
            <dl className="space-y-3 text-sm">
              <div><dt className="text-gray-400">标题</dt><dd className="font-medium">{detailEvent.title}</dd></div>
              <div><dt className="text-gray-400">日历</dt><dd>{calendars?.find(c => c.id === detailEvent.calendarId)?.name ?? detailEvent.calendarId}</dd></div>
              <div><dt className="text-gray-400">开始时间</dt><dd>{new Date(detailEvent.dtStart).toLocaleString('zh-CN')}</dd></div>
              <div><dt className="text-gray-400">结束时间</dt><dd>{new Date(detailEvent.dtEnd).toLocaleString('zh-CN')}</dd></div>
              {detailEvent.location && <div><dt className="text-gray-400">地点</dt><dd>{detailEvent.location}</dd></div>}
              {detailEvent.description && <div><dt className="text-gray-400">描述</dt><dd className="whitespace-pre-wrap">{detailEvent.description}</dd></div>}
              {detailEvent.rrule && <div><dt className="text-gray-400">重复规则</dt><dd>{rruleLabel(detailEvent.rrule)} ({detailEvent.rrule})</dd></div>}
              {detailEvent.source === 'outlook-ics' && (
                <div>
                  <dt className="text-gray-400">Outlook 导入</dt>
                  <dd className="rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm leading-6 text-blue-700">
                    Outlook 导入：会议字段已保留，PIM 暂不处理会议响应。
                  </dd>
                </div>
              )}
              <div><dt className="text-gray-400">状态</dt><dd>{detailEvent.status}</dd></div>
            </dl>
          </div>
        </div>
      )}

      <ConfirmActionDialog
        open={deleteInput !== null}
        input={deleteInput}
        isPending={deleteMut.isPending}
        onCancel={handleCancelDelete}
        onConfirm={handleConfirmDelete}
      />
    </div>
  );
}
