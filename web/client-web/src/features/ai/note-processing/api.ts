import { apiGet, apiPost, apiPut } from '@/api/client'
import type { components } from '@/api/generated'

export type NoteProcessingStatus = components['schemas']['NoteProcessingStatusDto']
export type NoteProcessingChain = components['schemas']['NoteProcessingChainDto']
export type StartNoteProcessing = components['schemas']['StartNoteProcessingRequest']
export type NoteProcessingPlan = components['schemas']['NoteProcessingPlanDto']
export type NoteProcessingConsent = components['schemas']['NoteProcessingConsent']
export type NoteProcessingPreferences = components['schemas']['NoteProcessingPreferencesDto']
export type SaveNoteProcessingPreferences = components['schemas']['SaveNoteProcessingPreferencesRequest']

export const noteProcessingApi = {
  preferences: (signal?: AbortSignal) =>
    apiGet<NoteProcessingPreferences>('/api/v1/native-notes/processing/preferences', { signal }),
  savePreferences: (body: SaveNoteProcessingPreferences) =>
    apiPut<NoteProcessingPreferences>('/api/v1/native-notes/processing/preferences', body),
  status: (noteId: string, versionId: string, signal?: AbortSignal) =>
    apiGet<NoteProcessingStatus>(
      `/api/v1/native-notes/processing?noteId=${encodeURIComponent(noteId)}&versionId=${encodeURIComponent(versionId)}`,
      { signal },
    ),
  plan: (noteId: string, versionId: string, signal?: AbortSignal) =>
    apiGet<NoteProcessingPlan>(
      `/api/v1/native-notes/processing/plan?noteId=${encodeURIComponent(noteId)}&versionId=${encodeURIComponent(versionId)}`,
      { signal },
    ),
  start: (body: StartNoteProcessing) =>
    apiPost<NoteProcessingStatus>('/api/v1/native-notes/processing/start', body),
}
