import { lazy, Suspense, useEffect, useMemo, useState } from 'react'
import { Link, useLocation } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { BatteryWarning, RefreshCw, Smartphone } from 'lucide-react'
import { ApiError, apiGet } from '@/api/client'
import { hasNativeBridge, reportPageState, requestNativeState, type NativeCollectionState } from './bridge'
import { todayBusinessDay } from '@/lib/businessDay'
import { formatDuration, formatTime } from '@/lib/datetime'
/*
 * EChartsBox 懒加载：内嵌页是 WebView 启动路径，保持轻量——
 * ECharts（数百 KB）只在图表真正出现时加载，骨架占位。
 */
const EChartsBox = lazy(() => import('@/components/viz/echarts-box').then((m) => ({ default: m.EChartsBox })))
import { Button, Card, CardTitle, EmptyState, MetricCard, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

/*
 * Android 内嵌页（`/embed/android/today`、`/embed/android/tracks`）：
 * - 无外壳（不经 AppShell），仅数据模块 + 原生桥
 * - 采集状态卡：native.state.request，30 秒轮询
 * - 日期每 45 秒重键（业务日翻转）
 * - page.report 上报页面数据状态
 */

/** 内嵌页公共外壳（紧凑布局 + 桥状态） */
function EmbedShell({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="min-h-dvh bg-bg px-3 py-3">
      <div className="mb-3 flex items-center gap-2">
        <span className="size-2 rounded-full bg-primary" aria-hidden />
        <span className="text-[15px] font-semibold text-text-1">{title}</span>
        <span className="ml-auto">
          <StatusBadge tone={hasNativeBridge() ? 'ok' : 'neutral'} dot={false}>
            {hasNativeBridge() ? '原生桥已连接' : '预览模式'}
          </StatusBadge>
        </span>
      </div>
      <div className="space-y-3">{children}</div>
    </div>
  )
}

/** 采集状态卡（桥 30 秒轮询） */
function CollectionStateCard() {
  const [state, setState] = useState<NativeCollectionState | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    setLoading(true)
    setError(null)
    try {
      const res = await requestNativeState()
      setState(res)
      if (res == null && hasNativeBridge()) setError('原生未返回采集状态')
    } catch (err) {
      setError(err instanceof Error ? err.message : '获取失败')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
    const timer = setInterval(() => void refresh(), 30_000)
    return () => clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <Card className="p-3.5">
      <div className="flex items-center gap-2">
        <Smartphone className="size-4 text-text-3" aria-hidden />
        <CardTitle className="text-[13px]">采集状态</CardTitle>
        <button type="button" aria-label="刷新采集状态" onClick={() => void refresh()} className="ml-auto rounded-ctl p-1 text-text-3 outline-none hover:bg-surface">
          <RefreshCw className={cn('size-3.5', loading && 'animate-spin')} aria-hidden />
        </button>
      </div>
      {!hasNativeBridge() ? (
        <p className="mt-2 text-[12px] text-text-4">
          预览模式：壳内运行时由原生桥提供采集状态（持续采集/触发原因/下次定位）。
        </p>
      ) : error ? (
        <p className="mt-2 text-[12px] text-warn">{error}</p>
      ) : state ? (
        <div className="mt-2 space-y-1.5 text-[12px]">
          <div className="flex items-center gap-2">
            {/*
              §8.3：只有 `collectionMode === true/false` 才映射到持续采集状态；
              缺失或非布尔显示中性「状态未知」，不得显示「采集已中断」。
            */}
            <StatusBadge
              tone={state.continuousCollection === true ? 'ok' : state.continuousCollection === false ? 'warn' : 'neutral'}
              dot={false}
            >
              {state.continuousCollection === true ? '持续采集中' : state.continuousCollection === false ? '采集已中断' : '状态未知'}
            </StatusBadge>
            {state.batteryOptimized === true && (
              <span className="flex items-center gap-1 text-[11px] text-warn">
                <BatteryWarning className="size-3" aria-hidden /> 电池优化可能影响采集
              </span>
            )}
          </div>
          <div className="text-text-3">触发原因：{state.triggerReason || '—'}</div>
          <div className="text-text-3">
            下次定位：{state.nextLocationAt ? formatTime(state.nextLocationAt) : '—'}
            {typeof state.pendingUpload === 'number' && state.pendingUpload > 0 && (
              <span className="ml-2 text-warn">待上传 {state.pendingUpload}</span>
            )}
          </div>
        </div>
      ) : (
        <Skeleton className="mt-2 h-14" />
      )}
    </Card>
  )
}

/** 生成时间条（含过期徽标；日期 45 秒重键） */
function FreshnessBar({ generatedAt }: { generatedAt: string | undefined }) {
  const ageMinutes = generatedAt ? Math.round((Date.now() - new Date(generatedAt).getTime()) / 60_000) : null
  const stale = ageMinutes != null && ageMinutes > 30

  return (
    <div className={cn('flex items-center gap-2 rounded-ctl border px-3 py-1.5 text-[12px]', stale ? 'border-warn-border bg-warn-soft text-warn' : 'border-border bg-surface text-text-3')}>
      <span className="tnum">数据生成于 {generatedAt ? formatTime(generatedAt) : '—'}</span>
      {state0(ageMinutes)}
    </div>
  )
}

function state0(age: number | null) {
  if (age == null) return null
  return <span className="tnum ml-auto">{age <= 0 ? '刚刚' : `${age} 分钟前`}</span>
}

/* ── §8.4 上报口径 ───────────────────────────────────────── */

/** 「数字 0」：`undefined`/`null`/字符串都不算。 */
function isNumericZero(value: unknown): boolean {
  return typeof value === 'number' && value === 0
}

/** `generatedAt` = 成功查询里 `dataUpdatedAt` 最大值的 ISO；没有成功查询则为 null。 */
function maxDataUpdatedAt(queries: { isSuccess: boolean; dataUpdatedAt: number }[]): string | null {
  const stamps = queries.filter((query) => query.isSuccess).map((query) => query.dataUpdatedAt)
  if (stamps.length === 0) return null
  return new Date(Math.max(...stamps)).toISOString()
}

/**
 * 安全文案：只取 `ApiError.message`（没有则用固定「请求失败」），最长 200 个 Unicode 码位，
 * 去掉 `\u0000..\u001F` 与 `\u007F`；不附带响应体、URL、堆栈。
 */
function safePageErrorText(error: unknown): string {
  const raw = error instanceof ApiError && error.message ? error.message : '请求失败'
  const cleaned = Array.from(raw.replace(/[\u0000-\u001F\u007F]/g, '')).slice(0, 200).join('')
  return cleaned.length > 0 ? cleaned : '请求失败'
}

/* ── /embed/android/today ─────────────────────────────────── */

interface LocationOverviewLite {
  pointCount: number
  distanceMeters: number
  usablePointCount: number
}

interface UsageSummaryLite {
  totalForegroundSeconds: number
  appSwitchCount: number
  completeness: number
  appRanking: { displayName: string; foregroundSeconds: number; share: number }[]
}

export function AndroidTodayEmbedPage() {
  const [bizDate, setBizDate] = useState(() => todayBusinessDay())
  /* 日期每 45 秒重键（跨业务日自动刷新） */
  useEffect(() => {
    const timer = setInterval(() => setBizDate(todayBusinessDay()), 45_000)
    return () => clearInterval(timer)
  }, [])

  const location = useQuery({
    queryKey: ['embed', 'location-overview'],
    queryFn: () => apiGet<LocationOverviewLite>('/api/v1/mobile/location/analytics/overview'),
    retry: false,
  })
  const tracks = useQuery({
    queryKey: ['embed', 'tracks'],
    queryFn: () => apiGet<{ segments: { id: string; kind: string; path: { latitude: number; longitude: number }[] }[] }[]>('/api/v1/mobile/location/analytics/tracks'),
    retry: false,
  })
  const usage = useQuery({
    queryKey: ['embed', 'usage', bizDate],
    queryFn: () => apiGet<UsageSummaryLite>(`/api/v1/mobile/summary?date=${bizDate}`),
    retry: false,
  })

  /*
   * 页面数据状态上报（§8.4）。只在今日嵌入页发送，轨迹页不调用 `reportPageState`；
   * 等 location 与 usage 的首次 `isLoading` 都结束（不等 tracks）。
   * 30 秒重试由壳侧负责，这里按状态变化上报。
   */
  const pageReport = useMemo(() => {
    if (location.isLoading || usage.isLoading) return null
    const failure = location.isError ? location.error : usage.isError ? usage.error : null
    const hasServerData = failure
      ? null
      : isNumericZero(location.data?.pointCount) && isNumericZero(usage.data?.totalForegroundSeconds)
        ? false
        : true
    return {
      hasServerData,
      generatedAt: maxDataUpdatedAt([location, tracks, usage]),
      error: failure ? safePageErrorText(failure) : null,
    }
  }, [
    location.isLoading,
    location.isError,
    location.error,
    location.data,
    location.dataUpdatedAt,
    usage.isLoading,
    usage.isError,
    usage.error,
    usage.data,
    usage.dataUpdatedAt,
    tracks.isSuccess,
    tracks.dataUpdatedAt,
  ])

  useEffect(() => {
    if (pageReport) reportPageState(pageReport)
  }, [pageReport])

  const trackPoints = useMemo(() => {
    const seg = tracks.data?.[0]?.segments?.find((s) => s.kind === 'move') ?? tracks.data?.[0]?.segments?.[0]
    return seg?.path ?? []
  }, [tracks.data])

  const sparkline = useMemo(() => {
    if (trackPoints.length === 0) return null
    const lats = trackPoints.map((p) => p.latitude)
    const lngs = trackPoints.map((p) => p.longitude)
    return {
      grid: { left: 0, right: 0, top: 4, bottom: 4 },
      xAxis: { type: 'value', show: false, min: Math.min(...lngs), max: Math.max(...lngs) },
      yAxis: { type: 'value', show: false, min: Math.min(...lats), max: Math.max(...lats) },
      series: [{ type: 'line', showSymbol: false, lineStyle: { color: '#2563EB', width: 2 }, data: trackPoints.map((p) => [p.longitude, p.latitude]) }],
    }
  }, [trackPoints])

  return (
    <EmbedShell title="今日（Android 内嵌）">
      <CollectionStateCard />
      <FreshnessBar generatedAt={location.dataUpdatedAt ? new Date(location.dataUpdatedAt).toISOString() : undefined} />

      {/* 位置指标 + 轨迹缩略 */}
      <div className="grid grid-cols-3 gap-2">
        <MetricCard label="定位点" value={location.data?.pointCount ?? '—'} className="px-3 py-2" />
        <MetricCard label="可用点" value={location.data?.usablePointCount ?? '—'} className="px-3 py-2" />
        <MetricCard label="里程" value={location.data ? `${(location.data.distanceMeters / 1000).toFixed(1)}km` : '—'} className="px-3 py-2" />
      </div>

      {sparkline && (
        <Card className="p-3">
          <CardTitle className="text-[13px]">今日轨迹</CardTitle>
          <Suspense fallback={<Skeleton className="h-[110px]" />}>
            <EChartsBox option={sparkline} height={110} />
          </Suspense>
        </Card>
      )}

      {/* 使用洞察 */}
      <Card className="p-3.5">
        <CardTitle className="text-[13px]">使用洞察</CardTitle>
        {usage.isLoading ? (
          <Skeleton className="mt-2 h-16" />
        ) : usage.isError || !usage.data ? (
          <EmptyState size="sm" title="暂无使用数据" description="设备上报后这里会显示手机使用摘要。" />
        ) : (
          <>
            <div className="mt-2 grid grid-cols-3 gap-2 text-center">
              <MiniStat label="前台时长" value={formatDuration(usage.data.totalForegroundSeconds)} />
              <MiniStat label="切换/拿起" value={usage.data.appSwitchCount} />
              <MiniStat label="完整度" value={`${Math.round(usage.data.completeness * 100)}%`} />
            </div>
            {/* 应用排行（占比条） */}
            <div className="mt-3 space-y-1.5">
              {usage.data.appRanking.slice(0, 5).map((a) => (
                <div key={a.displayName} className="flex items-center gap-2 text-[12px]">
                  <span className="w-20 shrink-0 truncate text-text-2">{a.displayName}</span>
                  <div className="h-2 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-2">
                    <div className="h-full rounded-full bg-primary" style={{ width: `${Math.round(a.share * 100)}%` }} />
                  </div>
                  <span className="tnum w-14 shrink-0 text-right text-text-3">{formatDuration(a.foregroundSeconds)}</span>
                </div>
              ))}
            </div>
          </>
        )}
      </Card>

      <div className="pt-1 text-center">
        <Link to="/embed/android/tracks" className="text-[12px] text-primary outline-none hover:underline">
          查看完整轨迹 →
        </Link>
      </div>
    </EmbedShell>
  )
}

function MiniStat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-ctl bg-surface py-2">
      <div className="tnum text-[15px] font-semibold text-text-1">{value}</div>
      <div className="text-[10px] text-text-4">{label}</div>
    </div>
  )
}

