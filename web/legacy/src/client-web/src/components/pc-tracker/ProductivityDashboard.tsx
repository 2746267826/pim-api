import { useQuery } from '@tanstack/react-query';
import { formatPcDate, getPcBusinessDate } from '../../utils/pcBusinessDay';
import { getProductivityDashboard } from '../../api/pcTracker';
import type { PcFocusBlocksResponse, PcLateNightResponse } from '../../api/pcTracker';
import type { DerivedMetrics } from '../../types';
import EChartBox from '../charts/EChartBox';
import {
  buildFocusGaugeOption,
  buildWeeklyTrendOption,
  parseDurationToMinutes,
} from '../charts/pcPanelOptions';

export interface ProductivityDashboardPanelProps {
  /** 专注块聚合数据（PcTrackerPage 已查询，页面传入） */
  focusBlocks?: PcFocusBlocksResponse;
  /** 深夜使用聚合数据（页面传入） */
  lateNight?: PcLateNightResponse;
  /** summary.metrics：提供记录总时长（字符串）与上下文切换次数（页面传入） */
  summaryMetrics?: DerivedMetrics | null;
  /** 业务日期字符串（yyyy-MM-dd，缺省取今天）；dashboard/周趋势请求与 query key 都随它走 */
  dateStr?: string;
}

function MetricLine({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg bg-slate-50 border border-slate-100 px-3 py-2">
      <div className="text-xs text-slate-500">{label}</div>
      <div className="text-lg font-semibold text-slate-800 mt-0.5">{value}</div>
    </div>
  );
}

export default function ProductivityDashboardPanel({
  focusBlocks,
  lateNight,
  summaryMetrics,
  dateStr,
}: ProductivityDashboardPanelProps) {
  const today = dateStr ?? formatPcDate(getPcBusinessDate());

  const { data, isLoading } = useQuery({
    queryKey: ['productivity-dashboard', today],
    queryFn: () => getProductivityDashboard(today),
  });

  if (isLoading) {
    return (
      <div className="pim-panel p-4">
        <h3 className="text-sm font-semibold text-slate-800 mb-3">专注概况</h3>
        <div className="text-sm text-slate-400 text-center py-4">加载中...</div>
      </div>
    );
  }

  if (!data) return null;

  const focusItems = focusBlocks?.items ?? [];
  const longestMinutes = focusItems.length > 0 ? Math.max(...focusItems.map(block => block.durationMinutes)) : null;
  const totalRecordedMinutes = summaryMetrics ? parseDurationToMinutes(summaryMetrics.totalRecordedDuration) : 0;
  const switchRatePerHour =
    summaryMetrics && totalRecordedMinutes > 0
      ? summaryMetrics.appSwitchCount / (totalRecordedMinutes / 60)
      : null;

  let lateMinutes: number | null = null;
  const lateItems = lateNight?.items ?? [];
  if (lateItems.length > 0) {
    const lastActivity = [...lateItems].reverse().find(item => item.hadActivity);
    const pick = lastActivity ?? lateItems[lateItems.length - 1];
    lateMinutes = pick.minutes;
  }

  return (
    <div className="pim-panel p-4">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-sm font-semibold text-slate-800">专注概况</h3>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-[220px_minmax(0,1fr)] items-center">
        <EChartBox option={buildFocusGaugeOption(focusBlocks, summaryMetrics ?? undefined)} height={200} ariaLabel="专注占比仪表" />
        <div className="grid grid-cols-2 gap-3">
          <MetricLine label="最长专注" value={longestMinutes !== null ? `${longestMinutes} 分钟` : '—'} />
          <MetricLine label="专注块数" value={focusItems.length > 0 ? `${focusItems.length} 段` : '—'} />
          <MetricLine
            label="碎片化"
            value={switchRatePerHour !== null ? `${switchRatePerHour.toFixed(1)} 次/时` : '—'}
          />
          <MetricLine label="深夜使用" value={lateMinutes !== null ? `${lateMinutes} 分钟` : '—'} />
        </div>
      </div>

      <div className="mt-4">
        <h4 className="text-xs font-medium text-slate-500 mb-2">本周趋势</h4>
        <EChartBox option={buildWeeklyTrendOption(data.weeklyTrend)} height={120} ariaLabel="本周记录时长趋势" />
      </div>
    </div>
  );
}
