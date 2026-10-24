import { ChevronLeft, ChevronRight, RotateCw } from 'lucide-react';
import type { MobileLocationPoint } from '../../api/mobile';
import { formatDateTime } from './mobileFormatting';
import {
  formatAccuracyLabel,
  formatCoordinate,
  locationQualityLabel,
  providerLabel,
  sourceKindLabel,
} from './locationFormatting';

export interface LocationRawPointTableProps {
  points: MobileLocationPoint[];
  selectedSegmentId: string | null;
  currentPage: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
  isFetching: boolean;
  error: string | null;
  selectedPointId?: string | null;
  onSelectPoint?: (pointId: string) => void;
  onPreviousPage: () => void;
  onNextPage: () => void;
  onRetry: () => void;
}

export default function LocationRawPointTable({
  points,
  selectedSegmentId,
  currentPage,
  hasNextPage,
  hasPreviousPage,
  isFetching,
  error,
  selectedPointId,
  onSelectPoint,
  onPreviousPage,
  onNextPage,
  onRetry,
}: LocationRawPointTableProps) {
  if (!selectedSegmentId) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
        选择片段后显示原始点。
      </section>
    );
  }

  if (isFetching && points.length === 0) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
        正在加载原始点...
      </section>
    );
  }

  if (error && points.length === 0) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-4">
        <div className="flex items-center justify-between">
          <span className="text-sm text-red-600">{error}</span>
          <button
            type="button"
            onClick={onRetry}
            className="flex items-center gap-1 rounded-md bg-red-50 px-3 py-1.5 text-sm text-red-700 hover:bg-red-100"
          >
            <RotateCw size={14} />
            重试
          </button>
        </div>
      </section>
    );
  }

  if (points.length === 0) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
        当前片段没有原始点。
      </section>
    );
  }

  return (
    <section className="rounded-md border border-slate-200 bg-white">
      <div className="flex items-start justify-between gap-3 border-b border-slate-100 p-4">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">原始点明细</h2>
          <p className="mt-1 text-xs text-slate-500">当前片段定位点</p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
            {points.length} 点
          </span>
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
            第 {currentPage} 页
          </span>
        </div>
      </div>

      {isFetching && points.length > 0 && (
        <div className="border-b border-slate-100 px-4 py-2 text-xs text-slate-500">
          正在加载更多...
        </div>
      )}

      {error && points.length > 0 && (
        <div className="flex items-center justify-between border-b border-slate-100 px-4 py-2">
          <span className="text-xs text-red-600">{error}</span>
          <button
            type="button"
            onClick={onRetry}
            className="flex items-center gap-1 text-xs text-red-700 hover:text-red-800"
          >
            <RotateCw size={12} />
            重试
          </button>
        </div>
      )}

      <div className="overflow-x-auto p-4">
        <table className="min-w-full text-left text-sm">
          <thead className="text-xs text-slate-500">
            <tr>
              <th className="pb-2 pr-3 font-medium">时间</th>
              <th className="pb-2 pr-3 font-medium">来源</th>
              <th className="pb-2 pr-3 font-medium">误差</th>
              <th className="pb-2 pr-3 font-medium">质量</th>
              <th className="pb-2 font-medium">坐标</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {points.map(point => (
              <tr
                key={point.id}
                data-point-id={point.id}
                onClick={() => onSelectPoint?.(point.id)}
                className={`cursor-pointer ${point.id === selectedPointId ? 'bg-blue-50' : 'hover:bg-slate-50'}`}
              >
                <td className="whitespace-nowrap py-2 pr-3 text-slate-700">{formatDateTime(point.recordedAtUtc)}</td>
                <td className="whitespace-nowrap py-2 pr-3 text-slate-600">
                  {providerLabel(point.provider)} / {sourceKindLabel(point.sourceKind)}
                </td>
                <td className="whitespace-nowrap py-2 pr-3 text-slate-600">{formatAccuracyLabel(point.horizontalAccuracyMeters)}</td>
                <td className="whitespace-nowrap py-2 pr-3 text-slate-600">{locationQualityLabel(point.quality)}</td>
                <td className="whitespace-nowrap py-2 font-mono text-xs text-slate-500">
                  {formatCoordinate(point.latitude, point.longitude)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex items-center justify-end gap-2 border-t border-slate-100 px-4 py-3">
        <button
          type="button"
          onClick={onPreviousPage}
          disabled={!hasPreviousPage || isFetching}
          className="flex items-center gap-1 rounded-md border border-slate-200 px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
        >
          <ChevronLeft size={14} />
          上一页
        </button>
        <span className="px-2 text-xs text-slate-500">第 {currentPage} 页</span>
        <button
          type="button"
          onClick={onNextPage}
          disabled={!hasNextPage || isFetching}
          className="flex items-center gap-1 rounded-md border border-slate-200 px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
        >
          下一页
          <ChevronRight size={14} />
        </button>
      </div>
    </section>
  );
}
