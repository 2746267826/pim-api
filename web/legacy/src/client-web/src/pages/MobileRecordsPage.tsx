import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import {
  getMobileAnalyticsCharts,
  getMobileAnalyticsHeatmap,
  getMobileAnalyticsOverview,
  getMobileAnalyticsTimelineBlocks,
  getMobileDevices,
  getMobileLivenessOverview,
  getMobileSessionEvents,
  getMobileTimelineBlockSessions,
  MOBILE_DEFAULT_TIMEZONE,
  type MobileAnalyticsQuery,
  type MobileHeatmapBucket,
} from '../api/mobile';
import MobileAnalyticsHeader from '../components/mobile/MobileAnalyticsHeader';
import MobileAnomalyPanel from '../components/mobile/MobileAnomalyPanel';
import LabelingQueue from '../components/labeling/LabelingQueue';
import MobileChartsGrid from '../components/mobile/MobileChartsGrid';
import MobileInsightStrip from '../components/mobile/MobileInsightStrip';
import MobileTimelineBlocks from '../components/mobile/MobileTimelineBlocks';
import MobileUsageBucketDetail from '../components/mobile/MobileUsageBucketDetail';
import DeviceLivenessPanel from '../components/mobile/DeviceLivenessPanel';
import MobileUsageHeatmap, { type MobileHeatmapGranularity } from '../components/mobile/MobileUsageHeatmap';
import { buildHeatmapMatrix } from '../components/mobile/mobileHeatmapMatrix';
import MobileCategoryDonut from '../components/charts/MobileCategoryDonut';
import DayHeatmapMatrix from '../components/charts/DayHeatmapMatrix';
import { useExhibitionData } from '../components/charts/hooks/useExhibitionData';
import {
  buildMobileAnalyticsDateRange,
  toMobileAnalyticsUtcRange,
  type MobileRangeShortcut,
} from '../components/mobile/mobileFormatting';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

function errorMessage(error: unknown) {
  if (error instanceof Error && error.message) return error.message;
  return error ? '手机记录加载失败，请稍后刷新。' : null;
}

