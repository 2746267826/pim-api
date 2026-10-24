import { formatDateTime, formatDuration, formatPercent } from './mobileFormatting';

export interface MobileMetricStripProps {
  totalForegroundSeconds: number;
  appSwitchCount: number;
  appsUsed: number;
  completeness: number;
  qualityIssueCount: number;
  lastSyncAt: string | null;
  fallbackForegroundSeconds?: number;
}

function MetricItem({
  label,
  value,
  helper,
}: {
  label: string;
  value: string | number;
  helper?: string;
}) {
  return (
    <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-4">
      <dt className="truncate text-xs text-slate-500">{label}</dt>
      <dd className="mt-2 break-words text-xl font-semibold text-slate-950">{value}</dd>
      {helper && <p className="mt-2 truncate text-xs text-slate-400">{helper}</p>}
    </div>
  );
}

export default function MobileMetricStrip({
  totalForegroundSeconds,
  appSwitchCount,
  appsUsed,
  completeness,
  qualityIssueCount,
  lastSyncAt,
  fallbackForegroundSeconds = 0,
}: MobileMetricStripProps) {
  const summaryMode = fallbackForegroundSeconds > 0 ? 'fallback' : 'events';

  return (
    <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-6" data-summary-mode={summaryMode}>
      <MetricItem
        label="总前台时长"
        value={formatDuration(totalForegroundSeconds)}
        helper={fallbackForegroundSeconds > 0 ? `回退汇总 ${formatDuration(fallbackForegroundSeconds)}` : '事件明细'}
      />
      <MetricItem label="切换次数" value={appSwitchCount} />
      <MetricItem label="使用 App 数" value={appsUsed} />
      <MetricItem label="完整度" value={formatPercent(completeness)} />
      <MetricItem label="质量问题" value={qualityIssueCount} />
      <MetricItem label="最后同步" value={formatDateTime(lastSyncAt)} />
    </dl>
  );
}
