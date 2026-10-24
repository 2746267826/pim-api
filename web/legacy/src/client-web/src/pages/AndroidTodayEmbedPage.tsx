import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getEmbedBridgeClient } from '../api/client';
import {
  getMobileLocationAnalyticsOverview,
  getMobileLocationAnalyticsTracks,
  getMobileAnalyticsOverview,
  getMobileSummary,
} from '../api/mobile';
import MobileMetricStrip from '../components/mobile/MobileMetricStrip';
import {
  buildMobileAnalyticsDateRange,
  toMobileAnalyticsUtcRange,
  formatShanghaiDateInput,
  formatDateTime,
} from '../components/mobile/mobileFormatting';
import LocationMetricStrip from '../components/mobile/LocationMetricStrip';
import LocationHistoryMap from '../components/mobile/LocationHistoryMap';
import MobileInsightStrip from '../components/mobile/MobileInsightStrip';
import MobileAppRanking from '../components/mobile/MobileAppRanking';
import type { NativeState } from '../embed/androidBridge';
import {
  buildPageReport,
  formatNativeBoolean,
  formatNativeField,
  staleStatusLabel,
  nativeErrorMessage,
  generatedAtEntries,
  shouldShowSummaryMetricsFallback,
  NATIVE_STATE_REFRESH_INTERVAL_MS,
} from './androidTodayEmbedState';
import type { PageReportInput } from './androidTodayEmbedState';

const DATE_REFRESH_INTERVAL_MS = 45_000;

