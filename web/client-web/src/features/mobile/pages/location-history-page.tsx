import { useEffect, useMemo, useState } from 'react'
import 'leaflet/dist/leaflet.css'
import { Crosshair, GitMerge, MapPin, Pencil, Plus, RefreshCw, RotateCcw, Trash2 } from 'lucide-react'
import { MapContainer, Polyline, TileLayer, CircleMarker, useMap } from 'react-leaflet'
import {
  useFrequentPlaces,
  useLocationOverview,
  useLocationPointActions,
  useManagedLocationPoints,
  useMovementStats,
  useTracks,
} from '../queries'
import { apiUrl } from '@/lib/apiBase'
import { ApiError } from '@/api/client'
import { formatDuration, formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import {
  Button,
  Card,
  CardTitle,
  Chip,
  ConfirmDialog,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  EmptyState,
  Input,
  MetricCard,
  PageHeader,
  Select,
  Skeleton,
} from '@/components/ui'
import type { FrequentPlace, ManagedLocationPoint } from '../types'

type RangeDays = 1 | 7 | 30
type PlaceType = 'home' | 'office' | 'other'
type ConflictRefreshOutcome = 'none' | 'refreshed' | 'needs-confirmation' | 'removed'

const RANGE_OPTS: { d: RangeDays; label: string }[] = [
  { d: 1, label: '今天' },
  { d: 7, label: '7 天' },
  { d: 30, label: '30 天' },
]

const PLACE_TYPE_OPTS: { value: PlaceType; label: string }[] = [
  { value: 'home', label: '家' },
  { value: 'office', label: '公司' },
  { value: 'other', label: '其他' },
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

function localInputValue(date: Date): string {
  const offset = date.getTimezoneOffset() * 60_000
  return new Date(date.getTime() - offset).toISOString().slice(0, 16)
}

function parseNumber(value: string): number | null {
  if (value.trim() === '') return null
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function validateCoordinates(latitude: number | null, longitude: number | null): string | null {
  if (latitude == null || longitude == null) return '请输入纬度和经度。'
  if (latitude < -90 || latitude > 90) return '纬度必须在 -90 到 90 之间。'
  if (longitude < -180 || longitude > 180) return '经度必须在 -180 到 180 之间。'
  return null
}

function errorMessage(error: unknown): string {
  if (error instanceof TypeError
    || (error instanceof Error && /Failed to fetch|NetworkError|Load failed/i.test(error.message))) {
    return '网络连接失败，请检查网络后重试。'
  }
  return error instanceof Error ? error.message : '请求失败，请检查网络后重试。'
}

function isRevisionConflict(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409 && error.code === 6204
}

function revisionConflictMessage(outcome: ConflictRefreshOutcome): string {
  if (outcome === 'needs-confirmation') {
    return '这个定位点已被其他修改更新。你的坐标修改尚未保存；请确认是否用当前输入覆盖服务端最新坐标，或载入最新坐标后重试。'
  }
  if (outcome === 'refreshed') {
    return '这个定位点已被其他修改更新。已载入服务端最新坐标，可直接重试。'
  }
  return '这个定位点已被其他修改更新，请刷新后重新选择。'
}

/** 历史位置（02 §location-history：Leaflet 轨迹 + 段选择 + 原始点管理 + 常去地点） */
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
  const activeIndex = segments.findIndex((s) => s.id === activeSegment)
  const active = activeIndex >= 0 ? segments[activeIndex] : segments[0] ?? null
  const nextSegment = activeIndex >= 0 ? segments[activeIndex + 1] ?? null : segments[1] ?? null

  const [showExcluded, setShowExcluded] = useState(false)
  const [cursorStack, setCursorStack] = useState<(string | null)[]>([null])
  const [cursorIndex, setCursorIndex] = useState(0)
  const [selectedPointIds, setSelectedPointIds] = useState<Set<string>>(new Set())

  useEffect(() => {
    setCursorStack([null])
    setCursorIndex(0)
    setSelectedPointIds(new Set())
  }, [active?.id, activeTrack?.deviceId, showExcluded])

  const pointParams = useMemo(
    () => ({
      ...params,
      deviceId: activeTrack?.deviceId,
      segmentId: showExcluded ? undefined : active?.id,
      includeExcluded: showExcluded,
      pageSize: 200,
      cursor: cursorStack[cursorIndex] ?? undefined,
    }),
    [params, activeTrack?.deviceId, active?.id, showExcluded, cursorStack, cursorIndex],
  )
  const pointsQuery = useManagedLocationPoints(pointParams, showExcluded || Boolean(activeTrack))
  const points = pointsQuery.data?.items ?? []
  const pointsById = useMemo(() => new Map(points.map((point) => [point.id, point])), [points])
  const actions = useLocationPointActions()

  const [activePoint, setActivePoint] = useState<ManagedLocationPoint | null>(null)
  const [pointLatitude, setPointLatitude] = useState('')
  const [pointLongitude, setPointLongitude] = useState('')
  const [pointCoordsDirty, setPointCoordsDirty] = useState(false)
  const [pointOverwritePending, setPointOverwritePending] = useState(false)
  const [pointExcluded, setPointExcluded] = useState(false)
  const [pointReason, setPointReason] = useState('')
  const [pointError, setPointError] = useState<string | null>(null)
  const [pointSaving, setPointSaving] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<ManagedLocationPoint | null>(null)
  const [deleteBusy, setDeleteBusy] = useState(false)

  const [batchBusy, setBatchBusy] = useState(false)
  const [mergeOpen, setMergeOpen] = useState(false)
  const [mergeKind, setMergeKind] = useState<'stay' | 'move'>('move')
  const [mergeBusy, setMergeBusy] = useState(false)
  const [mergeError, setMergeError] = useState<string | null>(null)

  const [fillOpen, setFillOpen] = useState(false)
  const [fillDevice, setFillDevice] = useState('')
  const [fillTime, setFillTime] = useState(localInputValue(new Date()))
  const [fillLatitude, setFillLatitude] = useState('')
  const [fillLongitude, setFillLongitude] = useState('')
  const [fillError, setFillError] = useState<string | null>(null)
  const [fillBusy, setFillBusy] = useState(false)

  const [placeDialog, setPlaceDialog] = useState<{
    place: FrequentPlace
    name: string
    type: PlaceType
    error: string | null
    busy: boolean
  } | null>(null)

  const o = overview.data

  async function refreshPointsAfterConflict(error: unknown): Promise<ConflictRefreshOutcome> {
    if (!isRevisionConflict(error)) return 'none'
    const refreshed = await pointsQuery.refetch()
    const items = refreshed.data?.items
    if (!items) return 'none'

    let outcome: ConflictRefreshOutcome = 'none'
    if (activePoint) {
      const latest = items.find((point) => point.id === activePoint.id)
      if (latest && latest.deletedAtUtc == null) {
        // 冲突后必须把服务端最新 revision 带回编辑态；否则下一次保存仍然携带旧 revision。
        // 排除态、更新时间与删除态也必须以服务端为准。
        // 坐标是否回填取决于用户是否改过：未改过时直接用最新坐标，避免用旧输入覆盖他处修改；
        // 已改过时保留输入，但后续保存必须先由用户明确确认覆盖。
        setActivePoint((previous) => previous && previous.id === latest.id ? latest : previous)
        setPointExcluded(latest.isExcluded)
        if (pointCoordsDirty) {
          setPointOverwritePending(true)
          outcome = 'needs-confirmation'
        } else {
          setPointLatitude(String(latest.latitude))
          setPointLongitude(String(latest.longitude))
          setPointCoordsDirty(false)
          setPointOverwritePending(false)
          outcome = 'refreshed'
        }
      } else {
        setActivePoint(null)
        setPointCoordsDirty(false)
        setPointOverwritePending(false)
        setPointError(latest
          ? '这个定位点已被永久删除，无法继续保存。'
          : '这个定位点已不存在或已不在当前筛选结果中，无法继续保存。')
        outcome = 'removed'
      }
    }

    if (deleteTarget) {
      const latest = items.find((point) => point.id === deleteTarget.id)
      setDeleteTarget((previous) => previous && latest && latest.deletedAtUtc == null
        ? {
            ...previous,
            revision: latest.revision,
            isExcluded: latest.isExcluded,
            excludedAtUtc: latest.excludedAtUtc,
            updatedAtUtc: latest.updatedAtUtc,
            deletedAtUtc: latest.deletedAtUtc,
          }
        : null)
    }
    return outcome
  }

  function openPoint(point: ManagedLocationPoint): void {
    setActivePoint(point)
    setPointLatitude(String(point.latitude))
    setPointLongitude(String(point.longitude))
    setPointCoordsDirty(false)
    setPointOverwritePending(false)
    setPointExcluded(point.isExcluded)
    setPointReason('')
    setPointError(null)
  }

  function loadLatestPointCoordinates(): void {
    if (!activePoint) return
    setPointLatitude(String(activePoint.latitude))
    setPointLongitude(String(activePoint.longitude))
    setPointCoordsDirty(false)
    setPointOverwritePending(false)
    setPointError(null)
  }

  async function savePoint(forceOverwrite = false): Promise<void> {
    if (!activePoint) return
    // 冲突后用户改过坐标时，不能直接用旧 revision 重试：必须先确认要用当前输入覆盖服务端最新坐标。
    if (pointOverwritePending && !forceOverwrite) return
    if (forceOverwrite) setPointOverwritePending(false)
    const latitude = parseNumber(pointLatitude)
    const longitude = parseNumber(pointLongitude)
    const validation = validateCoordinates(latitude, longitude)
    if (validation) {
      setPointError(validation)
      return
    }

    setPointSaving(true)
    setPointError(null)
    try {
      await actions.correct.mutateAsync({
        pointId: activePoint.id,
        body: {
          latitude: latitude!,
          longitude: longitude!,
          revision: activePoint.revision,
          excluded: pointExcluded,
          reason: pointReason.trim() || null,
        },
      })
      notifySuccess('定位点已保存', '轨迹、分段和统计已重新计算')
      setPointCoordsDirty(false)
      setPointOverwritePending(false)
      setActivePoint(null)
    } catch (error) {
      const message = errorMessage(error)
      const outcome = isRevisionConflict(error)
        ? await refreshPointsAfterConflict(error)
        : 'none'
      if (outcome !== 'removed') {
        setPointError(isRevisionConflict(error) ? revisionConflictMessage(outcome) : message)
      }
      notifyError(isRevisionConflict(error) ? '定位点已被其他地方修改' : '保存定位点失败', { description: message })
    } finally {
      setPointSaving(false)
    }
  }

  async function restorePoint(point: ManagedLocationPoint): Promise<void> {
    setPointSaving(true)
    setPointError(null)
    try {
      await actions.restore.mutateAsync({
        pointId: point.id,
        body: { revision: point.revision, reason: pointReason.trim() || null },
      })
      notifySuccess('已恢复原始坐标', '轨迹、分段和统计已重新计算')
      setActivePoint(null)
    } catch (error) {
      const message = errorMessage(error)
      const outcome = isRevisionConflict(error)
        ? await refreshPointsAfterConflict(error)
        : 'none'
      if (outcome !== 'removed') {
        setPointError(isRevisionConflict(error) ? revisionConflictMessage(outcome) : message)
      }
      notifyError('恢复原始坐标失败', { description: message })
    } finally {
      setPointSaving(false)
    }
  }

  async function deletePoint(): Promise<void> {
    if (!deleteTarget) return
    setDeleteBusy(true)
    try {
      await actions.remove.mutateAsync({ pointId: deleteTarget.id, revision: deleteTarget.revision })
      notifySuccess('定位点已永久删除', '刷新后不会再出现在历史位置中')
      setDeleteTarget(null)
      setActivePoint(null)
    } catch (error) {
      const message = errorMessage(error)
      notifyError(isRevisionConflict(error) ? '定位点已被其他地方修改' : '删除定位点失败', {
        description: isRevisionConflict(error) ? '已刷新到最新修订，请再次确认删除。' : message,
      })
      await refreshPointsAfterConflict(error)
    } finally {
      setDeleteBusy(false)
    }
  }

  async function setExcludedForSelection(excluded: boolean): Promise<void> {
    const items = [...selectedPointIds]
      .map((id) => pointsById.get(id))
      .filter((point): point is ManagedLocationPoint => point != null)
      .map((point) => ({ pointId: point.id, revision: point.revision }))
    if (items.length === 0) return

    setBatchBusy(true)
    try {
      const result = await actions.exclude.mutateAsync({ items, excluded })
      notifySuccess(excluded ? `已排除 ${result.updatedCount} 个定位点` : `已恢复 ${result.updatedCount} 个定位点`,
        '轨迹、分段和统计已重新计算')
      setSelectedPointIds(new Set())
    } catch (error) {
      notifyError(isRevisionConflict(error) ? '选中的定位点已被其他地方修改' : '批量处理失败', {
        description: errorMessage(error),
      })
      await refreshPointsAfterConflict(error)
    } finally {
      setBatchBusy(false)
    }
  }

  async function mergeNextSegment(): Promise<void> {
    if (!active || !nextSegment) return
    setMergeBusy(true)
    setMergeError(null)
    try {
      await actions.merge.mutateAsync({
        segmentIds: [active.id, nextSegment.id],
        kind: mergeKind,
        rangeStartUtc: params.rangeStartUtc!,
        rangeEndUtc: params.rangeEndUtc!,
        timezone: params.timezone,
        deviceId: activeTrack?.deviceId,
      })
      notifySuccess('相邻分段已合并', '轨迹和统计已重新计算')
      setMergeOpen(false)
    } catch (error) {
      setMergeError(errorMessage(error))
      notifyError('合并分段失败', { description: errorMessage(error) })
    } finally {
      setMergeBusy(false)
    }
  }

  function openFill(): void {
    const lastPoint = active?.path[active.path.length - 1]
    setFillDevice(activeTrack?.deviceId ?? '')
    setFillTime(localInputValue(new Date()))
    setFillLatitude(lastPoint ? String(lastPoint.latitude) : '')
    setFillLongitude(lastPoint ? String(lastPoint.longitude) : '')
    setFillError(null)
    setFillOpen(true)
  }

  async function submitFill(): Promise<void> {
    const latitude = parseNumber(fillLatitude)
    const longitude = parseNumber(fillLongitude)
    const validation = validateCoordinates(latitude, longitude)
    if (!fillDevice.trim()) {
      setFillError('请选择设备。')
      return
    }
    if (!fillTime) {
      setFillError('请选择记录时间。')
      return
    }
    if (validation) {
      setFillError(validation)
      return
    }

    setFillBusy(true)
    setFillError(null)
    try {
      await actions.fill.mutateAsync({
        deviceId: fillDevice.trim(),
        recordedAtUtc: new Date(fillTime).toISOString(),
        latitude: latitude!,
        longitude: longitude!,
        horizontalAccuracyMeters: 25,
      })
      notifySuccess('补点已保存', '轨迹、分段和统计已重新计算')
      setFillOpen(false)
    } catch (error) {
      setFillError(errorMessage(error))
      notifyError('补点失败', { description: errorMessage(error) })
    } finally {
      setFillBusy(false)
    }
  }

  function openPlace(place: FrequentPlace): void {
    setPlaceDialog({
      place,
      name: place.name ?? '',
      type: (place.type as PlaceType | null) ?? 'other',
      error: null,
      busy: false,
    })
  }

  async function savePlace(): Promise<void> {
    if (!placeDialog) return
    const name = placeDialog.name.trim()
    if (!name) {
      setPlaceDialog({ ...placeDialog, error: '请输入地点名称。' })
      return
    }

    setPlaceDialog({ ...placeDialog, busy: true, error: null })
    try {
      if (placeDialog.place.savedPlaceId && placeDialog.place.savedPlaceRevision != null) {
        await actions.updatePlace.mutateAsync({
          placeId: placeDialog.place.savedPlaceId,
          body: { name, type: placeDialog.type, revision: placeDialog.place.savedPlaceRevision },
        })
      } else {
        await actions.createPlace.mutateAsync({
          name,
          type: placeDialog.type,
          centerLatitude: placeDialog.place.centerLatitude,
          centerLongitude: placeDialog.place.centerLongitude,
          radiusMeters: Math.max(100, placeDialog.place.radiusMeters),
        })
      }
      notifySuccess('地点名称已保存')
      setPlaceDialog(null)
    } catch (error) {
      const message = errorMessage(error)
      setPlaceDialog({ ...placeDialog, busy: false, error: message })
      notifyError(isRevisionConflict(error) ? '地点已被其他地方修改' : '保存地点名称失败', {
        description: isRevisionConflict(error) ? '已刷新到最新修订，可直接重试。' : message,
      })
      if (isRevisionConflict(error)) {
        const savedPlaceId = placeDialog.place.savedPlaceId
        const refreshed = await places.refetch()
        const latest = savedPlaceId
          ? refreshed.data?.places.find((place) => place.savedPlaceId === savedPlaceId)
          : null
        if (latest?.savedPlaceRevision != null) {
          setPlaceDialog((previous) => previous && previous.place.savedPlaceId === savedPlaceId
            ? {
                ...previous,
                error: message,
                place: {
                  ...previous.place,
                  savedPlaceRevision: latest.savedPlaceRevision,
                },
              }
            : previous)
        }
      }
    }
  }

  const allPageSelected = points.length > 0 && points.every((point) => selectedPointIds.has(point.id))

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
        <Card className="overflow-hidden p-0">
          <TrackMap
            track={activeTrack ?? null}
            activeSegmentId={active?.id ?? null}
            onPickSegment={(id) => setActiveSegment(id)}
          />
        </Card>

        <Card className="flex max-h-[560px] flex-col p-4">
          <div className="flex items-center gap-2">
            <CardTitle>停留与移动段（{segments.length}）</CardTitle>
            {active && nextSegment && (
              <Button
                variant="secondary"
                size="sm"
                className="ml-auto"
                onClick={() => {
                  setMergeKind(active.kind === 'stay' ? 'stay' : 'move')
                  setMergeError(null)
                  setMergeOpen(true)
                }}
              >
                <GitMerge className="size-3.5" aria-hidden /> 合并下一段
              </Button>
            )}
          </div>
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
        <Card className="p-4">
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>原始定位点（{points.length}）</CardTitle>
            <Chip active={showExcluded} onClick={() => setShowExcluded((value) => !value)}>
              {showExcluded ? '隐藏被排除点' : '显示被排除点'}
            </Chip>
            <div className="ml-auto flex items-center gap-2 text-xs">
              <Button variant="ghost" size="sm" disabled={!activeTrack} onClick={openFill}>
                <Plus className="size-3.5" aria-hidden /> 补点
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={cursorIndex <= 0 || pointsQuery.isFetching}
                onClick={() => {
                  setSelectedPointIds(new Set())
                  setCursorIndex((value) => Math.max(0, value - 1))
                }}
              >
                上一页
              </Button>
              <span className="tnum text-text-3">第 {cursorIndex + 1} 页</span>
              <Button
                variant="secondary"
                size="sm"
                disabled={!pointsQuery.data?.hasMore || pointsQuery.isFetching}
                onClick={() => {
                  const next = pointsQuery.data?.nextCursor
                  if (!next) return
                  setSelectedPointIds(new Set())
                  setCursorStack((stack) => {
                    const updated = stack.slice(0, cursorIndex + 1)
                    updated.push(next)
                    return updated
                  })
                  setCursorIndex((value) => value + 1)
                }}
              >
                下一页
              </Button>
            </div>
          </div>

          {selectedPointIds.size > 0 && (
            <div className="mt-2 flex flex-wrap items-center gap-2 rounded-ctl border border-primary bg-primary-soft px-3 py-2 text-xs">
              <span className="tnum text-text-2">已选 {selectedPointIds.size} 个</span>
              <Button variant="secondary" size="sm" loading={batchBusy} onClick={() => void setExcludedForSelection(true)}>批量排除</Button>
              <Button variant="secondary" size="sm" loading={batchBusy} onClick={() => void setExcludedForSelection(false)}>批量恢复</Button>
              <Button variant="ghost" size="sm" onClick={() => setSelectedPointIds(new Set())}>取消选择</Button>
            </div>
          )}

          <div className="mt-2 max-h-72 overflow-y-auto">
            {pointsQuery.isLoading ? (
              <Skeleton className="h-32" />
            ) : points.length === 0 ? (
              <EmptyState size="sm" title="该段没有原始点" />
            ) : (
              <table className="w-full text-xs">
                <thead>
                  <tr className="text-left text-text-4">
                    <th className="w-8 py-1.5">
                      <input
                        type="checkbox"
                        aria-label="选择当前页全部定位点"
                        checked={allPageSelected}
                        onChange={() => {
                          setSelectedPointIds(allPageSelected
                            ? new Set()
                            : new Set(points.map((point) => point.id)))
                        }}
                        className="size-3.5 accent-primary"
                      />
                    </th>
                    <th className="py-1.5 font-medium">时间</th>
                    <th className="py-1.5 font-medium">当前坐标</th>
                    <th className="py-1.5 font-medium">精度</th>
                    <th className="py-1.5 font-medium">状态</th>
                    <th className="py-1.5 font-medium" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-divider">
                  {points.map((point) => (
                    <tr key={point.id} className={point.isExcluded ? 'opacity-60' : undefined}>
                      <td className="py-1.5">
                        <input
                          type="checkbox"
                          aria-label={`选择定位点 ${formatTime(point.recordedAtUtc)}`}
                          checked={selectedPointIds.has(point.id)}
                          onChange={() => {
                            setSelectedPointIds((previous) => {
                              const next = new Set(previous)
                              if (next.has(point.id)) next.delete(point.id)
                              else next.add(point.id)
                              return next
                            })
                          }}
                          className="size-3.5 accent-primary"
                        />
                      </td>
                      <td className="tnum py-1.5 text-text-2">{formatTime(point.recordedAtUtc)}</td>
                      <td className="tnum py-1.5 text-text-2">
                        {point.latitude.toFixed(5)}, {point.longitude.toFixed(5)}
                      </td>
                      <td className="tnum py-1.5 text-text-3">{point.horizontalAccuracyMeters.toFixed(0)} m</td>
                      <td className="py-1.5">
                        <span className={point.isExcluded ? 'text-text-4' : point.isCorrected ? 'text-primary' : 'text-text-3'}>
                          {point.isExcluded ? '已排除' : point.isCorrected ? '已修正' : point.quality === 'rejected' ? '低精度' : '计入'}
                        </span>
                      </td>
                      <td className="py-1.5 text-right">
                        <Button variant="ghost" size="sm" onClick={() => openPoint(point)}>查看</Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </Card>

        <Card className="p-4">
          <CardTitle>常去地点</CardTitle>
          <div className="mt-2 space-y-1.5">
            {(places.data?.places?.length ?? 0) === 0 && <EmptyState size="sm" title="暂无常去地点" />}
            {(places.data?.places ?? []).slice(0, 6).map((p, i) => (
              <div key={p.savedPlaceId ?? i} className="flex items-center gap-2 rounded-ctl bg-surface px-3 py-2 text-[13px]">
                {p.isHome && <Chip active className="h-5 px-1.5 text-[10px]">家</Chip>}
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium text-text-1">{p.name ?? '未命名地点'}</span>
                  <span className="mono block truncate text-[11px] text-text-3">
                    {p.centerLatitude.toFixed(4)}, {p.centerLongitude.toFixed(4)}
                  </span>
                </span>
                <span className="tnum shrink-0 text-xs text-text-3">{p.visitDayCount} 天 · {p.pointCount} 点</span>
                <Button variant="ghost" size="sm" aria-label={`${p.name ?? '未命名地点'}编辑名称`} onClick={() => openPlace(p)}>
                  <Pencil className="size-3.5" aria-hidden />
                </Button>
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

      <Dialog open={activePoint != null} onOpenChange={(open) => { if (!open && !pointSaving) setActivePoint(null) }}>
        <DialogContent className="max-w-[480px]">
          <DialogHeader
            title="定位点详情"
            description={activePoint ? `${formatTime(activePoint.recordedAtUtc)} · ${activePoint.deviceId}` : undefined}
          />
          <DialogBody className="space-y-3">
            {activePoint && (
              <>
                <div className="rounded-ctl bg-surface px-3 py-2 text-xs text-text-3">
                  <div className="flex justify-between gap-2">
                    <span>原始采集坐标</span>
                    <span className="mono text-text-2">
                      {activePoint.originalLatitude != null && activePoint.originalLongitude != null
                        ? `${activePoint.originalLatitude.toFixed(5)}, ${activePoint.originalLongitude.toFixed(5)}`
                        : `${activePoint.latitude.toFixed(5)}, ${activePoint.longitude.toFixed(5)}`}
                    </span>
                  </div>
                  <div className="mt-1 flex justify-between gap-2">
                    <span>记录事实</span>
                    <span className="mono text-text-2">精度 {activePoint.horizontalAccuracyMeters.toFixed(0)} m · 修订 {activePoint.revision}</span>
                  </div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <label className="space-y-1 text-xs text-text-3">
                    <span>纬度</span>
                    <Input
                      aria-label="纬度"
                      value={pointLatitude}
                      onChange={(event) => {
                        setPointLatitude(event.target.value)
                        setPointCoordsDirty(true)
                      }}
                      inputMode="decimal"
                    />
                  </label>
                  <label className="space-y-1 text-xs text-text-3">
                    <span>经度</span>
                    <Input
                      aria-label="经度"
                      value={pointLongitude}
                      onChange={(event) => {
                        setPointLongitude(event.target.value)
                        setPointCoordsDirty(true)
                      }}
                      inputMode="decimal"
                    />
                  </label>
                </div>
                <label className="space-y-1 text-xs text-text-3">
                  <span>修正原因（可选）</span>
                  <Input aria-label="修正原因" value={pointReason} onChange={(event) => setPointReason(event.target.value)} placeholder="例如：GPS 漂移、手动补点" />
                </label>
                <label className="flex items-center gap-2 text-[13px] text-text-2">
                  <input
                    type="checkbox"
                    checked={pointExcluded}
                    onChange={(event) => setPointExcluded(event.target.checked)}
                    className="size-4 accent-primary"
                  />
                  标记为误报，从轨迹和统计中排除
                </label>
                {activePoint.isCorrected && (
                  <Button variant="secondary" size="sm" disabled={pointSaving} onClick={() => void restorePoint(activePoint)}>
                    <RotateCcw className="size-3.5" aria-hidden /> 恢复原始坐标
                  </Button>
                )}
                {pointError && <p className="text-xs text-crit">{pointError}</p>}
              </>
            )}
          </DialogBody>
          <DialogFooter className="justify-between">
            <Button
              variant="ghost"
              className="text-crit"
              disabled={!activePoint || pointSaving}
              onClick={() => activePoint && setDeleteTarget(activePoint)}
            >
              <Trash2 className="size-3.5" aria-hidden /> 永久删除
            </Button>
            <div className="flex gap-2">
              <Button variant="secondary" disabled={pointSaving} onClick={() => setActivePoint(null)}>取消</Button>
              {pointOverwritePending ? (
                <>
                  <Button variant="secondary" disabled={pointSaving} onClick={loadLatestPointCoordinates}>
                    载入最新坐标
                  </Button>
                  <Button loading={pointSaving} onClick={() => void savePoint(true)}>确认覆盖并保存</Button>
                </>
              ) : (
                <Button loading={pointSaving} onClick={() => void savePoint()}>保存修改</Button>
              )}
            </div>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={deleteTarget != null}
        onOpenChange={(open) => { if (!open && !deleteBusy) setDeleteTarget(null) }}
        title="永久删除定位点？"
        description="删除后该点不会再出现在任何历史位置结果中，且不能恢复。"
        tone="danger"
        confirmLabel="永久删除"
        loading={deleteBusy}
        impact={deleteTarget ? {
          summary: `${formatTime(deleteTarget.recordedAtUtc)} · ${deleteTarget.latitude.toFixed(5)}, ${deleteTarget.longitude.toFixed(5)}`,
        } : undefined}
        onConfirm={() => void deletePoint()}
      />

      <Dialog open={mergeOpen} onOpenChange={(open) => { if (!open && !mergeBusy) setMergeOpen(false) }}>
        <DialogContent className="max-w-[420px]">
          <DialogHeader title="合并相邻分段" description="只允许合并共享边界点的相邻分段；保存后重新计算整段轨迹与统计。" />
          <DialogBody className="space-y-3">
            <p className="text-[13px] text-text-2">
              {active?.kind === 'stay' ? '停留' : '移动'} + {nextSegment?.kind === 'stay' ? '停留' : '移动'}
            </p>
            <label className="space-y-1 text-xs text-text-3">
              <span>合并后类型</span>
              <Select
                value={mergeKind}
                onValueChange={(value) => setMergeKind(value)}
                options={[{ value: 'move', label: '移动' }, { value: 'stay', label: '停留' }]}
                ariaLabel="合并后类型"
              />
            </label>
            {mergeError && <p className="text-xs text-crit">{mergeError}</p>}
          </DialogBody>
          <DialogFooter>
            <Button variant="secondary" disabled={mergeBusy} onClick={() => setMergeOpen(false)}>取消</Button>
            <Button loading={mergeBusy} onClick={() => void mergeNextSegment()}>合并并重算</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={fillOpen} onOpenChange={(open) => { if (!open && !fillBusy) setFillOpen(false) }}>
        <DialogContent className="max-w-[460px]">
          <DialogHeader title="补充定位点" description="用于填补定位缺失。补点会作为手动采集事实保存并重新计算轨迹。" />
          <DialogBody className="space-y-3">
            <label className="space-y-1 text-xs text-text-3">
              <span>设备</span>
              <Input aria-label="补点设备" value={fillDevice} onChange={(event) => setFillDevice(event.target.value)} />
            </label>
            <label className="space-y-1 text-xs text-text-3">
              <span>记录时间</span>
              <Input aria-label="补点时间" type="datetime-local" value={fillTime} onChange={(event) => setFillTime(event.target.value)} />
            </label>
            <div className="grid grid-cols-2 gap-3">
              <label className="space-y-1 text-xs text-text-3">
                <span>纬度</span>
                <Input aria-label="补点纬度" value={fillLatitude} onChange={(event) => setFillLatitude(event.target.value)} inputMode="decimal" />
              </label>
              <label className="space-y-1 text-xs text-text-3">
                <span>经度</span>
                <Input aria-label="补点经度" value={fillLongitude} onChange={(event) => setFillLongitude(event.target.value)} inputMode="decimal" />
              </label>
            </div>
            {fillError && <p className="text-xs text-crit">{fillError}</p>}
          </DialogBody>
          <DialogFooter>
            <Button variant="secondary" disabled={fillBusy} onClick={() => setFillOpen(false)}>取消</Button>
            <Button loading={fillBusy} onClick={() => void submitFill()}>补点并重算</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={placeDialog != null} onOpenChange={(open) => { if (!open && !placeDialog?.busy) setPlaceDialog(null) }}>
        <DialogContent className="max-w-[420px]">
          <DialogHeader
            title="地点名称"
            description={placeDialog
              ? `${placeDialog.place.centerLatitude.toFixed(4)}, ${placeDialog.place.centerLongitude.toFixed(4)} · ${placeDialog.place.visitDayCount} 天 · ${placeDialog.place.pointCount} 点`
              : undefined}
          />
          <DialogBody className="space-y-3">
            <label className="space-y-1 text-xs text-text-3">
              <span>名称</span>
              <Input
                aria-label="地点名称"
                value={placeDialog?.name ?? ''}
                onChange={(event) => placeDialog && setPlaceDialog({ ...placeDialog, name: event.target.value })}
                placeholder="例如：家、公司、健身房"
              />
            </label>
            <label className="space-y-1 text-xs text-text-3">
              <span>类型</span>
              <Select
                value={placeDialog?.type ?? 'other'}
                onValueChange={(value) => placeDialog && setPlaceDialog({ ...placeDialog, type: value })}
                options={PLACE_TYPE_OPTS}
                ariaLabel="地点类型"
              />
            </label>
            {placeDialog?.error && <p className="text-xs text-crit">{placeDialog.error}</p>}
          </DialogBody>
          <DialogFooter>
            <Button variant="secondary" disabled={placeDialog?.busy} onClick={() => setPlaceDialog(null)}>取消</Button>
            <Button loading={placeDialog?.busy} onClick={() => void savePlace()}>保存名称</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
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
  const paths = useMemo(() => {
    if (!track) return []
    return track.segments
      .filter((s) => s.path.length > 1)
      .map((s) => {
        let pts = s.path.map((p) => [p.latitude, p.longitude] as [number, number])
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
    }, [map, target])
    return null
  }

  return (
    <div className="relative h-[420px]">
      {center == null ? (
        <EmptyState className="h-full" icon={Crosshair} title="区间内没有轨迹点" description="设备上报位置后，这里会绘制轨迹折线与停留点。" />
      ) : (
        <MapContainer
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
      {activeSegmentId && (
        <div className="pointer-events-none absolute bottom-3 left-3 flex items-center gap-1 rounded-ctl bg-bg/90 px-2 py-1 text-[11px] text-text-3 shadow">
          <MapPin className="size-3" aria-hidden /> 已选分段
        </div>
      )}
    </div>
  )
}
