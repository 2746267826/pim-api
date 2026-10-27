import { apiDelete, apiGet, apiPost, apiPut } from '@/api/client'
import type { components } from '@/api/generated'
import type { PagedResult } from '@/api/types'

export type Model = components['schemas']['AiModelDto']
export type ModelSettings = components['schemas']['AiModelSettingsDto']
export type SaveModel = components['schemas']['SaveAiModelRequest']
export type SaveSettings = components['schemas']['SaveAiModelSettingsRequest']
export type Capability = components['schemas']['AiModelCapability']
export type CallRequest = components['schemas']['AiTextCallRequest']
export type CallResult = components['schemas']['AiTextCallResultDto']
export type CallItem = components['schemas']['AiRequestLogListItemDto']
export type CallDetail = components['schemas']['AiRequestLogDetailDto']
export type Usage = components['schemas']['AiUsageSummaryDto']

export const aiApi = {
  models: (signal?: AbortSignal) => apiGet<Model[]>('/api/v1/ai/models', { signal }),
  settings: (signal?: AbortSignal) => apiGet<ModelSettings>('/api/v1/ai/model-settings', { signal }),
  saveModel: (id: string | undefined, body: SaveModel) => id
    ? apiPut<Model>(`/api/v1/ai/models/${encodeURIComponent(id)}`, body)
    : apiPost<Model>('/api/v1/ai/models', body),
  removeModel: (model: Model) => apiDelete<boolean>(`/api/v1/ai/models/${encodeURIComponent(model.id)}?revision=${encodeURIComponent(model.revision)}`),
  saveSettings: (body: SaveSettings) => apiPut<ModelSettings>('/api/v1/ai/model-settings', body),
  call: (body: CallRequest, signal: AbortSignal) => apiPost<CallResult>('/api/v1/ai/text-calls', body, { signal }),
  lookup: (id: string) => apiGet<CallResult>(`/api/v1/ai/text-calls/${encodeURIComponent(id)}`),
  history: (page: number, status: string, model: string, signal?: AbortSignal) => {
    const q = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (status) q.set('status', status)
    if (model.trim()) q.set('model', model.trim())
    return apiGet<PagedResult<CallItem>>(`/api/v1/ai/requests?${q}`, { signal })
  },
  detail: (id: string, signal?: AbortSignal) => apiGet<CallDetail>(`/api/v1/ai/requests/${encodeURIComponent(id)}`, { signal }),
  usage: (signal?: AbortSignal) => apiGet<Usage>('/api/v1/ai/usage/summary', { signal }),
  legacy: (signal?: AbortSignal) => apiGet<components['schemas']['AiStatusDto']>('/api/v1/ai/status', { signal }),
}
