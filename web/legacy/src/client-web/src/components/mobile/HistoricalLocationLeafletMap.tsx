import 'leaflet/dist/leaflet.css';
import L from 'leaflet';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Circle, MapContainer, Marker, Polyline, Popup, TileLayer, useMap, useMapEvents } from 'react-leaflet';
import type { MobileFrequentPlace, MobileLocationTrack } from '../../api/mobile';
import { notifyUserInteraction } from '../../lib/autoRefresh';
import { formatDateTime } from './mobileFormatting';
import {
  formatAccuracyLabel,
  formatCoordinate,
  formatDistanceMeters,
  formatDurationSeconds,
  segmentKindLabel,
} from './locationFormatting';
import { buildMapDisplayModel } from './mobileMapModel';
import { simplifyPath } from './pathSmoothing';
import { buildFrequentPlaceCircles } from './mobileMapExtras';

export interface HistoricalLocationLeafletMapProps {
  tracks: MobileLocationTrack[];
  selectedSegmentId?: string | null;
  selectedPointId?: string | null;
  repositionKey?: number;
  frequentPlaces?: MobileFrequentPlace[];
  onSelectSegment?: (segmentId: string | null) => void;
  onSelectPoint?: (pointId: string) => void;
}

const markerIcon = L.divIcon({
  className: 'pim-location-marker',
  html: '<span></span>',
  iconSize: [18, 18],
  iconAnchor: [9, 9],
});

const selectedMarkerIcon = L.divIcon({
  className: 'pim-location-marker pim-location-marker-selected',
  html: '<span></span>',
  iconSize: [24, 24],
  iconAnchor: [12, 12],
});

/** 停留 marker 按停留时长分两档尺寸：>= 30 分钟 36px，否则 30px。 */
function stayMarkerIconFor(durationSeconds: number) {
  const size = durationSeconds >= 30 * 60 ? 36 : 30;
  return L.divIcon({
    className: 'pim-location-marker pim-location-marker-stay',
    html: '<span></span>',
    iconSize: [size, size],
    iconAnchor: [size / 2, size / 2],
  });
}

const homeLabelIcon = L.divIcon({
  className: 'pim-frequent-home-label',
  html: '<span>家</span>',
  iconSize: [30, 22],
  iconAnchor: [15, 22],
});

const jumpMarkerIcon = L.divIcon({
  className: 'pim-location-marker pim-location-marker-jump',
  html: '<span></span>',
  iconSize: [14, 14],
  iconAnchor: [7, 7],
});

function firstPosition(tracks: MobileLocationTrack[]): [number, number] {
  const point = tracks.flatMap(track => track.segments).flatMap(segment => segment.path)[0];
  return point ? [point.latitude, point.longitude] : [31.2304, 121.4737];
}

function segmentColor(kind: string, selected: boolean) {
  if (selected) return '#e11d48';
  return kind === 'move' ? '#2563eb' : '#14b8a6';
}

function MapClickHandler({ onBlankClick }: { onBlankClick: (event: L.LeafletMouseEvent) => void }) {
  useMapEvents({
    click: (event) => {
      const target = event.originalEvent.target as HTMLElement | null;
      if (target && typeof target.closest === 'function' && target.closest('.leaflet-popup-pane')) {
        return;
      }
      onBlankClick(event);
    },
  });
  return null;
}

function MapInteractionNotifier() {
  useMapEvents({
    dragstart: notifyUserInteraction,
    zoomstart: notifyUserInteraction,
  });
  return null;
}

function MapRepositioner({ tracks, repositionKey }: { tracks: MobileLocationTrack[]; repositionKey?: number }) {
  const map = useMap();
  const previousKey = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (previousKey.current === undefined) {
      previousKey.current = repositionKey;
      return;
    }
    previousKey.current = repositionKey;
    const points = tracks
      .flatMap(track => track.segments)
      .flatMap(segment => segment.path)
      .map(point => [point.latitude, point.longitude] as [number, number]);
    if (points.length === 0) return;
    const bounds = L.latLngBounds(points);
    map.fitBounds(bounds, { padding: [24, 24], maxZoom: 15 });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [repositionKey]);

  return null;
}

