import { useMemo } from 'react';
import EChartBox from '../charts/EChartBox';
import type { PcBrowserTimelineItem } from '../../api/pcBrowserSite';
import { buildBrowserDayTimelineOption } from './pcBrowserChartOptions';

interface Props {
  items: PcBrowserTimelineItem[];
  isLoading: boolean;
}

/** 单日时段分布：每行一个 host 的 0-24 时横向时间条，空数据显示空态提示。 */
export default function DayTimeline({ items, isLoading }: Props) {
  const option = useMemo(() => buildBrowserDayTimelineOption(items), [items]);

  if (isLoading) {
    return <div className="h-[220px] animate-pulse rounded-md bg-slate-100" />;
  }

  if (!items.length) {
    return (
      <div className="py-10 text-center text-sm text-slate-400">
        当日暂无浏览时段数据
      </div>
    );
  }

  const hostCount = new Set(items.map(item => item.host)).size;
  const height = Math.max(160, hostCount * 36 + 60);

  return (
    <EChartBox
      option={option}
      height={height}
      ariaLabel="浏览器使用时段分布图"
    />
  );
}
