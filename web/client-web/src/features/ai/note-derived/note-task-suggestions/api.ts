import { apiGet, apiPost, apiPut } from '@/api/client'
import type { components } from '@/api/generated'

export type NoteTaskSuggestion = components['schemas']['NoteTaskSuggestionDto']
export type NoteTaskSuggestionExtraction = components['schemas']['NoteTaskSuggestionExtractionDto']
export type NoteTaskSuggestionList = components['schemas']['NoteTaskSuggestionListDto']
export type NoteTaskSuggestionStatus = components['schemas']['NoteTaskSuggestionStatusDto']
export type NoteTaskSuggestionExtractionStatus = components['schemas']['NoteTaskSuggestionExtractionStatusDto']
export type StartNoteTaskSuggestionExtraction = components['schemas']['StartNoteTaskSuggestionExtractionRequest']
export type UpdateNoteTaskSuggestion = components['schemas']['UpdateNoteTaskSuggestionRequest']
export type ConfirmNoteTaskSuggestion = components['schemas']['ConfirmNoteTaskSuggestionRequest']
export type DismissNoteTaskSuggestion = components['schemas']['DismissNoteTaskSuggestionRequest']

const root = '/api/v1/native-notes/task-suggestions'

export const noteTaskSuggestionsApi = {
  list: (noteId: string, signal?: AbortSignal) =>
    apiGet<NoteTaskSuggestionList>(`${root}?noteId=${encodeURIComponent(noteId)}`, { signal }),
  get: (id: string, signal?: AbortSignal) =>
    apiGet<NoteTaskSuggestion>(`${root}/${encodeURIComponent(id)}`, { signal }),
  startExtraction: (body: StartNoteTaskSuggestionExtraction) =>
    apiPost<NoteTaskSuggestionExtraction>(`${root}/extractions`, body),
  extraction: (id: string, signal?: AbortSignal) =>
    apiGet<NoteTaskSuggestionExtraction>(`${root}/extractions/${encodeURIComponent(id)}`, { signal }),
  update: (id: string, body: UpdateNoteTaskSuggestion) =>
    apiPut<NoteTaskSuggestion>(`${root}/${encodeURIComponent(id)}`, body),
  confirm: (id: string, body: ConfirmNoteTaskSuggestion) =>
    apiPost<NoteTaskSuggestion>(`${root}/${encodeURIComponent(id)}/confirm`, body),
  dismiss: (id: string, body: DismissNoteTaskSuggestion) =>
    apiPost<NoteTaskSuggestion>(`${root}/${encodeURIComponent(id)}/dismiss`, body),
}