export default function HistoricalLocationLeafletMap({
  tracks,
  selectedSegmentId,
  selectedPointId,
  repositionKey,
  frequentPlaces,
  onSelectSegment,
  onSelectPoint,
}: HistoricalLocationLeafletMapProps) {
  const segments = useMemo(() => tracks.flatMap(track => track.segments), [tracks]);
  const model = useMemo(
    () => buildMapDisplayModel(tracks, selectedSegmentId ?? null),
    [tracks, selectedSegmentId],
  );
  const frequentCircles = useMemo(
    () => buildFrequentPlaceCircles(frequentPlaces ?? []),
    [frequentPlaces],
  );
  const simplifiedPaths = useMemo(() => {
    const bySegmentId = new Map<string, [number, number][]>();
    for (const polyline of model.movePolylines) {
      bySegmentId.set(polyline.segmentId, simplifyPath(polyline.positions, 15));
    }
    return bySegmentId;
  }, [model.movePolylines]);
  const [mapCenter] = useState(() => firstPosition(tracks));

  const stopPropagation = useCallback((event: L.LeafletMouseEvent) => {
    L.DomEvent.stopPropagation(event.originalEvent);
  }, []);

  const handleMapClick = useCallback(() => {
    onSelectSegment?.(null);
  }, [onSelectSegment]);

  return (
    <MapContainer
      center={mapCenter}
      zoom={segments.length > 0 ? 13 : 5}
      className="h-full min-h-[420px] w-full"
    >
      <MapClickHandler onBlankClick={handleMapClick} />
      <MapInteractionNotifier />
      <MapRepositioner tracks={tracks} repositionKey={repositionKey} />
      {/* 瓦片由 PIM API 服务端拉取并缓存；BASE_URL 拼接保证子路径部署时路径仍正确。 */}
      <TileLayer
        attribution="&copy; OpenStreetMap contributors"
        url={`${import.meta.env.BASE_URL}api/v1/tiles/{z}/{x}/{y}.png`}
      />
      {frequentCircles.map((circle, index) => (
        <Circle
          key={`frequent-${index}`}
          center={circle.center}
          radius={circle.radiusMeters}
          pathOptions={{ color: circle.color, fillOpacity: 0.12 }}
        >
          <Popup>
            <div>
              <strong>{circle.isHome ? '家 · 常去地点' : '常去地点'}</strong>
              <br />
              定位点数 {circle.pointCount}
              <br />
              到访天数 {circle.visitDayCount}
            </div>
          </Popup>
        </Circle>
      ))}
      {frequentCircles.filter(circle => circle.isHome).map(circle => (
        <Marker key="frequent-home-label" position={circle.center} icon={homeLabelIcon} interactive={false} />
      ))}
      {model.stayMarkers.map(marker => (
        <Circle
          key={`stay-radius-${marker.segmentId}`}
          center={marker.position}
          radius={marker.scatterRadiusMeters}
          pathOptions={{ color: '#14b8a6', weight: 1, fillOpacity: 0.08 }}
        />
      ))}
      {model.stayMarkers.map(marker => (
        <Marker
          key={`stay-${marker.segmentId}`}
          position={marker.position}
          icon={stayMarkerIconFor(marker.durationSeconds)}
          eventHandlers={{
            click: (event) => {
              stopPropagation(event);
              onSelectSegment?.(marker.segmentId);
            },
          }}
        >
          <Popup>
            <div>
              <strong>{segmentKindLabel('stay')}段</strong>
              <br />
              停留时长 {formatDurationSeconds(marker.durationSeconds)}
              <br />
              定位次数 {marker.pointCount}
              <br />
              散开半径 {formatDistanceMeters(marker.scatterRadiusMeters)}
              <br />
              最大误差 {formatAccuracyLabel(marker.maxAccuracyMeters)}
            </div>
          </Popup>
        </Marker>
      ))}
      {model.movePolylines.map(polyline => {
        const selected = polyline.segmentId === selectedSegmentId;
        return (
          <Polyline
            key={polyline.segmentId}
            positions={simplifiedPaths.get(polyline.segmentId) ?? polyline.positions}
            pathOptions={{
              color: segmentColor('move', selected),
              weight: selected ? 5 : 3,
            }}
            eventHandlers={{
              click: (event) => {
                stopPropagation(event);
                onSelectSegment?.(polyline.segmentId);
              },
            }}
          />
        );
      })}
      {model.pointMarkers.map(point => {
        const pointSelected = point.pointId === selectedPointId;
        const icon = point.isJump
          ? jumpMarkerIcon
          : pointSelected
            ? selectedMarkerIcon
            : markerIcon;
        return (
          <Marker
            key={`${point.segmentId}-${point.pointId}`}
            position={point.position}
            icon={icon}
            opacity={point.isJump ? 0.45 : pointSelected ? 1 : 0.76}
            eventHandlers={{
              click: (event) => {
                stopPropagation(event);
                onSelectSegment?.(point.segmentId);
                onSelectPoint?.(point.pointId);
              },
            }}
          >
            <Popup>
              <div>
                <strong>
                  {point.isJump ? '跳点' : `${segmentKindLabel(point.segmentKind)}片段`}
                </strong>
                <br />
                {point.recordedAtUtc ? formatDateTime(point.recordedAtUtc) : ''}
                <br />
                {formatCoordinate(point.position[0], point.position[1])}
                <br />
                误差 {formatAccuracyLabel(point.horizontalAccuracyMeters)}
                {point.isJump && (
                  <>
                    <br />
                    <span>速度超限，已从轨迹统计中剔除（原始数据保留）</span>
                  </>
                )}
              </div>
            </Popup>
          </Marker>
        );
      })}
    </MapContainer>
  );
}
