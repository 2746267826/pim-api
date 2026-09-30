import { useMemo } from 'react';
import EChartBox from '../charts/EChartBox';
import { buildActivityHeatmapOption, mapActivityGrid } from '../charts/pcHeatmapOptions';
import { chartColors } from '../charts/chartColors';
import type { HeatmapGridResponse } from '../../types';

interface Props {
  data: HeatmapGridResponse | undefined;
  isLoading: boolean;
  /**
   * 请求失败（如 `dimension=hour` 跨日返回 400）。必须显示后端文案，
   * 不能当作空数据渲染成一张空图（REQ-4）。
   */
  error?: unknown;
  onDateClick?: (date: string) => void;
}

function readableError(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === 'string' && error) return error;
  return '活动热力图加载失败，请稍后重试。';
}

export default function ActivityHeatmap({ data, isLoading, error, onDateClick }: Props) {
  const option = useMemo(() => buildActivityHeatmapOption(data), [data]);

  if (isLoading) {
    return <div className="rounded-2xl border border-slate-200 bg-slate-50 py-10 text-center text-sm text-slate-400">加载中...</div>;
  }

  if (error) {
    return (
      <div
        role="alert"
        className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-6 text-center text-sm text-amber-800"
      >
        <p className="font-medium">活动热力图请求未成功</p>
        <p className="mt-1 break-words text-xs text-amber-700">{readableError(error)}</p>
      </div>
    );
  }

  if (!data) {
    return <div className="rounded-2xl border border-slate-200 bg-slate-50 py-10 text-center text-sm text-slate-400">暂无活动数据</div>;
  }

  const dimension = data.dimension || 'day';
  const gridMap = mapActivityGrid(data);
  if (!gridMap) {
    return <div className="rounded-2xl border border-slate-200 bg-slate-50 py-10 text-center text-sm text-slate-400">暂无活动数据</div>;
  }

  const rowCount = gridMap.yLabels.length;
  const height =
    dimension === 'hour' ? 130 :
    dimension === 'year' ? rowCount * 18 + 30 :
    rowCount * 24 + 30;

  const handleClick = (params: unknown) => {
    if (!onDateClick) return;
    const p = params as { data?: { bucket?: { start?: string } } } | undefined;
    const bucket = p?.data?.bucket;
    if (!bucket?.start) return;
    if (dimension === 'hour') return; // hour 维度无日期语义，不触发
    onDateClick(bucket.start.slice(0, 10));
  };

  return (
    <div className="overflow-visible rounded-2xl border border-slate-200 bg-white p-4">
      {/* Header */}
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <span className="text-xs font-semibold uppercase tracking-wider text-slate-500">
            {dimension === 'hour' ? '小时' : dimension === 'month' ? '月度' : dimension === 'year' ? '年度' : '每日'}
          </span>
        </div>
        <div className="flex items-center gap-1.5 text-[10px] text-slate-400">
          <span>少</span>
          {chartColors.githubGreen.map((color, i) => (
            <div key={i} className="h-3 w-3 rounded-sm border border-white/50" style={{ backgroundColor: color }} />
          ))}
          <span>多</span>
        </div>
      </div>

      <EChartBox
        option={option}
        height={height}
        ariaLabel={`${dimension === 'hour' ? '小时' : dimension === 'month' ? '月度' : dimension === 'year' ? '年度' : '每日'}活动热力图`}
        onEvents={onDateClick ? { click: handleClick } : undefined}
      />
    </div>
  );
}
