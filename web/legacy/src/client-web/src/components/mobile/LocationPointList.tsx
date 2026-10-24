import type { MobileLocationPoint } from '../../api/mobile';
import { formatDateTime } from './mobileFormatting';
import {
  formatAccuracyLabel,
  formatCoordinate,
  locationQualityLabel,
  providerLabel,
  sourceKindLabel,
} from './locationFormatting';

export interface LocationPointListProps {
  points: MobileLocationPoint[];
  selectedPointId?: string | null;
  onSelectPoint?: (pointId: string) => void;
}

function PointDetail({ point }: { point: MobileLocationPoint | undefined }) {
  if (!point) {
    return (
      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-slate-950">选中点详情</h2>
        <p className="mt-3 text-sm text-slate-500">请选择一个定位点查看详情。</p>
      </section>
    );
  }

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <h2 className="text-sm font-semibold text-slate-950">选中点详情</h2>
      <dl className="mt-4 grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
        <div className="min-w-0">
          <dt className="text-xs text-slate-400">记录时间</dt>
          <dd className="mt-1 truncate text-slate-700">{formatDateTime(point.recordedAtUtc)}</dd>
        </div>
        <div className="min-w-0">
          <dt className="text-xs text-slate-400">提交时间</dt>
          <dd className="mt-1 truncate text-slate-700">{formatDateTime(point.submittedAtUtc)}</dd>
        </div>
        <div>
          <dt className="text-xs text-slate-400">误差</dt>
          <dd className="mt-1 font-medium text-slate-950">{formatAccuracyLabel(point.horizontalAccuracyMeters)}</dd>
        </div>
        <div>
          <dt className="text-xs text-slate-400">提供方</dt>
          <dd className="mt-1 text-slate-700">{providerLabel(point.provider)}</dd>
        </div>
        <div>
          <dt className="text-xs text-slate-400">来源</dt>
          <dd className="mt-1 text-slate-700">{sourceKindLabel(point.sourceKind)}</dd>
        </div>
        <div>
          <dt className="text-xs text-slate-400">质量</dt>
          <dd className="mt-1 text-slate-700">{locationQualityLabel(point.quality)}</dd>
        </div>
        <div className="sm:col-span-2">
          <dt className="text-xs text-slate-400">坐标</dt>
          <dd className="mt-1 break-words font-mono text-sm text-slate-950">
            {formatCoordinate(point.latitude, point.longitude)}
          </dd>
        </div>
      </dl>
    </section>
  );
}

export default function LocationPointList({
  points,
  selectedPointId,
  onSelectPoint,
}: LocationPointListProps) {
  const selectedPoint = points.find(point => point.id === selectedPointId) ?? points[0];

  return (
    <div className="space-y-4">
      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-slate-950">定位点列表</h2>
            <p className="mt-1 text-xs text-slate-500">按记录时间倒序展示</p>
          </div>
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
            {points.length} 个点
          </span>
        </div>

        {points.length === 0 ? (
          <p className="mt-4 text-sm text-slate-500">暂无历史位置。</p>
        ) : (
          <ol className="mt-4 space-y-2">
            {points.map(point => {
              const selected = point.id === selectedPoint?.id;
              return (
                <li key={point.id}>
                  <button
                    type="button"
                    onClick={() => onSelectPoint?.(point.id)}
                    className={`w-full rounded-lg border p-3 text-left transition ${
                      selected
                        ? 'border-blue-200 bg-blue-50'
                        : 'border-slate-100 bg-slate-50 hover:border-slate-200'
                    }`}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <span className="min-w-0 truncate text-sm font-medium text-slate-950">
                        {formatDateTime(point.recordedAtUtc)}
                      </span>
                      <span className="shrink-0 text-xs text-slate-500">
                        {formatAccuracyLabel(point.horizontalAccuracyMeters)}
                      </span>
                    </div>
                    <div className="mt-2 flex flex-wrap gap-2 text-xs text-slate-500">
                      <span>{providerLabel(point.provider)}</span>
                      <span>{sourceKindLabel(point.sourceKind)}</span>
                      <span>{locationQualityLabel(point.quality)}</span>
                    </div>
                  </button>
                </li>
              );
            })}
          </ol>
        )}
      </section>

      <PointDetail point={selectedPoint} />
    </div>
  );
}
