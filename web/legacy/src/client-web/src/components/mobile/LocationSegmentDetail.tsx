import type { MobileLocationSegment, MobileLocationTrack } from '../../api/mobile';
import {
  formatAccuracyLabel,
  formatDistanceMeters,
  formatDurationSeconds,
  formatSpeedMetersPerSecond,
  qualityFlagLabel,
  segmentKindLabel,
} from './locationFormatting';

export interface LocationSegmentDetailProps {
  tracks: MobileLocationTrack[];
  selectedSegmentId?: string | null;
}

function allSegments(tracks: MobileLocationTrack[]) {
  return tracks.flatMap(track => track.segments);
}

function fallbackSegment(tracks: MobileLocationTrack[]) {
  return allSegments(tracks)[0] ?? null;
}

function SegmentStats({ segment }: { segment: MobileLocationSegment }) {
  return (
    <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
      <div className="rounded-md bg-slate-50 p-3">
        <dt className="text-xs text-slate-500">耗时</dt>
        <dd className="mt-1 font-semibold text-slate-950">{formatDurationSeconds(segment.durationSeconds)}</dd>
      </div>
      <div className="rounded-md bg-slate-50 p-3">
        <dt className="text-xs text-slate-500">点数</dt>
        <dd className="mt-1 font-semibold text-slate-950">{segment.pointCount}</dd>
      </div>
      <div className="rounded-md bg-slate-50 p-3">
        <dt className="text-xs text-slate-500">平均速度</dt>
        <dd className="mt-1 font-semibold text-slate-950">{formatSpeedMetersPerSecond(segment.averageSpeedMetersPerSecond)}</dd>
      </div>
      <div className="rounded-md bg-slate-50 p-3">
        <dt className="text-xs text-slate-500">平均误差</dt>
        <dd className="mt-1 font-semibold text-slate-950">{formatAccuracyLabel(segment.averageAccuracyMeters)}</dd>
      </div>
    </dl>
  );
}

export default function LocationSegmentDetail({ tracks, selectedSegmentId }: LocationSegmentDetailProps) {
  const segment = selectedSegmentId
    ? allSegments(tracks).find(item => item.id === selectedSegmentId) ?? fallbackSegment(tracks)
    : null;

  if (!segment) {
    return (
      <section className="rounded-md border border-slate-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-slate-950">选中片段</h2>
        <p className="mt-3 text-sm text-slate-500">
          {selectedSegmentId ? '当前范围没有可展示的轨迹片段。' : '在地图上点击轨迹或停留点以查看片段详情。'}
        </p>
      </section>
    );
  }

  const qualityLabels = segment.qualityFlags.length
    ? segment.qualityFlags.map(qualityFlagLabel)
    : ['质量正常'];

  return (
    <section className="rounded-md border border-slate-200 bg-white p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">选中片段</h2>
          <p className="mt-1 truncate text-xs text-slate-500">
            {segment.localStart} 至 {segment.localEnd}
          </p>
        </div>
        <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
          {segmentKindLabel(segment.kind)}
        </span>
      </div>

      <div className="mt-4">
        <p className="text-3xl font-bold tracking-normal text-slate-950">
          {formatDistanceMeters(segment.distanceMeters)}
        </p>
        <p className="mt-1 text-xs text-slate-500">估算里程</p>
      </div>

      <SegmentStats segment={segment} />

      <div className="mt-4 space-y-2">
        <div className="rounded-md border border-slate-100 bg-slate-50 p-3 text-sm">
          <p className="font-medium text-slate-950">{segmentKindLabel(segment.kind)}片段</p>
          <p className="mt-1 text-xs text-slate-500">
            设备 {segment.deviceId}，最大误差 {formatAccuracyLabel(segment.maxAccuracyMeters)}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          {qualityLabels.map(label => (
            <span key={label} className="rounded-full border border-slate-200 bg-white px-2 py-0.5 text-xs text-slate-600">
              {label}
            </span>
          ))}
        </div>
      </div>

      <p className="mt-4 rounded-md bg-blue-50 px-3 py-2 text-xs text-blue-700">
        点击地图轨迹或下方时间线会更新这里，原始点明细只显示当前片段内的点。
      </p>
    </section>
  );
}
