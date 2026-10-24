import { useQuery } from '@tanstack/react-query';
import { getHealthStatusLabel, getStatusSummary } from '../../api/status';
import { getDeferredAutoRefreshInterval } from '../../lib/autoRefresh';
import type { PimHealthStatus } from '../../types';

const statusClasses: Record<PimHealthStatus, { dot: string; text: string }> = {
  Healthy: { dot: 'bg-emerald-500', text: 'text-emerald-700' },
  Warning: { dot: 'bg-amber-500', text: 'text-amber-700' },
  Critical: { dot: 'bg-red-500', text: 'text-red-700' },
  Unknown: { dot: 'bg-slate-400', text: 'text-slate-600' },
};

export default function SidebarStatusIndicator() {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['status-summary'],
    queryFn: getStatusSummary,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const status = isError ? 'Unknown' : data?.status ?? 'Unknown';
  const label = isLoading
    ? '检查中'
    : isError
      ? '未知'
      : data?.label || getHealthStatusLabel(status);
  const message = isLoading
    ? '正在检查系统状态...'
    : isError
      ? '系统状态暂不可用'
      : data?.message || '系统状态暂不可用';
  const classes = statusClasses[status];
  const statusLabel = getHealthStatusLabel(status);

  return (
    <div
      className="mx-3 mb-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2"
      role="status"
      aria-label={`系统状态：${statusLabel}，${message}`}
    >
      <div className="flex min-w-0 items-center gap-2">
        <span className={`h-2.5 w-2.5 shrink-0 rounded-full ${classes.dot}`} aria-hidden="true" />
        <span className={`truncate text-xs font-semibold ${classes.text}`}>{label}</span>
      </div>
      <p className="mt-1 truncate text-[11px] leading-4 text-slate-500" title={message}>
        {message}
      </p>
    </div>
  );
}
