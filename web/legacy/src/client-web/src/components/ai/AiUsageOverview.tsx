import type { AiUsageGroup, AiUsageSummary } from '../../types';
import MetricCard from '../../ui/MetricCard';

interface Props {
  summary?: AiUsageSummary;
  isLoading?: boolean;
  error?: Error | null;
}

function formatNumber(value?: number | null) {
  return (value ?? 0).toLocaleString('zh-CN');
}

function formatCost(value?: number | null) {
  return `$${(value ?? 0).toFixed(4)}`;
}

function failureRate(summary?: AiUsageSummary) {
  if (!summary || summary.requestCount === 0) return '0.0%';
  return `${((summary.failureCount / summary.requestCount) * 100).toFixed(1)}%`;
}

function CompactGroupRows({ title, groups }: { title: string; groups: AiUsageGroup[] }) {
  const visible = groups.slice(0, 5);

  return (
    <div className="min-w-0">
      <h3 className="mb-2 text-xs font-semibold text-slate-500">{title}</h3>
      {visible.length === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-3 py-4 text-sm text-slate-500">
          暂无数据
        </p>
      ) : (
        <div className="space-y-2">
          {visible.map(group => (
            <div key={group.groupKey} className="min-w-0 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2">
              <div className="flex items-center justify-between gap-3">
                <span className="min-w-0 truncate text-sm font-medium text-slate-800">{group.groupKey || '未命名'}</span>
                <span className="shrink-0 text-xs tabular-nums text-slate-500">{formatNumber(group.requestCount)} 次</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs text-slate-500">
                <span>成功 {formatNumber(group.successCount)}</span>
                <span>失败 {formatNumber(group.failureCount)}</span>
                <span>Token {formatNumber(group.totalTokens)}</span>
                <span>{formatCost(group.estimatedCost)}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default function AiUsageOverview({ summary, isLoading = false, error = null }: Props) {
  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">用量概览</h2>
          <p className="mt-1 text-xs text-slate-500">请求量、Token、成本与失败率</p>
        </div>
        {isLoading && <span className="text-xs text-slate-400">正在刷新...</span>}
      </div>

      {error && <p className="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error.message}</p>}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <MetricCard label="请求数" value={formatNumber(summary?.requestCount)} helper={`成功 ${formatNumber(summary?.successCount)}`} tone="primary" />
        <MetricCard label="总 Token" value={formatNumber(summary?.totalTokens)} helper={`输入 ${formatNumber(summary?.promptTokens)}`} tone="activity" />
        <MetricCard label="预估成本" value={formatCost(summary?.estimatedCost)} helper="按网关返回值汇总" />
        <MetricCard label="失败率" value={failureRate(summary)} helper={`失败 ${formatNumber(summary?.failureCount)}`} tone={(summary?.failureCount ?? 0) > 0 ? 'warning' : 'neutral'} />
      </div>

      <div className="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
        <CompactGroupRows title="Top 模块" groups={summary?.byModule ?? []} />
        <CompactGroupRows title="Top 模型" groups={summary?.byModel ?? []} />
      </div>
    </section>
  );
}
