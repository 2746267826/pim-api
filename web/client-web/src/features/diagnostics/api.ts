import { apiGet, apiPost, fetchBlob } from '@/api/client'
import type { DiagnosticEventUploadRequest, DiagnosticOverview, DiagnosticUploadResult } from './types'

function query(params: Record<string, string | number | null | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') search.set(key, String(value))
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

export const diagnosticsApi = {
  overview: (
    params: { deviceId?: string | null; platform?: string | null; limit?: number } = {},
    signal?: AbortSignal,
  ) =>
    apiGet<DiagnosticOverview>(
      `/api/v1/diagnostics/overview${query({ ...params, limit: params.limit ?? 100 })}`,
      { signal },
    ),

  uploadBatch: (request: DiagnosticEventUploadRequest, signal?: AbortSignal) =>
    apiPost<DiagnosticUploadResult>('/api/v1/diagnostics/events', request, { signal }),

  exportZip: (
    params: { deviceId?: string | null; platform?: string | null; days?: number },
    signal?: AbortSignal,
  ) =>
    fetchBlob(
      `/api/v1/diagnostics/export${query({ ...params, days: params.days ?? 7 })}`,
      signal,
      { refreshOn401: true },
    ),
}
