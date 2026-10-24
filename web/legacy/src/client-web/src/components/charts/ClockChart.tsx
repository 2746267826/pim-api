import { useMemo } from 'react';
import EChartBox from './EChartBox';
import { chartColors } from './chartColors';
import type { EChartsOption } from '../../lib/echarts';

/**
 * ClockChart.tsx — 24小时时钟图
 * 时钟盘面映射
 * 假数据与 fakeData.ts 同源，支持真实/模拟切换 via useExhibitionData
 */
export interface ClockChartDatum { label: string; value: number; }
export interface ClockChartProps {
  data?: ClockChartDatum[];
  loading?: boolean;
  error?: string | null;
  height?: number;
  onSelect?: (item: ClockChartDatum) => void;
  className?: string;
}
const FALLBACK: ClockChartDatum[] = [
  { label: "A", value: 32 },
  { label: "B", value: 28 },
  { label: "C", value: 24 },
  { label: "D", value: 18 },
  { label: "E", value: 14 },
];
function Skeleton({ height }: { height: number }) {
  return <div style={{ height }} className="animate-pulse rounded-md bg-slate-100" aria-busy="true" aria-label="加载中" />;
}
function Empty({ height }: { height: number }) {
  return <div style={{ height }} className="grid place-items-center rounded-md border border-dashed border-slate-200 bg-white text-center"><div><div className="text-2xl">📊</div><div className="mt-1 text-xs text-slate-500">暂无数据</div></div></div>;
}
function ErrorCard({ message, height }: { message: string; height: number }) {
  return <div style={{ height }} className="grid place-items-center rounded-md border border-red-200 bg-red-50 p-4 text-center"><div><div className="text-xs font-semibold text-red-600">加载失败</div><div className="mt-1 text-xs text-red-500">{message}</div></div></div>;
}
export default function ClockChart({ data, loading, error, height = 168, onSelect, className }: ClockChartProps) {
  const src = data && data.length ? data : FALLBACK;
  const option = useMemo<EChartsOption>(() => ({
    tooltip: { trigger: 'axis' },
    grid: { left: 32, right: 10, top: 10, bottom: 22 },
    xAxis: { type: 'category', data: src.map(d => d.label), axisLabel: { fontSize: 9, color: chartColors.textMuted } },
    yAxis: { type: 'value', splitLine: { lineStyle: { color: '#f1f5f9' } } },
    series: [{ type: 'bar', data: src.map(d => d.value), itemStyle: { color: chartColors.primary } }],
    animationDuration: 400,
  } as EChartsOption), [src]);
  if (loading) return <Skeleton height={height} />;
  if (error) return <ErrorCard message={error} height={height} />;
  if (!src.length) return <Empty height={height} />;
  return (
    <div className={className} role="img" aria-label="24小时时钟图" tabIndex={0} onKeyDown={(e) => { if (e.key === 'Enter' && onSelect && src[0]) onSelect(src[0]); }}>
      <EChartBox option={option} height={height} ariaLabel="24小时时钟图" onEvents={onSelect ? { click: (p: unknown) => { const d = p as { name?: string }; const it = src.find(x => x.label === d.name); if (it) onSelect(it); } } : undefined} />
    </div>
  );
}
