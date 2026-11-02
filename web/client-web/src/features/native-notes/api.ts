import { apiGet, apiPost, apiPutBinary, fetchBlob, fetchBoundedDownload, MAX_BOUNDED_DOWNLOAD_BYTES } from '@/api/client'
import type { components, paths } from '@/api/generated'
import type { NoteMaterialUpload, NoteMaterialVersion } from './material-model'

export type NoteLibrarySource = components['schemas']['NoteAsrSourceDto']
export type NoteLibrarySources = components['schemas']['NoteAsrSourcesDto']
export type NoteMaterialLimits = components['schemas']['NoteMaterialUploadLimitsDto']
type BundlePath = paths['/api/v1/native-notes/versions/{versionId}/bundle']['get']['parameters']['path']
type BundleContentType = keyof paths['/api/v1/native-notes/versions/{versionId}/bundle']['get']['responses'][200]['content']

export interface NativeNotesRequestContext {
  /** Aborted when the workspace closes or its owner/server identity changes. */
  signal?: AbortSignal
  /** Read at request time so a bound client always sees the current workspace session. */
  getSignal?: () => AbortSignal | undefined
  /** Called at each request boundary, including a 401 refresh replay. */
  assertActive: () => void
}

function createNativeNotesApi(context: NativeNotesRequestContext) {
  function scoped<T>(
    querySignal: AbortSignal | undefined,
    action: (options: { signal?: AbortSignal; assertActive: () => void }) => Promise<T>,
  ): Promise<T> {
    context.assertActive()
    const merged = mergeAbortSignals(context.getSignal?.() ?? context.signal, querySignal)
    return action({ signal: merged.signal, assertActive: context.assertActive })
      .finally(merged.dispose)
  }

  return {
    limits: (signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiGet<NoteMaterialLimits>('/api/v1/native-notes/upload-limits', options)),

    sources: (offset: number, limit = 50, signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiGet<NoteLibrarySources>(
          `/api/v1/native-notes/derived-tasks/sources?offset=${offset}&limit=${limit}`,
          options,
        )),

    version: (versionId: string, signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiGet<NoteMaterialVersion>(
          `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}`,
          options,
        )),

    begin: (body: components['schemas']['BeginNoteMaterialUploadRequest'], signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiPost<NoteMaterialUpload>('/api/v1/native-notes/versions', body, options)),

    uploadPart: (
      versionId: string,
      fileIndex: number,
      partIndex: number,
      body: Blob,
      sha256: string,
      signal?: AbortSignal,
    ) => scoped(signal, (options) =>
      apiPutBinary<string>(
        `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/files/${fileIndex}/parts/${partIndex}?sha256=${encodeURIComponent(sha256)}`,
        body,
        'application/octet-stream',
        options,
      )),

    complete: (versionId: string, signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiPost<NoteMaterialUpload>(
          `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/complete`,
          {},
          options,
        )),

    promote: (versionId: string, expectedCurrentVersionId: string | null, signal?: AbortSignal) =>
      scoped(signal, (options) =>
        apiPost<NoteMaterialUpload>(
          `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/promote`,
          { expectedCurrentVersionId },
          options,
        )),

    fileBytes: (versionId: string, fileIndex: number, signal?: AbortSignal) =>
      scoped(signal, async (options) => {
        const blob = await fetchBlob(
          `/api/v1/native-notes/versions/${encodeURIComponent(versionId)}/files/${fileIndex}`,
          options.signal,
          { refreshOn401: true, assertActive: options.assertActive },
        )
        context.assertActive()
        return new Uint8Array(await blob.arrayBuffer())
      }),

    downloadBundle: (versionId: string, signal?: AbortSignal) =>
      scoped(signal, (options) => {
        const pathParams: BundlePath = { versionId }
        const contentType: BundleContentType = 'application/zip'
        const filename = `${pathParams.versionId.toLowerCase()}.pimnote.zip`
        return fetchBoundedDownload(
          `/api/v1/native-notes/versions/${encodeURIComponent(pathParams.versionId)}/bundle`,
          options.signal,
          {
            refreshOn401: true,
            assertActive: options.assertActive,
            expectedFilename: filename,
            expectedContentType: contentType,
            maxBytes: MAX_BOUNDED_DOWNLOAD_BYTES,
          },
        )
      }),
  }
}

const nativeNotesApi = createNativeNotesApi({ assertActive: () => undefined })
export type NativeNotesClient = typeof nativeNotesApi

export function bindNativeNotesApi(context: NativeNotesRequestContext): NativeNotesClient {
  return createNativeNotesApi(context)
}

function mergeAbortSignals(
  first?: AbortSignal,
  second?: AbortSignal,
): { signal?: AbortSignal; dispose: () => void } {
  const signals = [first, second].filter((signal): signal is AbortSignal => Boolean(signal))
  if (!signals.length) return { signal: undefined, dispose: () => undefined }
  const alreadyAborted = signals.find((signal) => signal.aborted)
  if (alreadyAborted) return { signal: alreadyAborted, dispose: () => undefined }
  if (signals.length === 1) return { signal: signals[0], dispose: () => undefined }

  const controller = new AbortController()
  const relay = (event: Event) => {
    const source = event.currentTarget as AbortSignal
    dispose()
    controller.abort(source.reason)
  }
  const dispose = () => {
    for (const signal of signals) signal.removeEventListener('abort', relay)
  }
  for (const signal of signals) signal.addEventListener('abort', relay, { once: true })
  return { signal: controller.signal, dispose }
}
