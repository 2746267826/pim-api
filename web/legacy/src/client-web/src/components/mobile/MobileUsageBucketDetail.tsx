import type { HeatmapMatrixCell } from './mobileHeatmapMatrix';
import { formatDuration } from './mobileFormatting';

export interface MobileUsageBucketDetailProps {
  cell: HeatmapMatrixCell | null;
}

function formatCellRange(cell: HeatmapMatrixCell) {
  const [, month, day] = cell.localDate.split('-').map(Number);
  const start = `${String(cell.localHour).padStart(2, '0')}:00`;
  const end = `${String((cell.localHour + 1) % 24).padStart(2, '0')}:00`;
  return `${month}月${day}日 ${start} 至 ${end}`;
}

function peakLabel(cell: HeatmapMatrixCell) {
  if (cell.foregroundSeconds >= 45 * 60) return '高峰';
  if (cell.foregroundSeconds >= 15 * 60) return '活跃';
  return '低使用';
}

export default function MobileUsageBucketDetail({ cell }: MobileUsageBucketDetailProps) {
  if (!cell) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-slate-950">选中时段</h2>
        <p className="mt-1 text-xs text-slate-500">点击热力图格子后在这里查看分类构成。</p>
        <p className="mt-6 rounded-md border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
          暂未选择时段
        </p>
      </section>
    );
  }

  const topCategory = cell.categories[0]?.lifeCategory ?? '未分类';
  const hasWarnings = cell.qualityFlags.length > 0;

  return (
    <section className="rounded-md border border-slate-200 bg-white p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-950">选中时段</h2>
          <p className="mt-1 text-xs text-slate-500">{formatCellRange(cell)}</p>
        </div>
        <span className="rounded-full bg-slate-950 px-2 py-1 text-xs font-medium text-white">
          {peakLabel(cell)}
        </span>
      </div>

      <div className="mt-5 text-3xl font-semibold tracking-normal text-slate-950">
        {formatDuration(cell.foregroundSeconds)}
      </div>

      <div className="mt-3 flex flex-wrap gap-2">
        {cell.categories.slice(0, 3).map(category => (
          <span key={category.lifeCategory} className="rounded-full bg-teal-50 px-2 py-1 text-xs font-medium text-teal-700">
            {category.lifeCategory}
          </span>
        ))}
        <span className={`rounded-full px-2 py-1 text-xs font-medium ${
          hasWarnings ? 'bg-amber-50 text-amber-700' : 'bg-slate-100 text-slate-600'
        }`}>
          {hasWarnings ? '有质量提示' : '质量正常'}
        </span>
      </div>

      <dl className="mt-5 grid grid-cols-2 gap-2 text-xs">
        <div className="rounded-md border border-slate-100 bg-slate-50 p-3">
          <dt className="text-slate-500">Top 分类</dt>
          <dd className="mt-1 truncate font-semibold text-slate-900">{topCategory}</dd>
        </div>
        <div className="rounded-md border border-slate-100 bg-slate-50 p-3">
          <dt className="text-slate-500">桶数量</dt>
          <dd className="mt-1 font-semibold text-slate-900">{cell.sourceBuckets.length}</dd>
        </div>
        <div className="rounded-md border border-slate-100 bg-slate-50 p-3">
          <dt className="text-slate-500">最长连续</dt>
          <dd className="mt-1 font-semibold text-slate-900">{formatDuration(cell.foregroundSeconds)}</dd>
        </div>
        <div className="rounded-md border border-slate-100 bg-slate-50 p-3">
          <dt className="text-slate-500">系统噪声</dt>
          <dd className="mt-1 font-semibold text-slate-900">已隐藏</dd>
        </div>
      </dl>

      <div className="mt-5 space-y-2">
        <p className="text-xs font-medium text-slate-500">分类构成</p>
        {cell.categories.map(category => {
          const width = `${Math.max(4, (category.foregroundSeconds / Math.max(1, cell.foregroundSeconds)) * 100)}%`;
          return (
            <div key={category.lifeCategory} className="grid grid-cols-[minmax(88px,0.4fr)_1fr_auto] items-center gap-2 text-xs">
              <span className="truncate text-slate-600">{category.lifeCategory}</span>
              <span className="h-2 overflow-hidden rounded bg-slate-100">
                <span className="block h-full rounded bg-teal-500" style={{ width }} />
              </span>
              <span className="tabular-nums text-slate-500">{formatDuration(category.foregroundSeconds)}</span>
            </div>
          );
        })}
      </div>

      <p className="mt-5 rounded-md bg-slate-50 px-3 py-2 text-xs leading-5 text-slate-500">
        点击热力格后，不再把全局范围缩到单格导致页面跳走；右侧展示明细，下面的图表和时间线可选择是否跟随联动。
      </p>
    </section>
  );
}
