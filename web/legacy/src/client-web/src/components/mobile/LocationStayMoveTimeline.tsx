import type { MobileLocationTrack } from '../../api/mobile';
import {
  formatAccuracyLabel,
  formatDistanceMeters,
  formatDurationSeconds,
  segmentKindLabel,
} from './locationFormatting';

export interface LocationStayMoveTimelineProps {
  tracks: MobileLocationTrack[];
  selectedSegmentId?: string | null;
  onSelectSegment?: (segmentId: string) => void;
}

function segmentsFromTracks(tracks: MobileLocationTrack[]) {
  return tracks
    .flatMap(track => track.segments)
    .sort((left, right) => left.startUtc.localeCompare(right.startUtc));
}

export default function LocationStayMoveTimeline({
  tracks,
  selectedSegmentId,
  onSelectSegment,
}: LocationStayMoveTimelineProps) {
  const segments = segmentsFromTracks(tracks);

  return (
    <section className="rounded-md border border-slate-200 bg-white">
      <div className="border-b border-slate-100 p-4">
        <h2 className="text-sm font-semibold text-slate-950">停留与移动时间线</h2>
        <p className="mt-1 text-xs text-slate-500">按片段展示停留、移动和缺口，避免把原始点挤进第一屏。</p>
      </div>
      <div className="space-y-2 p-4">
        {segments.length === 0 ? (
          <p className="text-sm text-slate-500">当前范围没有可展示片段。</p>
        ) : (
          segments.map(segment => {
            const selected = segment.id === selectedSegmentId;
            return (
              <button
                key={segment.id}
                type="button"
                data-segment-id={segment.id}
                onClick={() => onSelectSegment?.(segment.id)}
                className={`grid w-full grid-cols-[88px_72px_minmax(0,1fr)_auto] items-center gap-3 rounded-md border p-3 text-left text-sm transition ${
                  selected ? 'border-blue-300 bg-blue-50' : 'border-slate-100 bg-white hover:border-slate-200'
                }`}
              >
                <span className="text-xs text-slate-500">{segment.localStart.slice(11)}-{segment.localEnd.slice(11)}</span>
                <span className="font-medium text-slate-950">{segmentKindLabel(segment.kind)}</span>
                <span className="min-w-0 truncate text-slate-600">
                  {formatDistanceMeters(segment.distanceMeters)} / {formatDurationSeconds(segment.durationSeconds)} / 误差 {formatAccuracyLabel(segment.averageAccuracyMeters)}
                </span>
                <span className="text-xs text-slate-400">{segment.pointCount} 点</span>
              </button>
            );
          })
        )}
      </div>
    </section>
  );
}
