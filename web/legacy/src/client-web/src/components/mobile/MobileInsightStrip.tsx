import type { MobileAnalyticsOverview } from '../../api/mobile';
import {
  formatCompactDuration,
  formatDateTime,
  formatDuration,
  formatNumber,
  formatPercent,
  formatSignedPercent,
} from './mobileFormatting';
import MobileMetricGrid, { type MobileMetricItem } from './MobileMetricGrid';

export interface MobileInsightStripProps {
  overview?: MobileAnalyticsOverview;
  isLoading?: boolean;
}

export default function MobileInsightStrip({ overview, isLoading = false }: MobileInsightStripProps) {
  const goal = overview?.goalProgress;
  const goalTone: MobileMetricItem['tone'] = goal?.isOverLimit ? 'warning' : goal ? 'good' : 'default';
  const items: MobileMetricItem[] = [
    {
      label: '总使用时长',
      value: isLoading ? '加载中' : formatDuration(overview?.totalForegroundSeconds),
      helper: overview ? `较上期 ${formatSignedPercent(overview.previousPeriodChange)}` : '等待分析数据',
    },
    {
      label: '日均',
      value: formatDuration(overview?.dailyAverageSeconds),
      helper: overview?.highestUseLocalDate ? `峰值日 ${overview.highestUseLocalDate}` : '近 7 天均值',
    },
    {
      label: '目标',
      value: goal ? `${formatCompactDuration(goal.usedSeconds)} / ${formatCompactDuration(goal.limitSeconds)}` : '未设置',
      helper: goal ? goal.label : '可配置每日目标',
      tone: goalTone,
    },
    {
      label: 'App 数',
      value: formatNumber(overview?.appCount),
      helper: `切换 ${formatNumber(overview?.switchOrPickupCount)} 次`,
    },
    {
      label: '完整度',
      value: formatPercent(overview?.completeness),
      helper: `事件覆盖 ${formatPercent(overview?.quality.usageEventsCoverage)}`,
      tone: (overview?.completeness ?? 0) >= 0.9 ? 'good' : 'warning',
    },
    {
      label: '最近同步',
      value: formatDateTime(overview?.quality.lastSyncAt).split(' ')[0] ?? '-',
      helper: overview?.isStale ? '数据可能过期' : `生成 ${formatDateTime(overview?.generatedAt)}`,
    },
  ];

  return (
    <section className="mx-auto max-w-[1500px] px-4 sm:px-6">
      <MobileMetricGrid items={items} />
    </section>
  );
}
