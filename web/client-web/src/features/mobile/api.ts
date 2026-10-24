import { apiDelete, apiGet, apiPost } from '@/api/client'
import type { components } from '@/api/generated'
import type {
  DeviceDeletePreview,
  DeviceMergePreview,
  FrequentPlace,
  LivenessOverview,
  LocationOverview,
  LocationTrack,
  MobileAnalyticsOverview,
  MobileChartDto as MobileAnalyticsChartDto,
  MobileHeatmapBucket,
  MobileTimelineBlock,
  MovementStats,
  SegmentPointsPage,
} from './types'

/** 位置分析公共查询参数组（rangeStartUtc/rangeEndUtc/timezone/deviceId/maxAccuracy/includeRejected + force） */
function locQ(params: Record<string, unknown>): string {
  const q = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') q.set(k, String(v))
  }
  return q.toString()
}

export const mobileApi = {
  analyticsOverview: (params: Record<string, unknown>) =>
    apiGet<MobileAnalyticsOverview>(`/api/v1/mobile/analytics/overview?${locQ(params)}`),
  heatmap: (params: Record<string, unknown>) =>
    apiGet<MobileHeatmapBucket[]>(`/api/v1/mobile/analytics/heatmap?${locQ(params)}`),
  charts: (params: Record<string, unknown>) =>
    apiGet<MobileAnalyticsChartDto[]>(`/api/v1/mobile/analytics/charts?${locQ(params)}`),
  timelineBlocks: (params: Record<string, unknown> & { page?: number; pageSize?: number }) =>
    apiGet<{ items: MobileTimelineBlock[]; hasMore: boolean; totalCount: number; nextCursor: string | null }>(
      `/api/v1/mobile/analytics/timeline-blocks?${locQ(params)}`,
    ),
  /** 会话原始事件明细（规格 mobile.md:915，会话下钻第三级） */
  sessionEvents: (sessionId: string) =>
    apiGet<{ id: string; sessionId: string; deviceId: string; packageName: string; eventType: string; eventTimeUtc: string; className: string | null; rawJson: string }[]>(
      `/api/v1/mobile/analytics/sessions/${encodeURIComponent(sessionId)}/events`,
    ),
  /** 逐设备存活取证事件（规格 mobile.md:1004，存活面板用） */
  livenessEvents: (deviceId: string, params: { rangeStartUtc?: string; rangeEndUtc?: string; page?: number; pageSize?: number }) =>
    apiGet<{
      items: { id: string; eventType: string; eventTypeLabel: string; occurredAtUtc: string; reasonLabel: string | null; payloadJson: string }[]
      page: number
      pageSize: number
      totalCount: number
      totalPages: number
    }>(`/api/v1/mobile/devices/${encodeURIComponent(deviceId)}/liveness/events?${locQ(params)}`),
  blockSessions: (blockId: string, params: Record<string, unknown>) =>
    apiGet<{ id: string; packageName: string; displayName: string; startUtc: string; endUtc: string | null; durationSeconds: number; lifeCategory: string }[]>(
      `/api/v1/mobile/analytics/timeline-blocks/${encodeURIComponent(blockId)}/sessions?${locQ(params)}`,
    ),

  devices: () => apiGet<import('./types').MobileDeviceDto[]>('/api/v1/mobile/devices'),
  /** sortBy 规格仅认 "data"，其余值按 lastSeenAtUtc 降序（等价默认） */
  devicesManage: (sortBy: 'data' | 'active') =>
    apiGet<import('./types').DeviceListItem[]>(
      `/api/v1/mobile/devices/manage${sortBy === 'data' ? '?sortBy=data' : ''}`,
    ),
  deviceDetail: (deviceId: string) =>
    apiGet<import('./types').DeviceDetail>(`/api/v1/mobile/devices/${encodeURIComponent(deviceId)}/detail`),
  renameDevice: (deviceId: string, displayName: string) =>
    apiPost(`/api/v1/mobile/devices/${encodeURIComponent(deviceId)}/rename`, { displayName }),
  deletePreview: (deviceId: string) =>
    apiGet<DeviceDeletePreview>(`/api/v1/mobile/devices/${encodeURIComponent(deviceId)}/delete-preview`),
  deleteDevice: (deviceId: string) =>
    apiDelete<string>(`/api/v1/mobile/devices/${encodeURIComponent(deviceId)}`),
  mergePreview: (body: components['schemas']['DeviceMergeRequest']) =>
    apiPost<DeviceMergePreview>('/api/v1/mobile/devices/merge/preview', body),
  merge: (body: components['schemas']['DeviceMergeRequest']) =>
    apiPost<string>('/api/v1/mobile/devices/merge', body),
  /**
   * 设备导出：响应是 application/json 文件流（非 ApiResponse 封装），
   * 必须用带认证的原生 fetch → Blob（规格 mobile.md:200-218）。
   * 返回 blob 与从 Content-Disposition 解析的文件名。
   */
  exportDeviceBlob: async (deviceId: string): Promise<{ blob: Blob; fileName: string | null }> => {
    const { fetchBlob } = await import('@/api/client')
    const path = `/api/v1/mobile/devices/${encodeURIComponent(deviceId)}/export`
    const blob = await fetchBlob(path)
    return { blob, fileName: null }
  },

  livenessOverview: (params: Record<string, unknown>) =>
    apiGet<LivenessOverview>(`/api/v1/mobile/liveness/overview?${locQ(params)}`),

  locationOverview: (params: Record<string, unknown>) =>
    apiGet<LocationOverview>(`/api/v1/mobile/location/analytics/overview?${locQ(params)}`),
  tracks: (params: Record<string, unknown>) =>
    apiGet<LocationTrack[]>(`/api/v1/mobile/location/analytics/tracks?${locQ(params)}`),
  frequentPlaces: (params: Record<string, unknown>) =>
    apiGet<{ home: FrequentPlace | null; places: FrequentPlace[] }>(
      `/api/v1/mobile/location/analytics/frequent-places?${locQ(params)}`,
    ),
  movementStats: (params: Record<string, unknown>) =>
    apiGet<MovementStats>(`/api/v1/mobile/location/analytics/movement-stats?${locQ(params)}`),
  segmentPoints: (segmentId: string, params: Record<string, unknown> & { cursor?: string; pageSize?: number }) =>
    apiGet<SegmentPointsPage>(
      `/api/v1/mobile/location/analytics/segments/${encodeURIComponent(segmentId)}/points?${locQ(params)}`,
    ),

  /** 全站唯一 cursor 分页（历史位置原始点表，cursor 栈回退） */
  rawPointsPager(segmentId: string, base: Record<string, unknown>) {
    const stack: (string | null)[] = [null]
    let index = 0
    return {
      get pageIndex() {
        return index
      },
      async fetch(pageDelta: number): Promise<SegmentPointsPage> {
        const target = Math.max(0, Math.min(index + pageDelta, stack.length - 1))
        index = target
        const res = await mobileApi.segmentPoints(segmentId, {
          ...base,
          cursor: stack[index] ?? undefined,
          pageSize: 200,
        })
        if (res.hasMore && res.nextCursor && index === stack.length - 1) stack.push(res.nextCursor)
        return res
      },
      reset() {
        stack.length = 1
        index = 0
      },
    }
  },
}
