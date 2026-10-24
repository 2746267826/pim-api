import MetricCard from '../../ui/MetricCard';
import type { PcBrowserSummaryResponse } from '../../api/pcBrowserSite';
import { formatBrowserCount, formatDurationMs } from './pcBrowserFormatting';

interface Props {
  summary: PcBrowserSummaryResponse | undefined;
  isLoading: boolean;
}

/** 汇总卡：总专注时长、访问次数、站点数、媒体播放时长。 */
export default function SummaryCards({ summary, isLoading }: Props) {
  if (isLoading || !summary) {
    return (
      <div className="grid grid-cols-2 gap-4 xl:grid-cols-4">
        {['总专注时长', '访问次数', '站点数', '媒体播放时长'].map(label => (
          <section key={label} className="pim-card p-4">
            <p className="mb-2 truncate text-xs text-slate-500">{label}</p>
            <div className="h-7 animate-pulse rounded bg-slate-100" />
          </section>
        ))}
      </div>
    );
  }

  return (
    <div className="grid grid-cols-2 gap-4 xl:grid-cols-4">
      <MetricCard
        label="总专注时长"
        value={formatDurationMs(summary.totalFocusMs)}
        helper={`统计范围 ${summary.from} ~ ${summary.to}`}
        tone="primary"
      />
      <MetricCard label="访问次数" value={formatBrowserCount(summary.totalVisits)} helper="浏览器页面访问总次数" />
      <MetricCard label="站点数" value={formatBrowserCount(summary.siteCount)} helper="有浏览记录的域名数" tone="activity" />
      <MetricCard
        label="媒体播放时长"
        value={formatDurationMs(summary.totalMediaMs)}
        helper={`后台运行 ${formatDurationMs(summary.totalRunMs)}`}
      />
    </div>
  );
}
