import { apiGet, apiPost } from '@/api/client'
import type { OperationConfirmation } from './types'

export const operationsApi = {
  listPending: () => apiGet<OperationConfirmation[]>('/api/v1/operations/confirmations/pending'),
  get: (id: string) => apiGet<OperationConfirmation>(`/api/v1/operations/confirmations/${id}`),
  confirm: (id: string) => apiPost<OperationConfirmation>(`/api/v1/operations/confirmations/${id}/confirm`, {}),
  confirmSecondLevel: (id: string) =>
    apiPost<OperationConfirmation>(`/api/v1/operations/confirmations/${id}/confirm-second-level`, {}),
  confirmStrict: (id: string) =>
    apiPost<OperationConfirmation>(`/api/v1/operations/confirmations/${id}/confirm-strict`, {}),
  reject: (id: string) => apiPost<OperationConfirmation>(`/api/v1/operations/confirmations/${id}/reject`, {}),
}

/* ── 审计时间线（P2） ─────────────────────────────────────── */

export interface AuditVersionItem {
  id: string
  objectType: string
  objectId: string
  confirmationId: string | null
  source: string
  actor: string
  beforeJson: string
  afterJson: string
  changedFieldsJson: string
  createdAt: string
}

export interface AuditRestorePreview {
  objectType: string
  objectId: string
  summary: string
  requiresConfirmation: boolean
  changedFields: string[]
  beforeJson: string | null
  afterJson: string | null
}

export const auditApi = {
  timeline: (objectType: string, objectId: string) =>
    apiGet<{ items: AuditVersionItem[] }>(
      `/api/v1/operations/audit/${encodeURIComponent(objectType)}/${encodeURIComponent(objectId)}`,
    ),
  restorePreview: (auditVersionId: string) =>
    apiPost<AuditRestorePreview>(`/api/v1/operations/audit/${auditVersionId}/restore-preview`, {}),
  export: (params?: { start?: string; end?: string }) => {
    const q = new URLSearchParams()
    if (params?.start) q.set('start', params.start)
    if (params?.end) q.set('end', params.end)
    return apiGet<{ fileName: string; contentType: string; content: string }>(
      `/api/v1/operations/audit/export${q.toString() ? `?${q}` : ''}`,
    )
  },
}

/* ── 状态页（P3） ─────────────────────────────────────────── */

export interface SystemStatusDetail {
  summary: { status: number | string; label: string; message: string; checkedAt: string }
  components: { key: string; name: string; kind: number; status: number | string; message: string; checkedAt: string; details: Record<string, string> }[]
  nextSteps: string[]
}

export interface PcQualityResponse {
  overallStatus: number | string
  label: string
  message: string
  checkedAt: string
  components: { key: string; name: string; status: number | string; message: string; details: Record<string, string> }[]
  issues: { code: string; severity: number | string; componentKey: string; message: string; nextStep: string | null }[]
  nextSteps: string[]
}

export interface TrackerHealth {
  deviceId: string
  status: string
  uptimeSeconds: number
  hookActive: boolean
  browserConnected: boolean
  browserHeartbeatAgeSeconds: number | null
  siteConnected: boolean
  siteEventsUploaded: number
  reportedAt: string
}

export interface DaemonHeartbeat {
  deviceId: string
  daemonKind: string
  version: string
  lastSuccessfulUploadAt: string | null
  uploadQueueCount: number | null
  activityWatchState: string
  keyStatsState: string
  collectionPaused: boolean
  receivedAt: string
  plannedOfflineAt: string | null
}

export interface MobileQualityResponse {
  overallStatus: number | string
  label: string
  message: string
  checkedAt: string
  components: { key: string; name: string; status: number | string; message: string; details: Record<string, string> }[]
  issues: { code: string; severity: number | string; componentKey: string; message: string; nextStep: string | null }[]
  nextSteps: string[]
}

export const statusApi = {
  detail: () => apiGet<SystemStatusDetail>('/api/v1/status/'),
  pcQuality: () => apiGet<PcQualityResponse>('/api/v1/pc/quality'),
  trackerHealth: () => apiGet<TrackerHealth>('/api/v1/pc/tracker/health/latest'),
  daemonHeartbeats: () => apiGet<DaemonHeartbeat[]>('/api/v1/daemon/heartbeats'),
  mobileQuality: (deviceId?: string) =>
    apiGet<MobileQualityResponse>(`/api/v1/mobile/quality${deviceId ? `?deviceId=${encodeURIComponent(deviceId)}` : ''}`),
}
