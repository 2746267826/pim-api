import { useQuery } from '@tanstack/react-query'
import { apiGet } from './client'
import type { ApiVersionInfo, SystemStatusSummary } from './types'

/**
 * 应用显示版本（页脚 / 关于里的 "v1.0.0"）。
 *
 * 来源是仓根 `VERSION` 文件，由 CI 以 `VITE_DISPLAY_VERSION` 在构建期注入：
 * 正式版是干净的 x.y.z，dev/PR 构建带 `-dev+sha` / `-pr.N.sha` 后缀。
 * 只影响"给人看"，更新检查与产物比较仍用 API 返回的内部版本号。
 *
 * 没注入时（本地 `npm run dev`）显式回落 `0.0.0-local`，不写死一个看起来像真
 * 版本的号 —— 之前写死的 0.1.0 一直被当成真实版本，核对发布时对不上号。
 */
const injectedDisplayVersion = import.meta.env.VITE_DISPLAY_VERSION
export const APP_VERSION =
  typeof injectedDisplayVersion === 'string' && injectedDisplayVersion.trim() !== ''
    ? injectedDisplayVersion.trim()
    : '0.0.0-local'

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