export default function MobileRecordsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const activeView = searchParams.get('view') === 'liveness' ? 'liveness' : 'usage';
  const forceRef = useRef(false);
  const refreshSeq = useRef(0);
  const defaultRange = useMemo(() => buildMobileAnalyticsDateRange('7d'), []);
  const [rangeShortcut, setRangeShortcut] = useState<MobileRangeShortcut>('7d');
  const [rangeStartDate, setRangeStartDate] = useState(defaultRange.startDate);
  const [rangeEndDate, setRangeEndDate] = useState(defaultRange.endDate);
  const [selectedDeviceId, setSelectedDeviceId] = useState('');
  const [selectedCategory, setSelectedCategory] = useState('');
  const [packageName, setPackageName] = useState('');
  const [includeSystemNoise, setIncludeSystemNoise] = useState(false);
  const [granularity, setGranularity] = useState<MobileHeatmapGranularity>('hour');
  const [selectedBucketStartUtc, setSelectedBucketStartUtc] = useState<string | null>(null);
  const [expandedBlockId, setExpandedBlockId] = useState<string | null>(null);
  const [expandedSessionId, setExpandedSessionId] = useState<string | null>(null);
  const [timelinePage, setTimelinePage] = useState(1);
  const [timelinePageSize, setTimelinePageSize] = useState(20);

  const utcRange = useMemo(
    () => toMobileAnalyticsUtcRange({ startDate: rangeStartDate, endDate: rangeEndDate }),
    [rangeStartDate, rangeEndDate],
  );

  const livenessQuery = useQuery({
    queryKey: ['mobile-liveness-overview', utcRange.rangeStartUtc, utcRange.rangeEndUtc],
    queryFn: () => getMobileLivenessOverview({
      rangeStartUtc: utcRange.rangeStartUtc,
      rangeEndUtc: utcRange.rangeEndUtc,
    }),
    enabled: activeView === 'liveness',
  });

  const analyticsQuery = useMemo<MobileAnalyticsQuery>(() => ({
    rangeStartUtc: utcRange.rangeStartUtc,
    rangeEndUtc: utcRange.rangeEndUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    deviceId: selectedDeviceId || null,
    category: selectedCategory || null,
    packageName: packageName.trim() || null,
    includeSystemNoise,
    minDurationSeconds: includeSystemNoise ? 0 : 1,
  }), [
    includeSystemNoise,
    packageName,
    selectedCategory,
    selectedDeviceId,
    utcRange.rangeEndUtc,
    utcRange.rangeStartUtc,
  ]);

  const devicesQuery = useQuery({
    queryKey: ['mobile-devices'],
    queryFn: getMobileDevices,
    staleTime: 60000,
  });

  const overviewQuery = useQuery({
    queryKey: ['mobile-analytics-overview', analyticsQuery],
    queryFn: () => getMobileAnalyticsOverview({ ...analyticsQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const heatmapQuery = useQuery({
    queryKey: ['mobile-analytics-heatmap', analyticsQuery, granularity],
    queryFn: () => getMobileAnalyticsHeatmap({ ...analyticsQuery, granularity, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const chartsQuery = useQuery({
    queryKey: ['mobile-analytics-charts', analyticsQuery],
    queryFn: () => getMobileAnalyticsCharts({ ...analyticsQuery, force: forceRef.current }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const timelineBlocksQuery = useQuery({
    queryKey: ['mobile-analytics-timeline-blocks', analyticsQuery, timelinePage, timelinePageSize],
    queryFn: () => getMobileAnalyticsTimelineBlocks({
      ...analyticsQuery,
      page: timelinePage,
      pageSize: timelinePageSize,
      force: forceRef.current,
    }),
  });

  const sessionsQuery = useQuery({
    queryKey: ['mobile-timeline-block-sessions', expandedBlockId, analyticsQuery],
    queryFn: () => getMobileTimelineBlockSessions(expandedBlockId ?? '', analyticsQuery),
    enabled: Boolean(expandedBlockId),
  });

  const eventsQuery = useQuery({
    queryKey: ['mobile-session-events', expandedSessionId],
    queryFn: () => getMobileSessionEvents(expandedSessionId ?? ''),
    enabled: Boolean(expandedSessionId),
  });

  const timelineBlocks = timelineBlocksQuery.data?.items ?? [];
  const timelinePageData = timelineBlocksQuery.data;
  const heatmapMatrix = useMemo(
    () => buildHeatmapMatrix(heatmapQuery.data ?? []),
    [heatmapQuery.data],
  );
  const selectedHeatmapCell = useMemo(
    () => heatmapMatrix.days
      .flatMap(day => day.cells)
      .find(cell => cell.bucketStartUtc === selectedBucketStartUtc)
      ?? null,
    [heatmapMatrix, selectedBucketStartUtc],
  );
  const sessionsByBlock = expandedBlockId && sessionsQuery.data
    ? { [expandedBlockId]: sessionsQuery.data }
    : {};
  const eventsBySession = expandedSessionId && eventsQuery.data
    ? { [expandedSessionId]: eventsQuery.data }
    : {};

  useEffect(() => {
    setExpandedBlockId(null);
    setExpandedSessionId(null);
  }, [analyticsQuery, timelinePage, timelinePageSize]);

  function handleShortcutChange(shortcut: Exclude<MobileRangeShortcut, 'custom'>) {
    const nextRange = buildMobileAnalyticsDateRange(shortcut);
    setRangeShortcut(shortcut);
    setRangeStartDate(nextRange.startDate);
    setRangeEndDate(nextRange.endDate);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handleCustomRangeChange(range: { startDate: string; endDate: string }) {
    setRangeShortcut('custom');
    setRangeStartDate(range.startDate);
    setRangeEndDate(range.endDate);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handleHeatmapBucketSelect(bucket: MobileHeatmapBucket) {
    setSelectedBucketStartUtc(bucket.bucketStartUtc);
    setExpandedBlockId(null);
    setExpandedSessionId(null);
  }

  function handleChartCategorySelect(category: string) {
    setSelectedCategory(category);
    setPackageName('');
    setSelectedBucketStartUtc(null);
    setExpandedBlockId(null);
    setExpandedSessionId(null);
    setTimelinePage(1);
  }

  function handleChartAppSelect(packageNameValue: string) {
    setPackageName(packageNameValue);
    setSelectedCategory('');
    setSelectedBucketStartUtc(null);
    setExpandedBlockId(null);
    setExpandedSessionId(null);
    setTimelinePage(1);
  }

  function handleDeviceChange(deviceId: string) {
    setSelectedDeviceId(deviceId);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handleHeaderCategoryChange(category: string) {
    setSelectedCategory(category);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handlePackageNameChange(value: string) {
    setPackageName(value);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handleIncludeSystemNoiseChange(value: boolean) {
    setIncludeSystemNoise(value);
    setSelectedBucketStartUtc(null);
    setTimelinePage(1);
  }

  function handleTimelinePageSizeChange(pageSize: number) {
    setTimelinePageSize(pageSize);
    setTimelinePage(1);
  }

  function refresh() {
    const seq = ++refreshSeq.current;
    forceRef.current = true;
    void Promise.all([
      devicesQuery.refetch(),
      overviewQuery.refetch(),
      heatmapQuery.refetch(),
      chartsQuery.refetch(),
      timelineBlocksQuery.refetch(),
    ]).finally(() => {
      if (refreshSeq.current === seq) {
        forceRef.current = false;
      }
    });
  }

  const loading = overviewQuery.isLoading
    || heatmapQuery.isLoading
    || chartsQuery.isLoading
    || timelineBlocksQuery.isLoading;
  const fetching = devicesQuery.isFetching
    || overviewQuery.isFetching
    || heatmapQuery.isFetching
    || chartsQuery.isFetching
    || timelineBlocksQuery.isFetching;
  const pageError = errorMessage(devicesQuery.error)
    ?? errorMessage(overviewQuery.error)
    ?? errorMessage(heatmapQuery.error)
    ?? errorMessage(chartsQuery.error)
    ?? errorMessage(timelineBlocksQuery.error);

  return (
    <div className="min-h-full bg-slate-50 pb-20 md:pb-4">
      <div className="border-b border-slate-200 bg-white px-4 sm:px-6">
        <nav className="mx-auto flex max-w-[1500px] gap-1" aria-label="手机记录子页面">
          {([
            { key: 'usage', label: '使用记录' },
            { key: 'liveness', label: '设备存活' },
          ] as const).map(view => <button
            key={view.key}
            type="button"
            aria-current={activeView === view.key ? 'page' : undefined}
            onClick={() => {
              const next = new URLSearchParams(searchParams);
              if (view.key === 'liveness') next.set('view', 'liveness');
              else next.delete('view');
              setSearchParams(next);
            }}
            className={`border-b-2 px-3 py-3 text-sm font-medium ${activeView === view.key ? 'border-blue-600 text-blue-700' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
          >{view.label}</button>)}
        </nav>
      </div>
      {activeView === 'liveness' ? <>
        <section className="border-b border-slate-200 bg-white px-4 py-4 sm:px-6">
          <div className="mx-auto flex max-w-[1500px] flex-wrap items-center justify-between gap-3">
            <div><h1 className="text-xl font-semibold text-slate-950">设备存活</h1><p className="mt-1 text-sm text-slate-500">设备心跳覆盖与静默情况</p></div>
            <div className="flex items-center gap-2" aria-label="日期快捷范围">
              {(['today', '7d', '30d'] as const).map(shortcut => <button
                key={shortcut}
                type="button"
                aria-pressed={rangeShortcut === shortcut}
                onClick={() => handleShortcutChange(shortcut)}
                className={`rounded-md border px-3 py-1.5 text-sm ${rangeShortcut === shortcut ? 'border-slate-900 bg-slate-900 text-white' : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'}`}
              >{shortcut === 'today' ? '今天' : shortcut === '7d' ? '7天' : '30天'}</button>)}
              <button type="button" onClick={() => void livenessQuery.refetch()} className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700">刷新</button>
            </div>
          </div>
        </section>
        <main className="mx-auto max-w-[1500px] space-y-4 px-4 py-4 sm:px-6">
          <DeviceLivenessPanel overview={livenessQuery.data} isLoading={livenessQuery.isLoading} error={livenessQuery.error} onRetry={() => void livenessQuery.refetch()} />
        </main>
      </> : <>
      <MobileAnalyticsHeader
        rangeShortcut={rangeShortcut}
        rangeStartDate={rangeStartDate}
        rangeEndDate={rangeEndDate}
        selectedDeviceId={selectedDeviceId}
        devices={devicesQuery.data ?? []}
        selectedCategory={selectedCategory}
        packageName={packageName}
        includeSystemNoise={includeSystemNoise}
        isFetching={fetching}
        errorMessage={pageError}
        onShortcutChange={handleShortcutChange}
        onCustomRangeChange={handleCustomRangeChange}
        onDeviceChange={handleDeviceChange}
        onCategoryChange={handleHeaderCategoryChange}
        onPackageNameChange={handlePackageNameChange}
        onIncludeSystemNoiseChange={handleIncludeSystemNoiseChange}
        onRefresh={refresh}
      />

      <main className="space-y-4 pt-4 min-h-[44px]">
        <MobileInsightStrip overview={overviewQuery.data} isLoading={loading} />
        <section className="mx-auto grid max-w-[1500px] grid-cols-1 gap-4 px-4 sm:px-6 xl:grid-cols-[minmax(0,1fr)_360px]">
          <MobileUsageHeatmap
            buckets={heatmapQuery.data ?? []}
            granularity={granularity}
            isLoading={heatmapQuery.isLoading}
            onGranularityChange={setGranularity}
            onBucketSelect={handleHeatmapBucketSelect}
          />
          <MobileUsageBucketDetail cell={selectedHeatmapCell} />
        </section>
        <section className="mx-auto max-w-[1500px] px-4 sm:px-6">
          <MobileChartsGrid
            charts={chartsQuery.data ?? []}
            isLoading={chartsQuery.isLoading}
            onCategorySelect={handleChartCategorySelect}
            onAppSelect={handleChartAppSelect}
          />
        </section>
        <section className="mx-auto max-w-[1500px] px-4 sm:px-6">
            <MobileTimelineBlocks
              blocks={timelineBlocks}
              sessionsByBlock={sessionsByBlock}
              eventsBySession={eventsBySession}
              expandedBlockId={expandedBlockId}
              expandedSessionId={expandedSessionId}
              page={timelinePageData?.page ?? timelinePage}
              pageSize={timelinePageData?.pageSize ?? timelinePageSize}
              totalCount={timelinePageData?.totalCount ?? 0}
              totalPages={timelinePageData?.totalPages ?? 0}
              isLoading={timelineBlocksQuery.isLoading}
              isLoadingSessions={sessionsQuery.isFetching}
              isLoadingEvents={eventsQuery.isFetching}
              onToggleBlock={blockId => {
                setExpandedBlockId(blockId);
                setExpandedSessionId(null);
              }}
              onToggleSession={setExpandedSessionId}
              onPageChange={setTimelinePage}
              onPageSizeChange={handleTimelinePageSizeChange}
            />
        </section>
        <div className="mx-auto grid max-w-[1500px] grid-cols-1 gap-4 px-4 sm:px-6 xl:grid-cols-2">
            <MobileAnomalyPanel
              anomalies={overviewQuery.data?.anomalies ?? []}
              suggestions={overviewQuery.data?.suggestions ?? []}
              quality={overviewQuery.data?.quality}
              isLoading={overviewQuery.isLoading}
            />
            <LabelingQueue limit={20} />
        </div>
        {/* 展览馆嵌入：日使用环形 + 24h热力（真实 via useExhibitionData） */}
        <MobileExhibitionEmbed />
      </main>
      </>}
    </div>
  );
}

function MobileExhibitionEmbed() {
  const q1 = useExhibitionData(1, { real: true });
  const q4 = useExhibitionData(4, { real: true });
  return (
    <section className="mx-auto grid max-w-[1500px] grid-cols-1 gap-4 px-4 sm:px-6 lg:grid-cols-2">
      <div className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">日使用时长 · 环形图（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">TopApp 长尾 · {q1.isReal ? '🔗真实' : '🔮模拟'} {q1.isEmpty ? '· 暂无数据' : ''}</p>
        <div className="mt-3">{q1.loading ? <div className="h-[180px] animate-pulse rounded-md bg-slate-100" /> : q1.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <MobileCategoryDonut />}</div>
      </div>
      <div className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">24小时热力 · 矩阵（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">8-12/19-23双峰 · {q4.isReal ? '🔗真实' : '🔮模拟'}</p>
        <div className="mt-3">{q4.loading ? <div className="h-[180px] animate-pulse rounded-md bg-slate-100" /> : q4.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <DayHeatmapMatrix />}</div>
      </div>
    </section>
  );
}
