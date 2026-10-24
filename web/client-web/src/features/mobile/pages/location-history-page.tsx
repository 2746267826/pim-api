import { useEffect, useMemo, useRef, useState } from 'react'
import 'leaflet/dist/leaflet.css'
import { Crosshair, RefreshCw } from 'lucide-react'
import { MapContainer, Polyline, TileLayer, CircleMarker, useMap } from 'react-leaflet'
import { useFrequentPlaces, useLocationOverview, useMovementStats, useTracks } from '../queries'
import { mobileApi } from '../api'
import { apiUrl } from '@/lib/apiBase'
import { formatDuration, formatTime } from '@/lib/datetime'
import { MetricCard, Button, Card, CardTitle, Chip, EmptyState, PageHeader, Skeleton } from '@/components/ui'

type RangeDays = 1 | 7 | 30

const RANGE_OPTS: { d: RangeDays; label: string }[] = [
  { d: 1, label: '今天' },
  { d: 7, label: '7 天' },
  { d: 30, label: '30 天' },
]

function rangeFor(days: RangeDays): Record<string, string> {
  const end = new Date()
  const start = new Date(end.getTime() - days * 86_400_000)
  return {
    rangeStartUtc: start.toISOString(),
    rangeEndUtc: end.toISOString(),
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  }
}

