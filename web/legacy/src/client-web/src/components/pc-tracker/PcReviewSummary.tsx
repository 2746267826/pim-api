import MetricCard from '../../ui/MetricCard';
import { buildReviewMetrics } from '../charts/pcPanelOptions';
import type { ActivityClassificationSuggestion, PcSummaryResponse } from '../../types';
import type {
  PcCategoryDistributionResponse,
  PcFocusBlocksResponse,
  PcLateNightResponse,
} from '../../api/pcTracker';

interface Props {
  summary: PcSummaryResponse | undefined;
  pendingSuggestions: ActivityClassificationSuggestion[];
  focusBlocks?: PcFocusBlocksResponse;
  lateNight?: PcLateNightResponse;
  categoryDistribution?: PcCategoryDistributionResponse;
  /** 页面业务日期字符串（yyyy-MM-dd），深夜使用取当日条目 */
  dateStr?: string;
}

export default function PcReviewSummary({
  summary,
  focusBlocks,
  lateNight,
  categoryDistribution,
  dateStr,
}: Props) {
  const metrics = summary?.metrics;
  const cards = buildReviewMetrics(summary, focusBlocks, lateNight, categoryDistribution, dateStr);

  return (
    <section className="pim-panel p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-base font-semibold text-slate-950">今日复盘</h2>
          <p className="mt-1 text-sm text-slate-500">
            先看今天的活动结构，再确认需要写入 App 知识库的上下文。
          </p>
        </div>
        {metrics?.mostFocusedApp && (
          <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-xs font-medium text-blue-700">
            最聚焦：{metrics.mostFocusedApp}
          </div>
        )}
      </div>
      <div className="mt-4 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-7">
        {cards.map(card => (
          <MetricCard
            key={card.label}
            label={card.label}
            value={card.value}
            helper={card.helper}
            tone={card.tone}
          />
        ))}
      </div>
    </section>
  );
}
