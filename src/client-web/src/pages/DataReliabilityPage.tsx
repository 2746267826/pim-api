import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import PageHeader from '../ui/PageHeader';
import DataReliabilityPanel from '../components/data-reliability/DataReliabilityPanel';
import DataReliabilityRuleDialog from '../components/data-reliability/DataReliabilityRuleDialog';
import {
  getDataReliabilityInspection,
  refreshDataReliabilityInspection,
} from '../api/dataReliability';
import type { DataReliabilityRuleReport } from '../api/dataReliabilityTypes';
import { formatDateTime, isInspectionStale } from '../components/data-reliability/dataReliabilityModel';

const inspectionQueryKey = ['data-reliability-inspection'] as const;

/**
 * 设置 → 数据可信度（#261）。
 * 默认读最近一次体检结果（不触发全量扫库），只有点「重新体检」才真正执行。
 * 「重新体检」始终在页头可见 —— 首次上线还没有任何体检结果时，用户也能一键跑出来，而不是对着一片未知发愣。
 * 只读：不提供任何一键修复入口。
 */
export default function DataReliabilityPage() {
  const queryClient = useQueryClient();
  const [selectedRule, setSelectedRule] = useState<DataReliabilityRuleReport | null>(null);

  const inspection = useQuery({
    queryKey: inspectionQueryKey,
    queryFn: getDataReliabilityInspection,
    retry: false,
  });

  const refresh = useMutation({
    mutationFn: refreshDataReliabilityInspection,
    onSuccess: data => {
      queryClient.setQueryData(inspectionQueryKey, data);
      toast.success('体检完成');
    },
    onError: error => {
      toast.error(error instanceof Error ? error.message : '体检失败，请稍后重试');
    },
  });

  const report = inspection.data ?? null;
  const now = new Date();
  const stale = report ? isInspectionStale(report.inspectedAtUtc, now) : false;
  const errorMessage = inspection.isError
    ? inspection.error instanceof Error
      ? inspection.error.message
      : '读取体检结果失败'
    : null;

  return (
    <div className="mx-auto max-w-5xl space-y-4 pb-20">
      <PageHeader
        title="数据可信度"
        subtitle="13 项只读体检尺子：数据自洽 / 覆盖完整 / 链路健康"
        actions={
          <button
            type="button"
            onClick={() => refresh.mutate()}
            disabled={refresh.isPending}
            className="min-h-[44px] rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-slate-800 disabled:opacity-50"
          >
            {refresh.isPending ? '体检中…' : '重新体检'}
          </button>
        }
      />

      {stale && report && (
        <p
          role="status"
          className="pim-card border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800"
        >
          本次体检结果已过期（体检时间 {formatDateTime(report.inspectedAtUtc)}，超过 26 小时），
          下面的红黄绿可能已经不代表现状，建议点右上角「重新体检」跑一次。
        </p>
      )}

      {report && <DataReliabilityPanel report={report} now={now} onSelectRule={setSelectedRule} />}

      {inspection.isLoading && !report && (
        <div className="pim-card p-6 text-sm text-slate-600">正在读取最近一次体检结果…</div>
      )}

      {errorMessage && (
        <div className="pim-card space-y-3 p-6 text-sm text-red-700">
          <p role="alert">读取体检结果失败：{errorMessage}</p>
          <button
            type="button"
            onClick={() => inspection.refetch()}
            className="min-h-[44px] rounded-lg border border-slate-300 px-4 py-2 text-slate-700"
          >
            重试
          </button>
        </div>
      )}

      {!inspection.isLoading && !errorMessage && !report && (
        <div className="pim-card space-y-2 p-6 text-sm text-slate-600">
          <p>暂无数据：还没有可用的体检结果。</p>
          <p className="text-xs text-slate-500">
            点右上角「重新体检」立即跑一次；体检完成后这里会显示 13 条尺子的红黄绿、违规样例与历史欠账。
          </p>
        </div>
      )}

      {refresh.isError && (
        <p role="alert" className="px-1 text-sm text-red-700">
          重新体检失败：
          {refresh.error instanceof Error ? refresh.error.message : '未知错误'}
        </p>
      )}

      <DataReliabilityRuleDialog rule={selectedRule} onClose={() => setSelectedRule(null)} />
    </div>
  );
}
