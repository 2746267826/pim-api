import { apiGet, apiPost } from '@/api/client'
import type { components } from '@/api/generated'

export type NoteProcessingStatus = components['schemas']['NoteProcessingStatusDto']
export type NoteProcessingChain = components['schemas']['NoteProcessingChainDto']
export type StartNoteProcessing = components['schemas']['StartNoteProcessingRequest']

export const noteProcessingApi = {
  status: (noteId: string, versionId: string, signal?: AbortSignal) =>
    apiGet<NoteProcessingStatus>(
      `/api/v1/native-notes/processing?noteId=${encodeURIComponent(noteId)}&versionId=${encodeURIComponent(versionId)}`,
      { signal },
    ),
  start: (body: StartNoteProcessing) =>
    apiPost<NoteProcessingStatus>('/api/v1/native-notes/processing/start', body),
}
