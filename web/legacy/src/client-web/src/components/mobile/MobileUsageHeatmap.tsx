import { useMemo } from 'react';
import type { MobileAnalyticsGranularity, MobileHeatmapBucket } from '../../api/mobile';
import { buildHeatmapMatrix } from './mobileHeatmapMatrix';
import {
  buildUsageHeatmapOption,
  findCellByParams,
} from '../charts/mobileChartOptions';
import EChartBox from '../charts/EChartBox';

export type MobileHeatmapGranularity = Extract<MobileAnalyticsGranularity, 'hour' | '30m' | '15m'>;

export interface MobileUsageHeatmapProps {
  buckets: MobileHeatmapBucket[];
  granularity: MobileHeatmapGranularity;
  isLoading?: boolean;
  onGranularityChange: (granularity: MobileHeatmapGranularity) => void;
  onBucketSelect: (bucket: MobileHeatmapBucket) => void;
}

const granularities: Array<{ key: MobileHeatmapGranularity; label: string }> = [
  { key: 'hour', label: '小时' },
  { key: '30m', label: '30m' },
  { key: '15m', label: '15m' },
];

export default function MobileUsageHeatmap({
  buckets,
  granularity,
  isLoading = false,
  onGranularityChange,
  onBucketSelect,
}: MobileUsageHeatmapProps) {
  const matrix = useMemo(() => buildHeatmapMatrix(buckets), [buckets]);
  const option = useMemo(() => buildUsageHeatmapOption(matrix), [matrix]);

  return (
    <section className="rounded-md border border-slate-200 bg-white p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-950">使用热力图</h2>
          <p className="mt-1 text-xs text-slate-500">
            左侧是日期，顶部是小时。格子不再重复写小时，点击后在右侧查看分类和 App 明细。
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 text-xs text-slate-500">
            <span>少</span>
            <span className="h-2 w-20 rounded-full bg-gradient-to-r from-slate-100 via-teal-100 to-teal-600" />
            <span>多</span>
          </div>
          <div className="flex rounded-md border border-slate-200 bg-slate-50 p-1">
            {granularities.map(item => (
              <button
                key={item.key}
                type="button"
                onClick={() => onGranularityChange(item.key)}
                className={`h-8 rounded px-3 text-xs font-medium ${
                  granularity === item.key ? 'bg-slate-950 text-white' : 'text-slate-600 hover:bg-white'
                }`}
              >
                {item.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="mt-4">
        <EChartBox
          option={option}
          height={matrix.days.length * 34 + 60}
          ariaLabel="手机使用热力图"
          onEvents={{
            click: params => {
              const cell = findCellByParams(matrix, params);
              const bucket = cell?.sourceBuckets[0];
              if (bucket) onBucketSelect(bucket);
            },
          }}
        />
      </div>

      {isLoading && <p className="mt-3 text-xs text-slate-500">正在加载热力图</p>}
      {!isLoading && buckets.length === 0 && <p className="mt-3 text-xs text-slate-500">所选范围暂无热力图数据</p>}
    </section>
  );
}
