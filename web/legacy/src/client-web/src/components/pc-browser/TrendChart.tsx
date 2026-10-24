import { useMemo } from 'react';
import EChartBox from '../charts/EChartBox';
import type { PcBrowserDailyItem } from '../../api/pcBrowserSite';
import { buildBrowserTrendOption } from './pcBrowserChartOptions';

interface Props {
  daily: PcBrowserDailyItem[];
  isLoading: boolean;
}

/** 范围趋势：按日总专注时长的柱状图（数据源 daily 已按 host 拆分，这里聚合成每日一根柱）。 */
export default function TrendChart({ daily, isLoading }: Props) {
  const option = useMemo(() => buildBrowserTrendOption(daily), [daily]);

  if (isLoading) {
    return <div className="h-[240px] animate-pulse rounded-md bg-slate-100" />;
  }

  if (!daily.length) {
    return (
      <div className="py-10 text-center text-sm text-slate-400">
        所选范围内暂无浏览数据
      </div>
    );
  }

  const dayCount = new Set(daily.map(item => item.date)).size;
  const height = Math.max(240, dayCount * 6 + 120);

  return (
    <EChartBox
      option={option}
      height={height}
      ariaLabel="浏览器使用每日专注时长趋势图"
    />
  );
}
