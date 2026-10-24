import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deferredIntervalMs } from '@/lib/polling'
import { mobileApi } from './api'

export const mobileKeys = {
  overview: (params: Record<string, unknown>) => ['mobile', 'overview', params] as const,
  heatmap: (params: Record<string, unknown>) => ['mobile', 'heatmap', params] as const,
  charts: (params: Record<string, unknown>) => ['mobile', 'charts', params] as const,
  blocks: (params: Record<string, unknown>) => ['mobile', 'blocks', params] as const,
  devices: (sort: string) => ['mobile', 'devices', sort] as const,
  liveness: (params: Record<string, unknown>) => ['mobile', 'liveness', params] as const,
  locationOverview: (params: Record<string, unknown>) => ['mobile', 'loc-overview', params] as const,
  tracks: (params: Record<string, unknown>) => ['mobile', 'tracks', params] as const,
  places: (params: Record<string, unknown>) => ['mobile', 'places', params] as const,
  movement: (params: Record<string, unknown>) => ['mobile', 'movement', params] as const,
}

export function useMobileOverview(params: Record<string, unknown>) {
  return useQuery({
    queryKey: mobileKeys.overview(params),
    queryFn: () => mobileApi.analyticsOverview(params),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useMobileHeatmap(params: Record<string, unknown>) {
  return useQuery({
    queryKey: mobileKeys.heatmap(params),
    queryFn: () => mobileApi.heatmap(params),
    refetchInterval: () => deferredIntervalMs(),
  })
}

/** 图表网格（复用同一查询参数组，含 force 语义） */
export function useMobileCharts(params: Record<string, unknown>) {
  return useQuery({
    queryKey: mobileKeys.charts(params),
    queryFn: () => mobileApi.charts(params),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useTimelineBlocks(params: Record<string, unknown> & { page: number; pageSize: number }) {
  return useQuery({
    queryKey: mobileKeys.blocks(params),
    queryFn: () => mobileApi.timelineBlocks(params),
    placeholderData: (prev) => prev,
  })
}

export function useMobileDevices(sort: 'data' | 'active') {
  return useQuery({
    queryKey: mobileKeys.devices(sort),
    queryFn: () => mobileApi.devicesManage(sort),
  })
}

export function useLiveness(params: Record<string, unknown>, enabled: boolean) {
  return useQuery({
    queryKey: mobileKeys.liveness(params),
    queryFn: () => mobileApi.livenessOverview(params),
    enabled,
  })
}

export function useLocationOverview(params: Record<string, unknown>, force = false) {
  return useQuery({
    queryKey: [...mobileKeys.locationOverview(params), force],
    queryFn: () => mobileApi.locationOverview({ ...params, ...(force ? { force: true } : {}) }),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useTracks(params: Record<string, unknown>, force = false) {
  return useQuery({
    queryKey: [...mobileKeys.tracks(params), force],
    queryFn: () => mobileApi.tracks({ ...params, ...(force ? { force: true } : {}) }),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useFrequentPlaces(params: Record<string, unknown>) {
  return useQuery({
    queryKey: mobileKeys.places(params),
    queryFn: () => mobileApi.frequentPlaces(params),
  })
}

export function useMovementStats(params: Record<string, unknown>) {
  return useQuery({
    queryKey: mobileKeys.movement(params),
    queryFn: () => mobileApi.movementStats(params),
  })
}

export function useDeviceActions() {
  const qc = useQueryClient()
  const invalidate = () => void qc.invalidateQueries({ queryKey: ['mobile'] })
  return {
    rename: useMutation({ mutationFn: ({ id, name }: { id: string; name: string }) => mobileApi.renameDevice(id, name), onSuccess: invalidate }),
    deletePreview: useMutation({ mutationFn: (id: string) => mobileApi.deletePreview(id) }),
    delete: useMutation({ mutationFn: (id: string) => mobileApi.deleteDevice(id), onSuccess: invalidate }),
    mergePreview: useMutation({ mutationFn: mobileApi.mergePreview }),
    merge: useMutation({ mutationFn: mobileApi.merge, onSuccess: invalidate }),
  }
}
