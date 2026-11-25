// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGet = vi.hoisted(() => vi.fn())
const apiPost = vi.hoisted(() => vi.fn())
const apiDelete = vi.hoisted(() => vi.fn())

vi.mock('@/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return { ...actual, apiGet, apiPost, apiDelete }
})

import { bindNativeNotesApi } from './api'

beforeEach(() => {
  apiGet.mockReset()
  apiPost.mockReset()
  apiDelete.mockReset()
})

describe('native notes workspace-bound API', () => {
  it('merges the workspace abort signal into version reads', async () => {
    const controller = new AbortController()
    const assertActive = vi.fn()
    const api = bindNativeNotesApi({ getSignal: () => controller.signal, assertActive })
    apiGet.mockResolvedValue([])

    await expect(api.versions('note-id')).resolves.toEqual([])
    const [path, options] = apiGet.mock.calls[0] as [
      string,
      { signal?: AbortSignal; assertActive?: () => void },
    ]
    expect(path).toBe('/api/v1/native-notes/note-id/versions')
    expect(options.assertActive).toBe(assertActive)
    expect(options.signal).toBeInstanceOf(AbortSignal)
    expect(options.signal?.aborted).toBe(false)

    controller.abort()
    expect(options.signal?.aborted).toBe(true)
  })

  it('refuses version reads and promote after the workspace is no longer active', () => {
    const switched = new Error('workspace switched')
    const api = bindNativeNotesApi({
      assertActive: () => {
        throw switched
      },
    })

    expect(() => api.current('note-id')).toThrow(switched)
    expect(() => api.promote('version-id', 'current-id')).toThrow(switched)
    expect(apiGet).not.toHaveBeenCalled()
    expect(apiPost).not.toHaveBeenCalled()
  })

  it('passes the workspace assertion and expected current version to promote', async () => {
    const assertActive = vi.fn()
    const api = bindNativeNotesApi({ assertActive })
    apiPost.mockResolvedValue({ versionId: 'version-id' })

    await api.promote('version-id', 'current-id')

    expect(apiPost).toHaveBeenCalledWith(
      '/api/v1/native-notes/versions/version-id/promote',
      { expectedCurrentVersionId: 'current-id' },
      expect.objectContaining({ assertActive }),
    )
  })

  it('soft-deletes the selected note through the recycle-bin entry', async () => {
    const assertActive = vi.fn()
    const api = bindNativeNotesApi({ assertActive })
    apiDelete.mockResolvedValue({ noteId: 'note-id' })

    await api.deleteNote('note/id')

    expect(apiDelete).toHaveBeenCalledWith(
      '/api/v1/native-notes/note%2Fid',
      expect.objectContaining({ assertActive }),
    )
  })
})
