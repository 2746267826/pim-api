import { useQuery } from '@tanstack/react-query'
import { apiGet } from './client'
import type { ApiVersionInfo, SystemStatusSummary } from './types'

/** 应用本地版本（页脚"本地版本"展示；与 latestVersion 比对判断更新） */
export const APP_VERSION = '0.1.0'

/** GET /api/version（匿名端点，非 ApiResponse 封装） */
export function useVersionInfo() {
  return useQuery({
    queryKey: ['version'],
    queryFn: () => apiGet<ApiVersionInfo>('/api/version', { auth: false }),
    staleTime: 10 * 60_000,
    retry: false,
    meta: { silent: true },
  })
}

/** GET /api/v1/status/summary（侧边栏状态点，60s 轮询） */
export function useStatusSummary() {
  return useQuery({
    queryKey: ['status', 'summary'],
    queryFn: () => apiGet<SystemStatusSummary>('/api/v1/status/summary'),
    refetchInterval: 60_000,
    staleTime: 30_000,
    retry: false,
    meta: { silent: true },
  })
}
