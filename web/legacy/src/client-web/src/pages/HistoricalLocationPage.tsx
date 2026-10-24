import { useMemo, useRef, useState, useCallback, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import {
  getMobileDevices,
  getMobileFrequentPlaces,
  getMobileLocationAnalyticsOverview,
  getMobileLocationAnalyticsSegmentPoints,
  getMobileLocationAnalyticsTracks,
  getMobileMovementStats,
  type MobileLocationAnalyticsParams,
} from '../api/mobile';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';
import HistoricalLocationDashboard from '../components/mobile/HistoricalLocationDashboard';
import GpsTrackMap from '../components/charts/GpsTrackMap';
import LocationBubbleChart from '../components/charts/LocationBubbleChart';
import { useExhibitionData } from '../components/charts/hooks/useExhibitionData';
import {
  buildMobileAnalyticsDateRange,
  toMobileAnalyticsUtcRange,
} from '../components/mobile/mobileFormatting';
import {
  parseTracksUrlFilters,
  serializeTracksUrlFilters,
  tracksUrlFiltersToParams,
  canAdvanceRawPointPage,
  advanceRawPointCursorStack,
} from './historicalLocationQuery';

function errorMessage(error: unknown) {
  if (error) return '历史位置数据获取失败，请重试。';
  return null;
}

export default function HistoricalLocationPage({ embedded }: { embedded?: boolean }) {
  const [searchParams, setSearchParams] = useSearchParams();

  const urlFilters = useMemo(() => parseTracksUrlFilters(searchParams), [searchParams]);

  const [rangeShortcut, setRangeShortcut] = useState(urlFilters.range);
  const [rangeStartDate, setRangeStartDate] = useState(urlFilters.startDate);
  const [rangeEndDate, setRangeEndDate] = useState(urlFilters.endDate);
  const [selectedDeviceId, setSelectedDeviceId] = useState(urlFilters.deviceId);
  const [maxAccuracyMeters, setMaxAccuracyMeters] = useState(urlFilters.maxAccuracyMeters);
  const [includeRejected, setIncludeRejected] = useState(urlFilters.includeRejected);
  const [selectedSegmentId, setSelectedSegmentId] = useState<string | null>(null);
  const [selectedPointId, setSelectedPointId] = useState<string | null>(null);
  const [selectionCleared, setSelectionCleared] = useState(false);
  const [hasUserSelectedSegment, setHasUserSelectedSegment] = useState(false);
  const [repositionKey, setRepositionKey] = useState(0);
  const forceRef = useRef(false);

  const utcRange = useMemo(
    () => toMobileAnalyticsUtcRange({ startDate: rangeStartDate, endDate: rangeEndDate }),
    [rangeStartDate, rangeEndDate],
  );

  const locationQuery: MobileLocationAnalyticsParams = useMemo(
    () => ({
      ...utcRange,
      ...tracksUrlFiltersToParams({
        range: rangeShortcut,
        startDate: rangeStartDate,
        endDate: rangeEndDate,
        deviceId: selectedDeviceId,
        maxAccuracyMeters,
        includeRejected,
      }),
    }),
    [utcRange, rangeShortcut, rangeStartDate, rangeEndDate, selectedDeviceId, maxAccuracyMeters, includeRejected],
  );

  const devicesQuery = useQuery({
    queryKey: ['mobile-devices'],
    queryFn: getMobileDevices,
    staleTime: 60000,
  });

  const overviewQuery = useQuery({
    queryKey: ['mobile-location-analytics-overview', locationQuery],
    queryFn: () => getMobileLocationAnalyticsOverview({ ...locationQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const tracksQuery = useQuery({
    queryKey: ['mobile-location-analytics-tracks', locationQuery],
    queryFn: () => getMobileLocationAnalyticsTracks({ ...locationQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const tracks = useMemo(() => tracksQuery.data ?? [], [tracksQuery.data]);
  const segments = useMemo(() => tracks.flatMap(track => track.segments), [tracks]);
  const effectiveSelectedSegmentId = useMemo(() => {
    if (selectedSegmentId && segments.some(segment => segment.id === selectedSegmentId)) {
      return selectedSegmentId;
    }
    if (hasUserSelectedSegment || selectionCleared) {
      return null;
    }
    return segments[0]?.id ?? null;
  }, [selectedSegmentId, segments, hasUserSelectedSegment, selectionCleared]);

  const cursorStack = useRef<string[]>([]);
  const [pageIndex, setPageIndex] = useState(0);
  const pageSize = 200;
  const previousEffectiveSegmentId = useRef<string | null>(null);

  useEffect(() => {
    if (previousEffectiveSegmentId.current === effectiveSelectedSegmentId) return;
    previousEffectiveSegmentId.current = effectiveSelectedSegmentId;
    cursorStack.current = [];
    setPageIndex(0);
    setSelectedPointId(null);
  }, [effectiveSelectedSegmentId]);

  const pointsQuery = useQuery({
    queryKey: ['mobile-location-analytics-segment-points', effectiveSelectedSegmentId, locationQuery, pageIndex],
    queryFn: async () => {
      const cursor = pageIndex > 0 && pageIndex - 1 < cursorStack.current.length
        ? cursorStack.current[pageIndex - 1]
        : undefined;
      const result = await getMobileLocationAnalyticsSegmentPoints(effectiveSelectedSegmentId!, {
        ...locationQuery,
        cursor: cursor || undefined,
        pageSize,
      });
      return result;
    },
    enabled: Boolean(effectiveSelectedSegmentId),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const points = useMemo(() => pointsQuery.data?.items ?? [], [pointsQuery.data]);
  const hasMore = pointsQuery.data?.hasMore ?? false;
  const currentNextCursor = pointsQuery.data?.nextCursor ?? null;

  const frequentPlacesQuery = useQuery({
    queryKey: ['mobile-frequent-places', locationQuery],
    queryFn: () => getMobileFrequentPlaces({ ...locationQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const movementStatsQuery = useQuery({
    queryKey: ['mobile-movement-stats', locationQuery],
    queryFn: () => getMobileMovementStats({ ...locationQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const frequentPlaces = useMemo(() => {
    const data = frequentPlacesQuery.data;
    if (!data) return [];
    const merged = data.home ? [data.home, ...data.places] : data.places;
    const seen = new Set<string>();
    return merged.filter(place => {
      const key = `${place.centerLatitude.toFixed(5)}|${place.centerLongitude.toFixed(5)}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }, [frequentPlacesQuery.data]);

  const effectiveSelectedPointId = selectedPointId && points.some(point => point.id === selectedPointId)
    ? selectedPointId
    : points[0]?.id ?? null;

  const rawPointsCurrentPage = pageIndex + 1;
  const rawPointsHasPreviousPage = pageIndex > 0;
  const rawPointsHasNextPage = canAdvanceRawPointPage({ hasMore, nextCursor: currentNextCursor });

  const syncUrl = useCallback((rangeValue: string, startDate: string, endDate: string, device: string, accuracy: number, rejected: boolean) => {
    const filters = {
      range: rangeValue as '7d' | '30d' | 'today' | 'custom',
      startDate,
      endDate,
      deviceId: device,
      maxAccuracyMeters: accuracy,
      includeRejected: rejected,
    };
    const next = serializeTracksUrlFilters(filters, searchParams);
    setSearchParams(next, { replace: true });
  }, [searchParams, setSearchParams]);

  function requestReposition() {
    setRepositionKey(prev => prev + 1);
  }

  const repositionedForQuery = useRef<string | null>(null);
  const locationQueryKey = useMemo(() => JSON.stringify(locationQuery), [locationQuery]);

  // 关键修复：避免依赖 tracksQuery.data 的数组引用（每次轮询即使内容不变也会产生新引用），
  // 改为依赖 tracks.length + locationQueryKey，且仅在 queryKey 变更时重定位，消除自激循环 (#310)
  const tracksLength = tracks.length;
  useEffect(() => {
    if (tracksLength === 0) return;
    if (repositionedForQuery.current === locationQueryKey) return;
    repositionedForQuery.current = locationQueryKey;
    requestReposition();
  }, [tracksLength, locationQueryKey]);

  function resetPagination() {
    cursorStack.current = [];
    setPageIndex(0);
    setSelectedPointId(null);
  }

  function applyShortcut(shortcut: string) {
    const range = buildMobileAnalyticsDateRange(shortcut as '7d' | '30d' | 'today' | 'custom');
    setRangeShortcut(range.shortcut);
    setRangeStartDate(range.startDate);
    setRangeEndDate(range.endDate);
    setSelectedSegmentId(null);
    setSelectionCleared(false);
    setHasUserSelectedSegment(false);
    resetPagination();
    syncUrl(range.shortcut, range.startDate, range.endDate, selectedDeviceId, maxAccuracyMeters, includeRejected);
  }

  function applyCustomRange(range: { startDate: string; endDate: string }) {
    setRangeShortcut('custom');
    setRangeStartDate(range.startDate);
    setRangeEndDate(range.endDate);
    setSelectedSegmentId(null);
    setSelectionCleared(false);
    setHasUserSelectedSegment(false);
    resetPagination();
    syncUrl('custom', range.startDate, range.endDate, selectedDeviceId, maxAccuracyMeters, includeRejected);
  }

  const refreshSeq = useRef(0);

  function refresh() {
    const seq = ++refreshSeq.current;
    forceRef.current = true;
    void Promise.all([
      devicesQuery.refetch(),
      overviewQuery.refetch(),
      tracksQuery.refetch(),
      pointsQuery.refetch(),
      frequentPlacesQuery.refetch(),
      movementStatsQuery.refetch(),
    ]).finally(() => {
      if (refreshSeq.current === seq) {
        forceRef.current = false;
      }
      requestReposition();
    });
  }

  function updateMaxAccuracy(value: number) {
    const next = Math.max(1, Math.round(value));
    setMaxAccuracyMeters(next);
    setSelectedSegmentId(null);
    setSelectionCleared(false);
    setHasUserSelectedSegment(false);
    resetPagination();
    syncUrl(rangeShortcut, rangeStartDate, rangeEndDate, selectedDeviceId, next, includeRejected);
  }

  function updateDevice(value: string) {
    setSelectedDeviceId(value);
    setSelectedSegmentId(null);
    setSelectionCleared(false);
    setHasUserSelectedSegment(false);
    resetPagination();
    syncUrl(rangeShortcut, rangeStartDate, rangeEndDate, value, maxAccuracyMeters, includeRejected);
  }

  function updateIncludeRejected(value: boolean) {
    setIncludeRejected(value);
    setSelectedSegmentId(null);
    setSelectionCleared(false);
    setHasUserSelectedSegment(false);
    resetPagination();
    syncUrl(rangeShortcut, rangeStartDate, rangeEndDate, selectedDeviceId, maxAccuracyMeters, value);
  }

  function handleRawPointsPreviousPage() {
    if (pageIndex <= 0) return;
    setPageIndex(prev => prev - 1);
    setSelectedPointId(null);
  }

  function handleRawPointsNextPage() {
    const advanced = advanceRawPointCursorStack({
      cursorStack: cursorStack.current,
      pageIndex,
      hasMore,
      nextCursor: currentNextCursor,
    });
    if (!advanced.didAdvance) return;
    cursorStack.current = advanced.cursorStack;
    setPageIndex(advanced.nextPageIndex);
    setSelectedPointId(null);
  }

  function handleSegmentSelect(segmentId: string | null) {
    setSelectedSegmentId(segmentId);
    setSelectionCleared(segmentId === null);
    if (segmentId !== null) {
      setHasUserSelectedSegment(true);
    }
    resetPagination();
  }

  return (
    <div className="pb-20 md:pb-4 space-y-4">
      <div className="min-h-[520px]">
        <HistoricalLocationDashboard
      rangeShortcut={rangeShortcut}
      rangeStartDate={rangeStartDate}
      rangeEndDate={rangeEndDate}
      selectedDeviceId={selectedDeviceId}
      devices={devicesQuery.data ?? []}
      maxAccuracyMeters={maxAccuracyMeters}
      includeRejected={includeRejected}
      overview={overviewQuery.data}
      tracks={tracks}
      selectedSegmentId={effectiveSelectedSegmentId}
      selectedPointId={effectiveSelectedPointId}
      repositionKey={repositionKey}
      frequentPlaces={frequentPlaces}
      movementStats={movementStatsQuery.data}
      points={points}
      isLoading={devicesQuery.isLoading || overviewQuery.isLoading || tracksQuery.isLoading}
      isFetching={devicesQuery.isFetching || overviewQuery.isFetching || tracksQuery.isFetching || pointsQuery.isFetching}
      errorMessage={
        errorMessage(devicesQuery.error)
        ?? errorMessage(overviewQuery.error)
        ?? errorMessage(tracksQuery.error)
        ?? null
      }
      rawPointsLoading={pointsQuery.isFetching}
      rawPointsError={pointsQuery.error ? '原始点加载失败' : null}
      rawPointsCurrentPage={rawPointsCurrentPage}
      rawPointsHasNextPage={rawPointsHasNextPage}
      rawPointsHasPreviousPage={rawPointsHasPreviousPage}
      onShortcutChange={applyShortcut}
      onCustomRangeChange={applyCustomRange}
      onDeviceChange={updateDevice}
      onMaxAccuracyChange={updateMaxAccuracy}
      onIncludeRejectedChange={updateIncludeRejected}
      onRefresh={refresh}
      onSelectSegment={handleSegmentSelect}
      onSelectPoint={setSelectedPointId}
      onRawPointsPreviousPage={handleRawPointsPreviousPage}
      onRawPointsNextPage={handleRawPointsNextPage}
      onRawPointsRetry={() => pointsQuery.refetch()}
      embedded={embedded}
        />
      </div>
      <LocationExhibitionEmbed />
    </div>
  );
}

function LocationExhibitionEmbed() {
  const q5 = useExhibitionData(5, { real: true });
  const q6 = useExhibitionData(6, { real: true });
  return (
    <div className="grid grid-cols-1 gap-4 px-4 lg:grid-cols-2">
      <section className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">GPS轨迹 · 散点/线图（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">家→地铁→公司3段连续 · {q5.isReal ? '🔗真实' : '🔮模拟'} {q5.isEmpty ? '· 暂无数据' : ''}</p>
        <div className="mt-3">{q5.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q5.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <GpsTrackMap data={q5.data as import('../components/charts/GpsTrackMap').GpsPoint[]} />}</div>
      </section>
      <section className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">常去地点 · 气泡图（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">家128 公司96 · {q6.isReal ? '🔗真实' : '🔮模拟'}</p>
        <div className="mt-3">{q6.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q6.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <LocationBubbleChart data={q6.data} />}</div>
      </section>
    </div>
  );
}
