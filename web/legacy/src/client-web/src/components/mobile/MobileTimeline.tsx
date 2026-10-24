import type { MobileTimelineItem } from '../../api/mobile';
import { formatDuration, formatShortTime } from './mobileFormatting';

export interface MobileTimelineProps {
  items: MobileTimelineItem[];
  isLoading?: boolean;
}

function itemKindLabel(item: MobileTimelineItem) {
  return item.kind === 'fallback' ? '回退汇总' : '事件明细';
}

function sourceLabel(source: string) {
  if (source === 'events') return 'Usage Events';
  if (source === 'fallback') return 'Usage Stats';
  return source;
}

export default function MobileTimeline({ items, isLoading = false }: MobileTimelineProps) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">时间线</h2>
          <p className="mt-1 text-xs text-slate-500">按开始时间展示手机前台活动</p>
        </div>
        <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
          {items.length} 段
        </span>
      </div>

      {isLoading ? (
        <p className="mt-4 text-sm text-slate-500">正在加载时间线...</p>
      ) : items.length === 0 ? (
        <p className="mt-4 text-sm text-slate-500">暂无手机活动记录。</p>
      ) : (
        <ol className="mt-4 space-y-3">
          {items.map(item => (
            <li key={item.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium text-slate-950">{item.displayName || item.packageName}</p>
                  <p className="mt-1 text-xs text-slate-500">{item.packageName}</p>
                </div>
                <span className="rounded-full border border-blue-100 bg-blue-50 px-2 py-0.5 text-xs text-blue-700">
                  {itemKindLabel(item)}
                </span>
              </div>
              <div className="mt-3 grid grid-cols-2 gap-2 text-xs text-slate-600 sm:grid-cols-4">
                <span>{formatShortTime(item.start)} - {formatShortTime(item.end)}</span>
                <span>{formatDuration(item.durationSeconds)}</span>
                <span>{sourceLabel(item.source)}</span>
                <span>{item.kind === 'fallback' ? item.reason : `置信度 ${Math.round(item.confidence * 100)}%`}</span>
              </div>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