/** 历史位置（02 §location-history：Leaflet 轨迹 + 段选择 + cursor 原始点表 + 常去地点） */
export function LocationHistoryPage() {
  const [days, setDays] = useState<RangeDays>(7)
  const params = useMemo(() => rangeFor(days), [days])

  const [force, setForce] = useState(false)
  const overview = useLocationOverview(params, force)
  const tracks = useTracks(params, force)
  const places = useFrequentPlaces(params)
  const movement = useMovementStats(params)

  const trackList = tracks.data ?? []
  const [activeSegment, setActiveSegment] = useState<string | null>(null)
  const activeTrack = trackList.find((t) => t.segments.some((s) => s.id === activeSegment)) ?? trackList[0]
  const segments = activeTrack?.segments ?? []
  const active = segments.find((s) => s.id === activeSegment) ?? segments[0] ?? null

  const o = overview.data

  return (
    <div className="space-y-5">
      <PageHeader
        title="历史位置"
        subtitle="GPS 轨迹与停留分析"
        actions={
          <>
            {RANGE_OPTS.map((r) => (
              <Chip key={r.d} active={days === r.d} onClick={() => { setDays(r.d); setActiveSegment(null) }}>{r.label}</Chip>
            ))}
            <Button
              variant="secondary"
              size="sm"
              loading={force}
              onClick={() => {
                setForce(true)
                setTimeout(() => setForce(false), 1200)
              }}
              title="穿透服务端聚合缓存重新计算"
            >
              <RefreshCw className="size-3.5" aria-hidden /> 强制刷新
            </Button>
          </>
        }
      />

      {/* 指标条 */}
      {overview.isLoading ? (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-20" />)}</div>
      ) : (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <MetricCard label="可用点数" value={`${o?.usablePointCount ?? 0} / ${o?.pointCount ?? 0}`} />
          <MetricCard label="总里程" value={`${((o?.distanceMeters ?? 0) / 1000).toFixed(1)} km`} />
          <MetricCard label="停留段" value={o?.stayCount ?? 0} />
          <MetricCard label="平均精度" value={`${o?.averageAccuracyMeters ?? 0} m`} />
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_340px]">
        {/* 轨迹地图 */}
        <Card className="overflow-hidden p-0">
          <TrackMap
            track={activeTrack ?? null}
            activeSegmentId={active?.id ?? null}
            onPickSegment={(id) => setActiveSegment(id)}
          />
        </Card>

        {/* 段列表 + 时间线 */}
        <Card className="flex max-h-[560px] flex-col p-4">
          <CardTitle>停留与移动段（{segments.length}）</CardTitle>
          <div className="mt-2 min-h-0 flex-1 space-y-1.5 overflow-y-auto">
            {segments.length === 0 && <EmptyState size="sm" title="区间内没有轨迹" />}
            {segments.map((s) => (
              <button
                key={s.id}
                type="button"
                onClick={() => setActiveSegment(s.id)}
                className={`flex w-full items-center gap-2 rounded-ctl border px-2.5 py-2 text-left text-[13px] transition-colors outline-none ${
                  active?.id === s.id ? 'border-primary bg-primary-soft' : 'border-transparent hover:bg-surface'
                }`}
              >
                <span
                  className="size-2 shrink-0 rounded-full"
                  style={{ backgroundColor: s.kind === 'stay' ? '#0EA5E9' : s.kind === 'move' ? '#22C55E' : '#94A3B8' }}
                  aria-hidden
                />
                <span className="truncate font-medium text-text-1">
                  {s.kind === 'stay' ? '停留' : s.kind === 'move' ? '移动' : s.kind}
                </span>
                <span className="tnum ml-auto shrink-0 text-xs text-text-3">{formatDuration(s.durationSeconds)}</span>
                <span className="tnum shrink-0 text-[11px] text-text-4">{s.distanceMeters > 0 ? `${(s.distanceMeters / 1000).toFixed(1)}km` : ''}</span>
              </button>
            ))}
          </div>
        </Card>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {/* 原始点表（cursor 分页） */}
        {active && <RawPointsTable segmentId={active.id} days={days} />}
        {/* 常去地点 + 移动统计 */}
        <Card className="p-4">
          <CardTitle>常去地点</CardTitle>
          <div className="mt-2 space-y-1.5">
            {(places.data?.places?.length ?? 0) === 0 && <EmptyState size="sm" title="暂无常去地点" />}
            {(places.data?.places ?? []).slice(0, 6).map((p, i) => (
              <div key={i} className="flex items-center gap-2 rounded-ctl bg-surface px-3 py-2 text-[13px]">
                {p.isHome && <Chip active className="h-5 px-1.5 text-[10px]">家</Chip>}
                <span className="mono text-xs text-text-3">{p.centerLatitude.toFixed(4)}, {p.centerLongitude.toFixed(4)}</span>
                <span className="tnum ml-auto text-xs text-text-3">{p.visitDayCount} 天 · {p.pointCount} 点</span>
              </div>
            ))}
          </div>
          {movement.data && (
            <div className="mt-3 grid grid-cols-3 gap-2 border-t border-divider pt-3 text-center">
              <div>
                <div className="tnum text-sm font-semibold text-text-1">{movement.data.outingCount}</div>
                <div className="text-[10px] text-text-4">外出次数</div>
              </div>
              <div>
                <div className="tnum text-sm font-semibold text-text-1">{formatDuration(movement.data.outingSeconds)}</div>
                <div className="text-[10px] text-text-4">外出时长</div>
              </div>
              <div>
                <div className="tnum text-sm font-semibold text-text-1">
                  {movement.data.maxSpeedMetersPerSecond ? `${movement.data.maxSpeedMetersPerSecond.toFixed(1)} m/s` : '—'}
                </div>
                <div className="text-[10px] text-text-4">最高速度</div>
              </div>
            </div>
          )}
        </Card>
      </div>
    </div>
  )
}

/** Leaflet 轨迹地图（瓦片走后端代理） */
function TrackMap({
  track,
  activeSegmentId,
  onPickSegment,
}: {
  track: { segments: { id: string; kind: string; path: { latitude: number; longitude: number }[] }[] } | null
  activeSegmentId: string | null
  onPickSegment: (id: string) => void
}) {
  const ref = useRef<L.Map | null>(null)

  const paths = useMemo(() => {
    if (!track) return []
    return track.segments
      .filter((s) => s.path.length > 1)
      .map((s) => {
        let pts = s.path.map((p) => [p.latitude, p.longitude] as [number, number])
        /*
         * 抽稀：单段原始点可达数千（30 天可上万），SVG 逐点投影会让平移/缩放卡顿。
         * 等距抽稀到 ≤600 点并强制保留末点——城市级缩放（zoom 14）下视觉无差。
         */
        const MAX = 600
        if (pts.length > MAX) {
          const stride = Math.ceil(pts.length / MAX)
          const decimated: [number, number][] = []
          for (let i = 0; i < pts.length; i += stride) decimated.push(pts[i]!)
          if (decimated[decimated.length - 1] !== pts[pts.length - 1]) decimated.push(pts[pts.length - 1]!)
          pts = decimated
        }
        return { id: s.id, kind: s.kind, points: pts }
      })
  }, [track])

  const stays = useMemo(() => {
    if (!track) return []
    return track.segments.filter((s) => s.kind === 'stay' && s.path.length > 0).map((s) => s.path[0]!)
  }, [track])

  const center = useMemo<[number, number] | null>(() => {
    for (const p of paths) if (p.points[0]) return p.points[0]
    for (const s of stays) return [s.latitude, s.longitude]
    return null
  }, [paths, stays])

  function Recenter({ target }: { target: [number, number] | null }) {
    const map = useMap()
    useEffect(() => {
      if (target) map.setView(target, 14)
    }, [target?.[0], target?.[1]])
    return null
  }

  return (
    <div className="relative h-[420px]">
      {center == null ? (
        <EmptyState className="h-full" icon={Crosshair} title="区间内没有轨迹点" description="设备上报位置后，这里会绘制轨迹折线与停留点。" />
      ) : (
        <MapContainer
          ref={(m) => {
            ref.current = m
          }}
          center={center}
          zoom={14}
          className="h-full w-full"
          scrollWheelZoom
          preferCanvas
        >
          <Recenter target={center} />
          <TileLayer url={apiUrl('/api/v1/tiles/{z}/{x}/{y}.png')} attribution="&copy; OpenStreetMap (PIM proxy)" />
          {paths.map((p) => (
            <Polyline
              key={p.id}
              positions={p.points}
              pathOptions={{
                color: p.id === activeSegmentId ? '#2563EB' : p.kind === 'stay' ? '#0EA5E9' : '#22C55E',
                weight: p.id === activeSegmentId ? 5 : 3,
                opacity: 0.85,
              }}
              eventHandlers={{ click: () => onPickSegment(p.id) }}
            />
          ))}
          {stays.map((p, i) => (
            <CircleMarker
              key={i}
              center={[p.latitude, p.longitude]}
              radius={6}
              pathOptions={{ color: '#0EA5E9', fillColor: '#0EA5E9', fillOpacity: 0.9 }}
            />
          ))}
        </MapContainer>
      )}
    </div>
  )
}

/** 原始点表：全站唯一 cursor 分页（cursor 栈支持回退） */
function RawPointsTable({ segmentId, days }: { segmentId: string; days: RangeDays }) {
  const params = useMemo(() => rangeFor(days), [days])
  const pagerRef = useRef(mobileApi.rawPointsPager(segmentId, params))
  const [points, setPoints] = useState<{ id: string; recordedAtUtc: string; latitude: number; longitude: number }[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [pageIndex, setPageIndex] = useState(0)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    pagerRef.current = mobileApi.rawPointsPager(segmentId, params)
    setPageIndex(0)
  }, [segmentId, params])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    void pagerRef.current.fetch(0).then((res) => {
      if (cancelled) return
      setPoints(res.items)
      setHasMore(res.hasMore)
      setLoading(false)
    })
    return () => {
      cancelled = true
    }
  }, [segmentId, params])

  async function turn(delta: number) {
    setLoading(true)
    const res = await pagerRef.current.fetch(delta)
    setPageIndex(pagerRef.current.pageIndex)
    setPoints(res.items)
    setHasMore(res.hasMore)
    setLoading(false)
  }

  return (
    <Card className="p-4">
      <div className="flex items-center gap-2">
        <CardTitle>原始定位点（{points.length}）</CardTitle>
        <div className="ml-auto flex items-center gap-2 text-xs">
          <Button variant="secondary" size="sm" disabled={pageIndex <= 0 || loading} onClick={() => void turn(-1)}>上一页</Button>
          <span className="tnum text-text-3">第 {pageIndex + 1} 页</span>
          <Button variant="secondary" size="sm" disabled={!hasMore || loading} onClick={() => void turn(1)}>下一页</Button>
        </div>
      </div>
      <div className="mt-2 max-h-64 overflow-y-auto">
        {loading ? (
          <Skeleton className="h-32" />
        ) : points.length === 0 ? (
          <EmptyState size="sm" title="该段没有原始点" />
        ) : (
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-text-4">
                <th className="py-1.5 font-medium">时间</th>
                <th className="py-1.5 font-medium">纬度</th>
                <th className="py-1.5 font-medium">经度</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-divider">
              {points.map((p) => (
                <tr key={p.id}>
                  <td className="tnum py-1.5 text-text-2">{formatTime(p.recordedAtUtc)}</td>
                  <td className="tnum py-1.5 text-text-2">{p.latitude.toFixed(5)}</td>
                  <td className="tnum py-1.5 text-text-2">{p.longitude.toFixed(5)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </Card>
  )
}