export default function AndroidTodayEmbedPage() {
  const [dateRefreshKey, setDateRefreshKey] = useState(0);

  useEffect(() => {
    const id = setInterval(() => setDateRefreshKey(k => k + 1), DATE_REFRESH_INTERVAL_MS);
    return () => clearInterval(id);
  }, []);

  const utcRange = useMemo(() => {
    const range = buildMobileAnalyticsDateRange('today');
    return toMobileAnalyticsUtcRange(range);
  }, [dateRefreshKey]);

  const localDate = useMemo(() => formatShanghaiDateInput(), [dateRefreshKey]);

  const nativeStateQ = useQuery({
    queryKey: ['android-today-native-state'],
    queryFn: async (): Promise<NativeState> => {
      const bridge = await getEmbedBridgeClient();
      if (!bridge) throw new Error('bridge_unavailable');
      return bridge.requestNativeState();
    },
    retry: 1,
    refetchInterval: NATIVE_STATE_REFRESH_INTERVAL_MS,
    refetchIntervalInBackground: false,
  });

  const nativeState = nativeStateQ.data ?? null;
  const nativeError = nativeStateQ.isError ? nativeStateQ.error : null;

  const prevReportRef = useRef<string | null>(null);
  const [reportRetryKey, setReportRetryKey] = useState(0);
  const retryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const locationOverviewQ = useQuery({
    queryKey: ['android-today-location-overview', utcRange],
    queryFn: () => getMobileLocationAnalyticsOverview(utcRange),
    retry: 1,
  });

  const tracksQ = useQuery({
    queryKey: ['android-today-tracks', utcRange],
    queryFn: () => getMobileLocationAnalyticsTracks(utcRange),
    retry: 1,
  });

  const usageOverviewQ = useQuery({
    queryKey: ['android-today-usage-overview', utcRange],
    queryFn: () => getMobileAnalyticsOverview(utcRange),
    retry: 1,
  });

  const summaryQ = useQuery({
    queryKey: ['android-today-summary', localDate],
    queryFn: () => getMobileSummary(localDate),
    retry: 1,
  });

  const isLoading = locationOverviewQ.isLoading || tracksQ.isLoading || usageOverviewQ.isLoading || summaryQ.isLoading;

  const reportInput = useMemo<PageReportInput>(() => ({
    locationOverview: locationOverviewQ.data ?? null,
    tracks: tracksQ.data ?? null,
    usageOverview: usageOverviewQ.data ?? null,
    summary: summaryQ.data ?? null,
    locationError: locationOverviewQ.isError ? (locationOverviewQ.error as Error) : null,
    tracksError: tracksQ.isError ? (tracksQ.error as Error) : null,
    usageError: usageOverviewQ.isError ? (usageOverviewQ.error as Error) : null,
    summaryError: summaryQ.isError ? (summaryQ.error as Error) : null,
  }), [
    locationOverviewQ.data, locationOverviewQ.isError, locationOverviewQ.error,
    tracksQ.data, tracksQ.isError, tracksQ.error,
    usageOverviewQ.data, usageOverviewQ.isError, usageOverviewQ.error,
    summaryQ.data, summaryQ.isError, summaryQ.error,
  ]);

  const report = useMemo(() => buildPageReport(reportInput), [reportInput]);

  useEffect(() => {
    if (isLoading) return;

    const key = JSON.stringify(report);
    if (key === prevReportRef.current) return;
    let cancelled = false;

    (async () => {
      try {
        const bridge = await getEmbedBridgeClient();
        if (bridge) {
          await bridge.sendPageReport(report);
          if (!cancelled) {
            prevReportRef.current = key;
          }
        }
      } catch {
        if (!cancelled && !retryTimerRef.current) {
          retryTimerRef.current = setTimeout(() => {
            retryTimerRef.current = null;
            setReportRetryKey(k => k + 1);
          }, 30000);
        }
      }
    })();

    return () => {
      cancelled = true;
      if (retryTimerRef.current) {
        clearTimeout(retryTimerRef.current);
        retryTimerRef.current = null;
      }
    };
  }, [report, isLoading, reportRetryKey]);

  const isStale = usageOverviewQ.data?.isStale === true;

  const genEntries = useMemo(() => generatedAtEntries(
    locationOverviewQ.data ?? null,
    usageOverviewQ.data ?? null,
    summaryQ.data ?? null,
  ), [locationOverviewQ.data, usageOverviewQ.data, summaryQ.data]);

  const settled = !isLoading;
  const anyData = report.hasServerData;

  return (
    <div className="mx-auto max-w-[1500px] space-y-4 p-4 pb-8">
      <h1 className="text-xl font-semibold text-slate-950">今日数据</h1>

      {/* ── Native State ── */}
      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-slate-950">采集状态</h2>
        {nativeError ? (
          <p className="mt-2 text-xs text-red-600">{nativeErrorMessage(nativeError)}</p>
        ) : nativeState ? (
          <dl className="mt-2 grid grid-cols-1 gap-3 text-xs sm:grid-cols-3">
            <div>
              <dt className="text-slate-500">连续采集</dt>
              <dd className="mt-1 font-medium text-slate-900">{formatNativeBoolean(nativeState.collectionMode)}</dd>
            </div>
            <div>
              <dt className="text-slate-500">触发原因</dt>
              <dd className="mt-1 font-medium text-slate-900">{formatNativeField(nativeState.triggerReason ?? null)}</dd>
            </div>
            <div>
              <dt className="text-slate-500">下次定位</dt>
              <dd className="mt-1 font-medium text-slate-900">{formatNativeField(nativeState.nextLocationAt ?? null)}</dd>
            </div>
          </dl>
        ) : (
          <p className="mt-2 text-xs text-slate-500">正在加载采集状态...</p>
        )}
      </section>

      {/* ── GeneratedAt Information ── */}
      {genEntries.length > 0 && (
        <section className="rounded-lg border border-slate-200 bg-white p-4">
          <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
            {genEntries.map(entry => (
              <span key={entry.label}>
                {entry.label}：{formatDateTime(entry.generatedAt)}
              </span>
            ))}
            {staleStatusLabel(isStale) && (
              <span className="rounded bg-amber-50 px-1.5 py-0.5 font-medium text-amber-700">{staleStatusLabel(isStale)}</span>
            )}
          </div>
        </section>
      )}

      {/* ── Loading ── */}
      {!settled && (
        <p className="text-sm text-slate-500">正在加载今日数据...</p>
      )}

      {/* ── Errors ── */}
      {report.error && (
        <section className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700" role="alert">
          <p className="font-medium">部分数据获取失败</p>
          <ul className="mt-1 list-inside list-disc text-xs">
            {report.error.split('；').map(msg => <li key={msg}>{msg}</li>)}
          </ul>
          <button
            type="button"
            onClick={() => {
              locationOverviewQ.refetch();
              tracksQ.refetch();
              usageOverviewQ.refetch();
              summaryQ.refetch();
            }}
            className="mt-2 rounded bg-red-100 px-3 py-1 text-xs font-medium text-red-700 hover:bg-red-200"
          >
            重试
          </button>
        </section>
      )}

      {/* ── Data ── */}
      {settled && (
        <>
          {(locationOverviewQ.data?.pointCount ?? 0) > 0 && (
            <section>
              <h2 className="mb-2 text-sm font-semibold text-slate-950">位置概况</h2>
              <LocationMetricStrip overview={locationOverviewQ.data!} />
            </section>
          )}

          {tracksQ.data && tracksQ.data.length > 0 && (
            <LocationHistoryMap tracks={tracksQ.data} />
          )}

          {(usageOverviewQ.data?.totalForegroundSeconds ?? 0) > 0 && (
            <MobileInsightStrip overview={usageOverviewQ.data!} />
          )}

          {shouldShowSummaryMetricsFallback(summaryQ.data ?? null, usageOverviewQ.data ?? null) && (
            <section>
              <h2 className="mb-2 text-sm font-semibold text-slate-950">手机使用摘要</h2>
              <MobileMetricStrip
                totalForegroundSeconds={summaryQ.data!.totalForegroundSeconds}
                appSwitchCount={summaryQ.data!.appSwitchCount}
                appsUsed={summaryQ.data!.appsUsed}
                completeness={summaryQ.data!.completeness}
                qualityIssueCount={summaryQ.data!.qualityIssueCount}
                lastSyncAt={summaryQ.data!.lastSyncAt}
                fallbackForegroundSeconds={summaryQ.data!.fallbackForegroundSeconds}
              />
            </section>
          )}

          {summaryQ.data && summaryQ.data.appRanking.length > 0 && (
            <MobileAppRanking
              apps={summaryQ.data.appRanking}
              totalForegroundSeconds={summaryQ.data.totalForegroundSeconds}
            />
          )}

          {!anyData && !report.error && (
            <section className="rounded-lg border border-dashed border-slate-200 p-8 text-center text-sm text-slate-500">
              暂无今日数据。
            </section>
          )}
        </>
      )}
    </div>
  );
}
