import { useEffect, useState } from 'react';
import type { ComponentType } from 'react';
import type { MobileFrequentPlace, MobileLocationTrack } from '../../api/mobile';
import {
  formatAccuracyLabel,
  formatDistanceMeters,
  segmentKindLabel,
} from './locationFormatting';
import type { HistoricalLocationLeafletMapProps } from './HistoricalLocationLeafletMap';

export interface LocationHistoryMapProps {
  tracks: MobileLocationTrack[];
  selectedSegmentId?: string | null;
  selectedPointId?: string | null;
  repositionKey?: number;
  frequentPlaces?: MobileFrequentPlace[];
  onSelectSegment?: (segmentId: string | null) => void;
  onSelectPoint?: (pointId: string) => void;
}

function allSegments(tracks: MobileLocationTrack[]) {
  return tracks.flatMap(track => track.segments);
}

export default function LocationHistoryMap({
  tracks,
  selectedSegmentId,
  selectedPointId,
  repositionKey,
  frequentPlaces,
  onSelectSegment,
  onSelectPoint,
}: LocationHistoryMapProps) {
  const [LeafletMap, setLeafletMap] = useState<ComponentType<HistoricalLocationLeafletMapProps> | null>(null);
  const segments = allSegments(tracks);
  const selectedSegment = segments.find(segment => segment.id === selectedSegmentId) ?? segments[0];
  const totalDistance = tracks.reduce((sum, track) => sum + track.distanceMeters, 0);
  const averageAccuracy = segments.length === 0
    ? 0
    : segments.reduce((sum, segment) => sum + segment.averageAccuracyMeters, 0) / segments.length;

  useEffect(() => {
    if (typeof window === 'undefined') return undefined;

    let mounted = true;
    void import('./HistoricalLocationLeafletMap').then(module => {
      if (mounted) setLeafletMap(() => module.default);
    });

    return () => {
      mounted = false;
    };
  }, []);

  return (
    <section
      className="overflow-hidden rounded-md border border-slate-200 bg-white"
      data-track-count={tracks.length}
    >
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-100 p-4">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">轨迹地图</h2>
          <p className="mt-1 text-xs text-slate-500">轨迹线、停留点、误差和质量状态在地图上联动。</p>
        </div>
        <div className="flex flex-wrap gap-2 text-xs text-slate-600">
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5">{tracks.length} 条轨迹</span>
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5">{formatDistanceMeters(totalDistance)}</span>
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5">平均误差 {formatAccuracyLabel(averageAccuracy)}</span>
        </div>
      </div>

      <div className="h-[500px] min-h-[420px] bg-slate-100">
        {LeafletMap ? (
          <LeafletMap
            tracks={tracks}
            selectedSegmentId={selectedSegmentId}
            selectedPointId={selectedPointId}
            repositionKey={repositionKey}
            frequentPlaces={frequentPlaces}
            onSelectSegment={onSelectSegment}
            onSelectPoint={onSelectPoint}
          />
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-3 p-6 text-center text-sm text-slate-600">
            <p className="font-medium text-slate-950">轨迹地图</p>
            <p>浏览器中加载底图后显示轨迹线、停留点、误差圈和低质量提示。</p>
            {selectedSegment && (
              <p className="text-xs text-slate-500">
                当前片段：{segmentKindLabel(selectedSegment.kind)} / {formatDistanceMeters(selectedSegment.distanceMeters)} / {selectedSegment.pointCount} 点
              </p>
            )}
          </div>
        )}
      </div>
    </section>
  );
}