/* ── /embed/android/tracks ────────────────────────────────── */

export function AndroidTracksEmbedPage() {
  const location = useLocation()
  const [bizDate] = useState(() => todayBusinessDay())

  const tracks = useQuery({
    queryKey: ['embed', 'tracks-full', bizDate],
    queryFn: () => apiGet<{ id: string; segments: { id: string; kind: string; startUtc: string; endUtc: string; distanceMeters: number; path: { latitude: number; longitude: number }[] }[] }[]>(
      '/api/v1/mobile/location/analytics/tracks',
    ),
    retry: false,
  })
  const overview = useQuery({
    queryKey: ['embed', 'loc-overview-full'],
    queryFn: () => apiGet<LocationOverviewLite & { stayCount: number; averageAccuracyMeters: number }>('/api/v1/mobile/location/analytics/overview'),
    retry: false,
  })

  const segments = tracks.data?.[0]?.segments ?? []
  const chart = useMemo(() => {
    const moveSeg = segments.find((s) => s.kind === 'move') ?? segments[0]
    if (!moveSeg?.path?.length) return null
    const pts = moveSeg.path.map((p) => [p.longitude, p.latitude])
    return {
      grid: { left: 30, right: 10, top: 10, bottom: 20 },
      xAxis: { type: 'value', scale: true, axisLabel: { fontSize: 9, color: '#94A3B8' } },
      yAxis: { type: 'value', scale: true, axisLabel: { fontSize: 9, color: '#94A3B8' } },
      series: [{ type: 'line', showSymbol: false, smooth: true, lineStyle: { color: '#2563EB', width: 2.5 }, data: pts }],
    }
  }, [segments])

  return (
    <EmbedShell title="轨迹（Android 内嵌）">
      <CollectionStateCard />
      <FreshnessBar generatedAt={tracks.dataUpdatedAt ? new Date(tracks.dataUpdatedAt).toISOString() : undefined} />

      <div className="grid grid-cols-2 gap-2">
        <MetricCard label="停留段" value={overview.data?.stayCount ?? '—'} className="px-3 py-2" />
        <MetricCard label="平均精度" value={overview.data ? `${overview.data.averageAccuracyMeters}m` : '—'} className="px-3 py-2" />
      </div>

      <Card className="p-3">
        <CardTitle className="text-[13px]">今日轨迹</CardTitle>
        {tracks.isLoading ? (
          <Skeleton className="mt-2 h-48" />
        ) : chart ? (
          <Suspense fallback={<Skeleton className="mt-2 h-48" />}>
            <EChartsBox option={chart} height={240} />
          </Suspense>
        ) : (
          <EmptyState size="sm" title="今天还没有轨迹" description="设备上报定位点后自动绘制。" />
        )}
      </Card>

      {/* 段列表（紧凑） */}
      {segments.length > 0 && (
        <Card className="p-3.5">
          <CardTitle className="text-[13px]">停留与移动段</CardTitle>
          <div className="mt-2 space-y-1">
            {segments.map((s) => (
              <div key={s.id} className="flex items-center gap-2 text-[12px]">
                <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: s.kind === 'stay' ? '#0EA5E9' : '#22C55E' }} aria-hidden />
                <span className="text-text-2">{s.kind === 'stay' ? '停留' : '移动'}</span>
                <span className="tnum ml-auto text-text-3">
                  {formatTime(s.startUtc)}
                  {s.distanceMeters > 0 && ` · ${(s.distanceMeters / 1000).toFixed(1)}km`}
                </span>
              </div>
            ))}
          </div>
        </Card>
      )}

      <div className="pt-1 text-center">
        <Link to="/embed/android/today" className="text-[12px] text-primary outline-none hover:underline">
          ← 返回今日
        </Link>
        <Button variant="ghost" size="sm" className="ml-3" onClick={() => window.location.reload()}>
          <RefreshCw className="size-3.5" aria-hidden /> 刷新
        </Button>
      </div>
      <p className="pt-1 text-center text-[10px] text-text-4">{location.pathname}</p>
    </EmbedShell>
  )
}
