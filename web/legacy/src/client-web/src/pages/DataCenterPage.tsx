import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  getAuditExport,
  previewDataCenterRestore,
  queryDataCenter,
} from '../api/calendar';
import DataCenterBatchPreview from '../components/schedule/DataCenterBatchPreview';
import type { DataCenterItem } from '../types';
import PageHeader from '../ui/PageHeader';

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function readableObjectType(value?: string | null) {
  const labels: Record<string, string> = {
    event: '日程',
    task: '任务',
    'task-segment': '任务片段',
    habit: '习惯',
    reminder: '提醒',
    report: '报告',
    'sync-batch': '同步批次',
    'sync-conflict': '同步冲突',
    'audit-version': '审计版本',
  };

  return value ? labels[value] ?? value : '全部对象';
}

export default function DataCenterPage() {
  const [search, setSearch] = useState('');
  const [objectType, setObjectType] = useState('');
  const [source, setSource] = useState('');
  const [pendingOnly, setPendingOnly] = useState(false);
  const [outlookOnly, setOutlookOnly] = useState(false);
  const [selectedObjectId, setSelectedObjectId] = useState<string | null>(null);
  const [filterOpen, setFilterOpen] = useState(false);

  const request = useMemo(() => ({
    search: search.trim() || null,
    objectType: objectType || null,
    source: outlookOnly ? 'outlook' : source || null,
    pendingOnly,
    page: 1,
    pageSize: 50,
  }), [objectType, outlookOnly, pendingOnly, search, source]);

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['data-center-query', request],
    queryFn: () => queryDataCenter(request),
  });

  const exportMutation = useMutation({
    mutationFn: getAuditExport,
  });

  const restorePreviewMutation = useMutation({
    mutationFn: (auditVersionId: string) => previewDataCenterRestore(auditVersionId, '数据中心版本恢复预览'),
  });

  const items = data?.items ?? [];
  const selected = items.find(item => item.objectId === selectedObjectId) ?? items[0];
  const selectedKey = selected ? `${selected.objectType}-${selected.objectId}` : null;

  function selectRow(item: DataCenterItem) {
    setSelectedObjectId(item.objectId);
  }

  return (
    <div className="mx-auto w-full max-w-[1500px] space-y-4 pb-20">
      <PageHeader
        title="数据中心"
        subtitle="跨日程、任务、习惯、提醒、报告和同步来源进行全局治理、审计导出与版本恢复。"
        actions={
          <button
            type="button"
            onClick={() => exportMutation.mutate()}
            disabled={exportMutation.isPending}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            审计导出
          </button>
        }
      />

      <div className="flex justify-end lg:hidden">
        <button
          type="button"
          onClick={() => setFilterOpen(true)}
          className="pim-button-secondary px-3 py-2 text-sm"
        >
          筛选
        </button>
      </div>

      <section className="pim-panel p-4 hidden lg:block">
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(220px,1fr)_180px_180px_auto_auto]">
          <label className="min-w-0">
            <span className="text-xs font-semibold text-slate-500">全局搜索</span>
            <input
              value={search}
              onChange={event => setSearch(event.target.value)}
              placeholder="标题、摘要、来源对象"
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
            />
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">对象过滤</span>
            <select
              value={objectType}
              onChange={event => setObjectType(event.target.value)}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
            >
              <option value="">全部对象</option>
              <option value="event">日程</option>
              <option value="task">任务</option>
              <option value="task-segment">任务片段</option>
              <option value="habit">习惯</option>
              <option value="reminder">提醒</option>
              <option value="report">报告</option>
              <option value="sync-batch">同步批次</option>
              <option value="sync-conflict">同步冲突</option>
              <option value="audit-version">审计版本</option>
            </select>
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">来源过滤</span>
            <select
              value={source}
              onChange={event => setSource(event.target.value)}
              disabled={outlookOnly}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400 disabled:bg-slate-50"
            >
              <option value="">全部来源</option>
              <option value="pim">PIM</option>
              <option value="outlook">Outlook</option>
              <option value="manual">手动</option>
              <option value="ai">智能</option>
            </select>
          </label>
          <label className="flex items-end gap-2 pb-2 text-sm font-medium text-slate-700">
            <input
              type="checkbox"
              checked={pendingOnly}
              onChange={event => setPendingOnly(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300"
            />
            待处理视图
          </label>
          <label className="flex items-end gap-2 pb-2 text-sm font-medium text-slate-700">
            <input
              type="checkbox"
              checked={outlookOnly}
              onChange={event => setOutlookOnly(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300"
            />
            Outlook-only
          </label>
        </div>
      </section>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(340px,1fr)]">
        <section className="pim-panel min-w-0 overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 px-4 py-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-950">治理对象</h2>
              <p className="mt-1 text-xs text-slate-500">
                包含回收站、同步批次、审计时间线和待确认变更入口。
              </p>
            </div>
            <span className="text-xs text-slate-500">{data?.totalCount ?? 0} 个对象</span>
          </div>

          {isLoading ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">正在加载数据中心对象。</p>
          ) : isError ? (
            <p className="px-4 py-8 text-center text-sm text-red-600">
              {error instanceof Error ? error.message : '数据中心查询失败'}
            </p>
          ) : items.length === 0 ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">当前筛选下没有对象。</p>
          ) : (
            <>
              <div className="overflow-auto overflow-x-auto hidden md:block">
                <table className="min-w-full divide-y divide-slate-200 text-left text-sm">
                <thead className="bg-slate-50 text-xs text-slate-500">
                  <tr>
                    <th className="px-4 py-3 font-semibold">标题</th>
                    <th className="px-4 py-3 font-semibold">对象</th>
                    <th className="px-4 py-3 font-semibold">来源</th>
                    <th className="px-4 py-3 font-semibold">状态</th>
                    <th className="px-4 py-3 font-semibold">开始</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {items.map(item => {
                    const rowKey = `${item.objectType}-${item.objectId}`;

                    return (
                      <tr
                        key={rowKey}
                        onClick={() => selectRow(item)}
                        className={`cursor-pointer transition-colors hover:bg-blue-50 ${
                          selectedKey === rowKey ? 'bg-blue-50' : 'bg-white'
                        }`}
                      >
                        <td className="max-w-[300px] px-4 py-3">
                          <p className="truncate font-medium text-slate-800">{item.title}</p>
                          <p className="mt-1 truncate text-xs text-slate-500">{item.summary}</p>
                        </td>
                        <td className="px-4 py-3 text-slate-600">{readableObjectType(item.objectType)}</td>
                        <td className="px-4 py-3 text-slate-600">{item.source}</td>
                        <td className="px-4 py-3 text-slate-600">{item.status}</td>
                        <td className="whitespace-nowrap px-4 py-3 text-slate-500">{formatDateTime(item.startsAt)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
              </div>
              {/* Mobile card fallback */}
              <div className="grid grid-cols-1 gap-3 p-3 md:grid-cols-2 md:hidden">
                {items.map(item => {
                  const rowKey = `${item.objectType}-${item.objectId}`;
                  return (
                    <button
                      key={rowKey}
                      type="button"
                      onClick={() => selectRow(item)}
                      className={`rounded-lg border p-3 text-left transition-colors ${selectedKey === rowKey ? 'border-blue-300 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}
                    >
                      <p className="truncate text-sm font-semibold text-slate-800">{item.title}</p>
                      <p className="mt-1 truncate text-xs text-slate-500">{item.summary}</p>
                      <div className="mt-2 flex flex-wrap gap-1.5 text-[11px] text-slate-500">
                        <span className="rounded bg-slate-100 px-1.5 py-0.5">{readableObjectType(item.objectType)}</span>
                        <span className="rounded bg-slate-100 px-1.5 py-0.5">{item.source}</span>
                        <span className="rounded bg-slate-100 px-1.5 py-0.5">{item.status}</span>
                      </div>
                      <p className="mt-1 text-[11px] text-slate-400">{formatDateTime(item.startsAt)}</p>
                    </button>
                  );
                })}
              </div>
            </>
          )}
        </section>

        <div className="space-y-4">
          <section className="pim-panel min-w-0 p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-sm font-semibold text-slate-950">对象详情</h2>
              {selected && (
                <Link
                  to={`/audit/${encodeURIComponent(selected.objectType)}/${encodeURIComponent(selected.objectId)}`}
                  className="pim-button-secondary px-3 py-1.5 text-xs"
                >
                  审计时间线
                </Link>
              )}
            </div>

            {selected ? (
              <div className="mt-4 space-y-3 text-sm">
                <dl className="grid grid-cols-1 gap-2">
                  {[
                    ['标题', selected.title],
                    ['对象 ID', selected.objectId],
                    ['对象类型', readableObjectType(selected.objectType)],
                    ['来源', selected.source],
                    ['状态', selected.status],
                    ['开始', formatDateTime(selected.startsAt)],
                    ['结束', formatDateTime(selected.endsAt)],
                    ['摘要', selected.summary],
                  ].map(([label, value]) => (
                    <div key={label} className="rounded-lg bg-slate-50 px-3 py-2">
                      <dt className="text-xs font-semibold text-slate-400">{label}</dt>
                      <dd className="mt-1 break-words text-slate-800">{value}</dd>
                    </div>
                  ))}
                </dl>

                <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                  <button type="button" className="pim-button-secondary px-3 py-2 text-sm">
                    回收站
                  </button>
                  <button
                    type="button"
                    onClick={() => restorePreviewMutation.mutate(selected.objectId)}
                    disabled={restorePreviewMutation.isPending}
                    className="pim-button-secondary px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    版本恢复
                  </button>
                </div>

                {restorePreviewMutation.data && (
                  <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                    <p className="font-semibold">恢复预览</p>
                    <p className="mt-1 text-xs leading-5">{restorePreviewMutation.data.summary}</p>
                  </div>
                )}
                {exportMutation.data && (
                  <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
                    已生成审计导出：{exportMutation.data.fileName}
                  </div>
                )}
              </div>
            ) : (
              <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-8 text-center text-sm text-slate-500">
                选择一条记录以查看审计、恢复和批量治理入口。
              </p>
            )}
          </section>

          <DataCenterBatchPreview selected={selected} />
        </div>
      </div>

      {filterOpen && (
        <div className="fixed inset-0 z-40 flex justify-end lg:hidden">
          <div className="absolute inset-0 bg-slate-950/30" onClick={() => setFilterOpen(false)} />
          <div className="relative flex h-full w-full max-w-[420px] flex-col overflow-auto bg-white p-4 shadow-xl">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-slate-800">筛选</h3>
              <button type="button" className="text-xs text-slate-500 hover:text-slate-700" onClick={() => setFilterOpen(false)}>
                关闭
              </button>
            </div>
            <div className="mt-4 grid grid-cols-1 gap-3">
              <label className="min-w-0">
                <span className="text-xs font-semibold text-slate-500">全局搜索</span>
                <input
                  value={search}
                  onChange={event => setSearch(event.target.value)}
                  placeholder="标题、摘要、来源对象"
                  className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
                />
              </label>
              <label>
                <span className="text-xs font-semibold text-slate-500">对象过滤</span>
                <select
                  value={objectType}
                  onChange={event => setObjectType(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
                >
                  <option value="">全部对象</option>
                  <option value="event">日程</option>
                  <option value="task">任务</option>
                  <option value="habit">习惯</option>
                  <option value="reminder">提醒</option>
                </select>
              </label>
              <label>
                <span className="text-xs font-semibold text-slate-500">来源过滤</span>
                <select
                  value={source}
                  onChange={event => setSource(event.target.value)}
                  disabled={outlookOnly}
                  className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400 disabled:bg-slate-50"
                >
                  <option value="">全部来源</option>
                  <option value="pim">PIM</option>
                  <option value="outlook">Outlook</option>
                </select>
              </label>
              <label className="flex items-center gap-2 text-sm font-medium text-slate-700">
                <input type="checkbox" checked={pendingOnly} onChange={event => setPendingOnly(event.target.checked)} className="h-4 w-4 rounded border-slate-300" />
                待处理视图
              </label>
              <label className="flex items-center gap-2 text-sm font-medium text-slate-700">
                <input type="checkbox" checked={outlookOnly} onChange={event => setOutlookOnly(event.target.checked)} className="h-4 w-4 rounded border-slate-300" />
                Outlook-only
              </label>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
