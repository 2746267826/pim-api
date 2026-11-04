import { apiGet } from '@/api/client'
import type { components } from '@/api/generated'

export type NativeNotePage = components['schemas']['NativeNotePageDto']
export type NativeNoteSummary = components['schemas']['NativeNoteSummaryDto']
export type NoteMaterialVersion = components['schemas']['NoteMaterialVersionDto']

export const nativeNotesApi = {
  list: (offset: number, signal?: AbortSignal) =>
    apiGet<NativeNotePage>(`/api/v1/native-notes?offset=${offset}&limit=20`, { signal }),
  current: (noteId: string, signal?: AbortSignal) =>
    apiGet<NoteMaterialVersion>(`/api/v1/native-notes/${encodeURIComponent(noteId)}/current`, { signal }),
}
