import { ApiError, apiDelete, apiGet, apiPost, apiPut, fetchBlob } from '@/api/client'
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
export type NoteAsrTask = components['schemas']['NoteAsrTaskDto']
export type NoteAsrStart = components['schemas']['StartNoteAsrRequest']
export type NoteAsrRetry = components['schemas']['RetryNoteAsrRequest']
export type NoteAsrSegments = components['schemas']['NoteAsrSegmentsDto']
export type NoteAsrSources = components['schemas']['NoteAsrSourcesDto']
export type NoteAsrDestinations = components['schemas']['NoteAsrDestinationsDto']
export type NoteAsrDestination = components['schemas']['NoteAsrDestinationDto']
export type NoteAsrRouteConfig = components['schemas']['NoteAsrRouteConfigDto']
export type NoteAsrRouteConfigSave = components['schemas']['UpdateNoteAsrRouteConfigRequest']
export type NoteAsrAttemptDetail = components['schemas']['NoteAsrAttemptDetailDto']
export type NoteAsrNode = components['schemas']['NoteAsrNodeDto']
export type NoteAsrBatchJob = components['schemas']['NoteAsrBatchJobDto']
export type NoteAsrReprocessHistory = components['schemas']['NoteAsrReprocessHistoryDto']
export type NoteAsrReprocessRequest = components['schemas']['ReprocessNoteAsrRequest']
export type NoteAsrReprocessResult = components['schemas']['NoteAsrReprocessResultDto']
export type NoteAsrBatchFromTaskRequest = components['schemas']['StartNoteAsrBatchFromTaskRequest']
export type NoteAsrBatchRetryFailedRequest = components['schemas']['RetryFailedNoteAsrBatchRequest']
export type NoteAsrBatchTarget = components['schemas']['NoteAsrBatchTarget']
export type NoteAsrBatchSubmission = components['schemas']['NoteAsrBatchSubmissionResultDto']
export type NoteAsrReprocessReason = components['schemas']['NoteAsrReprocessReason']
export type NoteAsrReprocessRoute = components['schemas']['NoteAsrReprocessRoute']
export type CallItem = components['schemas']['AiRequestLogListItemDto']
export type CallDetail = components['schemas']['AiRequestLogDetailDto']
export type Usage = components['schemas']['AiUsageSummaryDto']

export const aiApi = {
  noteAsrSources: (offset: number, signal?: AbortSignal) => apiGet<NoteAsrSources>(`/api/v1/native-notes/asr-tasks/sources?offset=${offset}&limit=20`, { signal }),
  noteAsrDestinations: (signal?: AbortSignal) => apiGet<NoteAsrDestinations>('/api/v1/native-notes/asr-tasks/destinations', { signal }),
  noteAsrTasks: (signal?: AbortSignal) => apiGet<NoteAsrTask[]>('/api/v1/native-notes/asr-tasks', { signal }),
  noteAsrStart: (body: NoteAsrStart) => apiPost<NoteAsrTask>('/api/v1/native-notes/asr-tasks', body),
  noteAsrTask: (id: string, signal?: AbortSignal) => apiGet<NoteAsrTask>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(id)}`, { signal }),
  noteAsrTaskOrNull: async (id: string, signal?: AbortSignal): Promise<NoteAsrTask | null> => {
    try {
      return await apiGet<NoteAsrTask>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(id)}`, { signal })
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 404) return null
      throw failure
    }
  },
  noteAsrSegments: (id: string, offset: number, signal?: AbortSignal) => apiGet<NoteAsrSegments>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(id)}/segments?offset=${offset}&limit=10`, { signal }),
  noteAsrStop: (id: string) => apiPost<NoteAsrTask>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(id)}/stop`, {}),
  noteAsrRetry: (id: string, body: NoteAsrRetry) => apiPost<NoteAsrTask>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(id)}/retry`, body),
  asrReprocessHistory: (sourceTaskId: string, expectedUserId: string, offset = 0, limit = 20, signal?: AbortSignal) =>
    apiGet<NoteAsrReprocessHistory>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(sourceTaskId)}/reprocess-history?expectedUserId=${encodeURIComponent(expectedUserId)}&offset=${offset}&limit=${limit}`, { signal }),
  asrReprocess: (sourceTaskId: string, body: NoteAsrReprocessRequest) =>
    apiPost<NoteAsrReprocessResult>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(sourceTaskId)}/reprocess`, body),
  asrRouteConfig: (signal?: AbortSignal) => apiGet<NoteAsrRouteConfig>('/api/v1/native-notes/asr-routing/config', { signal }),
  asrRouteConfigSave: (body: NoteAsrRouteConfigSave) => apiPut<NoteAsrRouteConfig>('/api/v1/native-notes/asr-routing/config', body),
  asrRoutingAttempts: (taskId?: string, signal?: AbortSignal) =>
    apiGet<NoteAsrAttemptDetail[]>(`/api/v1/native-notes/asr-routing/attempts${taskId ? `?taskId=${encodeURIComponent(taskId)}` : ''}`, { signal }),
  asrRoutingAttemptStop: (attemptId: string) => apiPost<NoteAsrAttemptDetail>(`/api/v1/native-notes/asr-routing/attempts/${encodeURIComponent(attemptId)}/stop`, {}),
  asrRoutingNodes: (signal?: AbortSignal) => apiGet<NoteAsrNode[]>('/api/v1/native-notes/asr-routing/nodes', { signal }),
  asrBatchJobs: (signal?: AbortSignal) => apiGet<NoteAsrBatchJob[]>('/api/v1/native-notes/asr-routing/batches', { signal }),
  asrBatchJob: (batchJobId: string, signal?: AbortSignal) =>
    apiGet<NoteAsrBatchJob>(`/api/v1/native-notes/asr-routing/batches/${encodeURIComponent(batchJobId)}`, { signal }),
  asrBatchPoll: (batchJobId: string) => apiPost<NoteAsrBatchJob>(`/api/v1/native-notes/asr-routing/batches/${encodeURIComponent(batchJobId)}/poll`, {}),
  asrBatchCancel: (batchJobId: string) => apiPost<NoteAsrBatchJob>(`/api/v1/native-notes/asr-routing/batches/${encodeURIComponent(batchJobId)}/cancel`, {}),
  asrBatchFromTask: (sourceTaskId: string, body: NoteAsrBatchFromTaskRequest) =>
    apiPost<NoteAsrBatchSubmission>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(sourceTaskId)}/batch-from-task`, body),
  asrBatchRetryFailed: (sourceJobId: string, body: NoteAsrBatchRetryFailedRequest) =>
    apiPost<NoteAsrBatchSubmission>(`/api/v1/native-notes/asr-tasks/${encodeURIComponent(sourceJobId)}/batch-retry-failed`, body),
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
