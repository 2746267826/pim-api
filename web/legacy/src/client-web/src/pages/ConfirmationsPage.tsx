import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  confirmOperation,
  confirmOperationSecondLevel,
  confirmOperationStrict,
  getConfirmationDetail,
  getPendingConfirmations,
  rejectOperation,
} from '../api/operations';
import BeforeAfterDiff from '../components/schedule/BeforeAfterDiff';
import StrictConfirmationPanel from '../components/schedule/StrictConfirmationPanel';
import { safeChangedFields, safeExternalEffectText } from '../utils/eventFieldDiff';
import MobilePageHeader from '../ui/MobilePageHeader';
import PageHeader from '../ui/PageHeader';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function actionError(error: unknown) {
  return error instanceof Error ? error.message : '操作失败';
}

export function getConfirmActionState(requiresSecondLevel: boolean, secondLevelArmed: boolean) {
  return {
    label: requiresSecondLevel && secondLevelArmed ? 'Confirm final' : 'Confirm',
    requiresArm: requiresSecondLevel && !secondLevelArmed,
  };
}

export default function ConfirmationsPage() {
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [secondLevelArmedId, setSecondLevelArmedId] = useState<string | null>(null);

  const { data: confirmations = [], isLoading } = useQuery({
    queryKey: ['pending-confirmations'],
    queryFn: getPendingConfirmations,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  useEffect(() => {
    if (!selectedId && confirmations[0]) {
      setSelectedId(confirmations[0].id);
    }
  }, [confirmations, selectedId]);

  useEffect(() => {
    setSecondLevelArmedId(null);
  }, [selectedId]);

  const { data: detail, isLoading: detailLoading } = useQuery({
    queryKey: ['confirmation-detail', selectedId],
    queryFn: () => getConfirmationDetail(selectedId!),
    enabled: selectedId !== null,
  });

  const confirmMutation = useMutation({
    mutationFn: (id: string) => {
      const target = detail ?? confirmations.find(item => item.id === id);
      if (target?.requiresStrictConfirmation || target?.riskLevel === 'L4BatchOrDestructiveGovernance') {
        return confirmOperationStrict(id);
      }

      if (target?.requiresSecondLevelConfirmation) {
        return confirmOperationSecondLevel(id);
      }

      return confirmOperation(id);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pending-confirmations'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-pending-confirmations'] });
      queryClient.invalidateQueries({ queryKey: ['confirmation-detail'] });
    },
  });

  const rejectMutation = useMutation({
    mutationFn: rejectOperation,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pending-confirmations'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-pending-confirmations'] });
      queryClient.invalidateQueries({ queryKey: ['confirmation-detail'] });
    },
  });

  const active = detail ?? confirmations.find(item => item.id === selectedId);
  const changedFieldItems = active ? safeChangedFields(active.changedFields) : [];
  const busy = confirmMutation.isPending || rejectMutation.isPending;
  const confirmActionState = getConfirmActionState(
    Boolean(active?.requiresSecondLevelConfirmation),
    active !== undefined && secondLevelArmedId === active.id,
  );

  function handleConfirmActive() {
    if (!active || busy) return;

    if (confirmActionState.requiresArm) {
      setSecondLevelArmedId(active.id);
      return;
    }

    confirmMutation.mutate(active.id, {
      onSuccess: () => setSecondLevelArmedId(null),
    });
  }

  function handleRejectActive() {
    if (!active || busy) return;

    rejectMutation.mutate(active.id, {
      onSuccess: () => setSecondLevelArmedId(null),
    });
  }

  return (
    <div className="mx-auto w-full max-w-[1400px] space-y-4 overflow-auto pb-20 md:pb-4">
      <MobilePageHeader title="确认中心" />
      <PageHeader
        title="确认中心"
        subtitle="在日程、同步、数据中心变更执行前复核影响对象、来源、回写影响和恢复路径。"
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(320px,0.9fr)_minmax(0,1.6fr)]">
        <section className="pim-panel min-w-0 overflow-hidden">
          <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
            <h2 className="text-sm font-semibold text-slate-950">待确认列表</h2>
            <span className="text-xs text-slate-500">{confirmations.length} 个待处理</span>
          </div>
          {isLoading ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">正在加载确认项。</p>
          ) : confirmations.length === 0 ? (
            <p className="px-4 py-8 text-center text-sm text-slate-500">暂无待确认项。</p>
          ) : (
            <div className="divide-y divide-slate-100">
              {confirmations.map(item => (
                <button
                  key={item.id}
                  type="button"
                  onClick={() => setSelectedId(item.id)}
                  className={`block w-full px-4 py-3 text-left transition-colors hover:bg-blue-50 ${
                    selectedId === item.id ? 'bg-blue-50' : 'bg-white'
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <p className="min-w-0 truncate text-sm font-semibold text-slate-800">{item.summary}</p>
                    <span className="shrink-0 rounded-full bg-amber-50 px-2 py-0.5 text-[11px] font-semibold text-amber-700">
                      {item.riskLevel}
                    </span>
                  </div>
                  <p className="mt-1 truncate text-xs text-slate-500">{item.source} / {item.operationType}</p>
                  <p className="mt-1 text-[11px] text-slate-400">过期 {formatDateTime(item.expiresAt)}</p>
                </button>
              ))}
            </div>
          )}
        </section>

        <section className="pim-panel min-w-0 p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-950">详情面板</h2>
              <p className="mt-1 text-xs text-slate-500">
                {detailLoading ? '正在加载所选操作' : active?.id ?? '未选择操作'}
              </p>
            </div>
            {active && (
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  onClick={handleConfirmActive}
                  disabled={busy}
                  className="pim-button-primary min-h-[44px] px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {confirmActionState.label}
                </button>
                <button
                  type="button"
                  onClick={handleRejectActive}
                  disabled={busy}
                  className="pim-button-secondary min-h-[44px] px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                >
                  拒绝
                </button>
              </div>
            )}
          </div>

          {active ? (
            <div className="mt-4 grid grid-cols-1 gap-3 lg:grid-cols-2">
              {active.requiresSecondLevelConfirmation && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800 lg:col-span-2">
                  二级确认：此操作需要复核外部来源、影响对象和回写效果。
                  {secondLevelArmedId === active.id ? ' 最终确认已就绪。' : ' 请先复核详情字段再继续。'}
                </div>
              )}
              <div className="lg:col-span-2">
                <StrictConfirmationPanel
                  confirmation={active}
                  armed={secondLevelArmedId === active.id}
                  onArm={() => setSecondLevelArmedId(active.id)}
                />
              </div>
              <div className="rounded-lg border border-slate-200 bg-white p-3 lg:col-span-2">
                <p className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">摘要</p>
                <p className="mt-2 text-sm font-medium text-slate-800">{active.summary}</p>
              </div>
              {[
                ['风险', active.riskLevel],
                ['来源', active.source],
                ['操作类型', active.operationType],
                ['状态', active.status],
                ['对象类型', active.objectType ?? '暂无'],
                ['对象编号', active.objectId ?? '暂无'],
                ['关联编号', active.correlationId ?? '暂无'],
                ['审计批次', active.auditBatchId ?? '暂无'],
                ['智能建议', active.aiRecommendation ?? '暂无'],
                ['外部回写影响', active.externalEffect ? safeExternalEffectText(active.externalEffect) : '暂无'],
                ['恢复路径', active.recoveryPath ?? '暂无'],
                ['二级确认标记', active.requiresSecondLevelConfirmation ? '需要' : '不需要'],
              ].map(([label, value]) => (
                <div key={label} className="rounded-lg bg-slate-50 px-3 py-2">
                  <p className="text-xs font-semibold text-slate-400">{label}</p>
                  <p className="mt-1 break-words text-sm text-slate-800">{value}</p>
                </div>
              ))}
              <div className="rounded-lg border border-slate-200 bg-white p-3">
                <p className="text-xs font-semibold text-slate-400">变更字段</p>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {changedFieldItems.length > 0 ? (
                    changedFieldItems.map(item => (
                      <span key={item.key} className="rounded-full bg-blue-50 px-2 py-0.5 text-xs font-semibold text-blue-700">
                        {item.label}
                      </span>
                    ))
                  ) : (
                    <span className="text-sm text-slate-500">暂无变更字段。</span>
                  )}
                </div>
              </div>
              <div className="rounded-lg border border-slate-200 bg-white p-3">
                <p className="text-xs font-semibold text-slate-400">allowedActions 可选操作</p>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {(active.allowedActions ?? []).length > 0 ? (
                    active.allowedActions?.map(action => (
                      <span key={action} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-700">
                        {action}
                      </span>
                    ))
                  ) : (
                    <span className="text-sm text-slate-500">待处理项可确认或拒绝。</span>
                  )}
                </div>
              </div>
              <div className="rounded-lg border border-slate-200 bg-white p-3 lg:col-span-2">
                <p className="text-xs font-semibold text-slate-400">操作预览</p>
                {active.previewJson ? (
                  <p className="mt-2 text-sm leading-6 text-slate-700">
                    此操作包含预览数据（原始内容已隐藏），请在变更字段与前后对比中复核后再确认。
                  </p>
                ) : (
                  <p className="mt-2 text-sm text-slate-500">此操作暂无预览数据。</p>
                )}
              </div>
              <div className="lg:col-span-2">
                <BeforeAfterDiff
                  beforeJson={active.beforeJson}
                  afterJson={active.afterJson}
                />
              </div>
            </div>
          ) : (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              选择待确认项以查看请求变更。
            </p>
          )}

          {(confirmMutation.isError || rejectMutation.isError) && (
            <p className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              {actionError(confirmMutation.error ?? rejectMutation.error)}
            </p>
          )}
        </section>
      </div>
    </div>
  );
}
