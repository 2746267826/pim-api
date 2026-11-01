import { apiGet, apiPost, apiPutBinary, fetchBlob } from '@/api/client'
import type { components } from '@/api/generated'
import type { NoteMaterialUpload, NoteMaterialVersion } from './material-model'

export type NoteLibrarySource = components['schemas']['NoteAsrSourceDto']
export type NoteLibrarySources = components['schemas']['NoteAsrSourcesDto']
export type NoteMaterialLimits = components['schemas']['NoteMaterialUploadLimitsDto']

export const nativeNotesApi = {
  limits: (signal?: AbortSignal) =>
    apiGet<NoteMaterialLimits>('/api/v1/native-notes/upload-limits', { signal }),

  sources: (offset: number, limit = 50, signal?: AbortSignal) =>
    apiGet<NoteLibrarySources>(
      `/api/v1/native-notes/derived-tasks/sources?offset=${offset}&limit=${limit}`,
      { signal },
    ),

  version: (versionId: string, signal?: AbortSignal) =>
    apiGet<NoteMaterialVersion>(`/api/v1/native-notes/versions/${encodeURIComponent(versionId)}`, { signal }),

  begin: (body: components['schemas']['BeginNoteMaterialUploadRequest']) =>
    apiPost<NoteMaterialUpload>('/api/v1/native-notes/versions', body),

  uploadPart: (
    versionId: string,
    fileIndex: number,
    partIndex: number,
    body: Blob,
    sha256: string,
  ) => apiPutBinary<string>(
    `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/files/${fileIndex}/parts/${partIndex}?sha256=${encodeURIComponent(sha256)}`,
    body,
  ),

  complete: (versionId: string) =>
    apiPost<NoteMaterialUpload>(
      `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/complete`,
      {},
    ),

  promote: (versionId: string, expectedCurrentVersionId: string | null) =>
    apiPost<NoteMaterialUpload>(
      `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/promote`,
      { expectedCurrentVersionId },
    ),

  fileBytes: async (versionId: string, fileIndex: number, signal?: AbortSignal) => {
    const blob = await fetchBlob(
      `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/files/${fileIndex}`,
      signal,
      { refreshOn401: true },
    )
    return new Uint8Array(await blob.arrayBuffer())
  },
}
