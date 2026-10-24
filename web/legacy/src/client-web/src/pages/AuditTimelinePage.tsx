import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  exportAudit,
  getAuditTimeline,
  getRestorePreview,
} from '../api/operations';
import BeforeAfterDiff from '../components/schedule/BeforeAfterDiff';
import { safeChangedFields } from '../utils/eventFieldDiff';
import type { AuditVersion } from '../types';
import PageHeader from '../ui/PageHeader';

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function versionTitle(version: AuditVersion) {
  const labels = safeChangedFields(version.changedFields).map(item => item.label);
  const fields = labels.length > 0 ? labels.join('、') : '无字段摘要';
  return `${version.source ?? 'PIM'} · ${fields}`;
}

export default function AuditTimelinePage() {
  const params = useParams();
  const objectType = params.objectType ?? 'task';
  const objectId = params.objectId ?? '';
  const [selectedVersionId, setSelectedVersionId] = useState<string | null>(null);
  const [filterOpen, setFilterOpen] = useState(false);
  const [sourceFilter, setSourceFilter] = useState('');

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['audit-timeline', objectType, objectId],
    queryFn: () => getAuditTimeline(objectType, objectId),
    enabled: objectId.length > 0,
  });

  const restorePreviewMutation = useMutation({
    mutationFn: (auditVersionId: string) => getRestorePreview(auditVersionId),
  });

  const exportMutation = useMutation({
    mutationFn: exportAudit,
  });

  const versions = data?.items ?? [];
  const selectedVersion = useMemo(
    () => versions.find(item => item.id === selectedVersionId) ?? versions[0],
    [selectedVersionId, versions],
  );

  useEffect(() => {
    if (!selectedVersionId && versions[0]) {
      setSelectedVersionId(versions[0].id);
    }
  }, [selectedVersionId, versions]);

  return (
    <div className="mx-auto w-full max-w-[1300px] space-y-4 pb-20">
      <PageHeader
        title="审计时间线"
        subtitle={`${objectType} / ${objectId || '未指定对象'} 的版本历史、字段差异、恢复预览与导出审计。`}
        actions={
          <button
            type="button"
            onClick={() => exportMutation.mutate()}
            disabled={exportMutation.isPending}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            导出审计
          </button>
        }
      />

      <div className="flex justify-end xl:hidden">
        <button type="button" onClick={() => setFilterOpen(true)} className="pim-button-secondary px-3 py-2 text-sm">
          筛选
        </button>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(320px,0.9fr)_minmax(0,1.5fr)]">
        <section className="pim-panel min-w-0 overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 px-4 py-3">
            <h2 className="text-sm font-semibold text-slate-950">版本记录</h2>
            <span className="text-xs text-slate-500">{versions.length} 条</span>
          </div>

          {isLoading ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">正在加载审计时间线。</p>
          ) : isError ? (
            <p className="px-4 py-8 text-center text-sm text-red-600">
              {error instanceof Error ? error.message : '审计时间线加载失败'}
            </p>
          ) : versions.length === 0 ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">暂无审计版本。</p>
          ) : (
            <div className="divide-y divide-slate-100">
              {versions.map(version => (
                <button
                  key={version.id}
                  type="button"
                  onClick={() => setSelectedVersionId(version.id)}
                  className={`block w-full px-4 py-3 text-left transition-colors hover:bg-blue-50 ${
                    selectedVersion?.id === version.id ? 'bg-blue-50' : 'bg-white'
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <p className="min-w-0 truncate text-sm font-semibold text-slate-800">{versionTitle(version)}</p>
                    <span className="shrink-0 text-[11px] font-semibold text-slate-500">
                      {formatDateTime(version.createdAt)}
                    </span>
                  </div>
                  <p className="mt-1 truncate text-xs text-slate-500">
                    操作人：{version.actor ?? '系统'} / 确认：{version.confirmationId ?? '无'}
                  </p>
                </button>
              ))}
            </div>
          )}
        </section>

        <section className="pim-panel min-w-0 p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-950">版本详情</h2>
              <p className="mt-1 text-xs text-slate-500">{selectedVersion?.id ?? '未选择版本'}</p>
            </div>
            {selectedVersion && (
              <button
                type="button"
                onClick={() => restorePreviewMutation.mutate(selectedVersion.id)}
                disabled={restorePreviewMutation.isPending}
                className="pim-button-secondary px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
              >
                恢复预览
              </button>
            )}
          </div>

          {selectedVersion ? (
            <div className="mt-4 space-y-4">
              <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
                {[
                  ['对象类型', selectedVersion.objectType],
                  ['对象 ID', selectedVersion.objectId],
                  ['来源', selectedVersion.source ?? 'PIM'],
                  ['确认 ID', selectedVersion.confirmationId ?? '无'],
                  ['操作人', selectedVersion.actor ?? '系统'],
                  ['创建时间', formatDateTime(selectedVersion.createdAt)],
                ].map(([label, value]) => (
                  <div key={label} className="rounded-lg bg-slate-50 px-3 py-2">
                    <p className="text-xs font-semibold text-slate-400">{label}</p>
                    <p className="mt-1 break-words text-sm text-slate-800">{value}</p>
                  </div>
                ))}
              </div>

              {restorePreviewMutation.data && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
                  <p className="font-semibold">恢复预览</p>
                  <p className="mt-1 text-xs leading-5">{restorePreviewMutation.data.summary}</p>
                  <p className="mt-2 text-xs">需要确认：{restorePreviewMutation.data.requiresConfirmation ? '是' : '否'}</p>
                </div>
              )}

              {exportMutation.data && (
                <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-800">
                  导出审计文件：{exportMutation.data.fileName}
                </div>
              )}

              <BeforeAfterDiff
                beforeJson={selectedVersion.beforeJson}
                afterJson={selectedVersion.afterJson}
              />
            </div>
          ) : (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              请选择一个版本查看差异。
            </p>
          )}
        </section>
      </div>

      {filterOpen && (
        <div className="fixed inset-0 z-40 flex justify-end xl:hidden">
          <div className="absolute inset-0 bg-slate-950/30" onClick={() => setFilterOpen(false)} />
          <div className="relative flex h-full w-full max-w-[420px] flex-col overflow-auto bg-white p-4 shadow-xl">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-slate-800">筛选</h3>
              <button type="button" className="text-xs text-slate-500 hover:text-slate-700" onClick={() => setFilterOpen(false)}>
                关闭
              </button>
            </div>
            <div className="mt-4 space-y-3">
              <label className="block">
                <span className="text-xs font-semibold text-slate-500">来源过滤</span>
                <select value={sourceFilter} onChange={e => setSourceFilter(e.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
                  <option value="">全部来源</option>
                  <option value="pim">PIM</option>
                  <option value="outlook">Outlook</option>
                  <option value="manual">手动</option>
                </select>
              </label>
              <p className="text-xs text-slate-400">时间线为单列布局，小屏下自动单列展示，筛选项在抽屉内完成。</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
