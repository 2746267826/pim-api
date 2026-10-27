import { apiDelete, apiGet, apiPost, apiPut, fetchBlob } from '@/api/client'
import type { components } from '@/api/generated'
import type { PagedResult } from '@/api/types'

export type Model = components['schemas']['AiModelDto']
export type ModelSettings = components['schemas']['AiModelSettingsDto']
export type SaveModel = components['schemas']['SaveAiModelRequest']
export type SaveSettings = components['schemas']['SaveAiModelSettingsRequest']
export type Capability = components['schemas']['AiModelCapability']
export type CallRequest = components['schemas']['AiTextCallRequest']
export type CallResult = components['schemas']['AiTextCallResultDto']
export type VisionRequest = components['schemas']['AiVisionCallRequest']
export type CallImage = components['schemas']['AiCallImageDto']
export type AsrPreset = components['schemas']['AiAsrPresetDto']
export type AsrRequest = components['schemas']['AiAsrCallRequest']
export type CallAudio = components['schemas']['AiCallAudioDto']
export type CallItem = components['schemas']['AiRequestLogListItemDto']
export type CallDetail = components['schemas']['AiRequestLogDetailDto']
export type Usage = components['schemas']['AiUsageSummaryDto']

export const aiApi = {
  asrPresets: (signal?: AbortSignal) => apiGet<AsrPreset[]>('/api/v1/ai/asr-presets', { signal }),
  asrCall: (body: AsrRequest, signal: AbortSignal) => apiPost<CallResult>('/api/v1/ai/asr-calls', body, { signal }),
  asrLookup: (id: string) => apiGet<CallResult>(`/api/v1/ai/asr-calls/${encodeURIComponent(id)}`),
  audio: (id: string, signal?: AbortSignal) => apiGet<CallAudio | null>(`/api/v1/ai/requests/${encodeURIComponent(id)}/audio`, { signal }),
  audioContent: (id: string, signal: AbortSignal) => fetchBlob(`/api/v1/ai/requests/${encodeURIComponent(id)}/audio/content`, signal, { refreshOn401: true }),
  models: (signal?: AbortSignal) => apiGet<Model[]>('/api/v1/ai/models', { signal }),
  settings: (signal?: AbortSignal) => apiGet<ModelSettings>('/api/v1/ai/model-settings', { signal }),
  saveModel: (id: string | undefined, body: SaveModel) => id
    ? apiPut<Model>(`/api/v1/ai/models/${encodeURIComponent(id)}`, body)
    : apiPost<Model>('/api/v1/ai/models', body),
  removeModel: (model: Model) => apiDelete<boolean>(`/api/v1/ai/models/${encodeURIComponent(model.id)}?revision=${encodeURIComponent(model.revision)}`),
  saveSettings: (body: SaveSettings) => apiPut<ModelSettings>('/api/v1/ai/model-settings', body),
  call: (body: CallRequest, signal: AbortSignal) => apiPost<CallResult>('/api/v1/ai/text-calls', body, { signal }),
  lookup: (id: string) => apiGet<CallResult>(`/api/v1/ai/text-calls/${encodeURIComponent(id)}`),
  visionCall: (body: VisionRequest, signal: AbortSignal) => apiPost<CallResult>('/api/v1/ai/vision-calls', body, { signal }),
  visionLookup: (id: string) => apiGet<CallResult>(`/api/v1/ai/vision-calls/${encodeURIComponent(id)}`),
  images: (logId: string, signal?: AbortSignal) => apiGet<CallImage[]>(`/api/v1/ai/requests/${encodeURIComponent(logId)}/images`, { signal }),
  image: (logId: string, imageId: string, signal: AbortSignal) => fetchBlob(`/api/v1/ai/requests/${encodeURIComponent(logId)}/images/${encodeURIComponent(imageId)}`, signal, { refreshOn401: true }),
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
