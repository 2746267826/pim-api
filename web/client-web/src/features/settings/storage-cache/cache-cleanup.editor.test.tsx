// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { NativeNotesClient } from '@/features/native-notes/api'
import type { NoteMaterialVersion } from '@/features/native-notes/material-model'
import { NoteEditorPane } from '@/features/native-notes/note-material-editor'
import { resetUnsavedDraftsForTests } from '@/lib/unsaved-drafts'
import { clearTemporaryWebCache, snapshotTemporaryCache } from './cache-cleanup'

const ownerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const serverBinding = 'https://pim-a.example'
const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'
const blockId = '33333333-3333-4333-8333-333333333333'

const material: NoteMaterialVersion = {
  inputJson: JSON.stringify({
    schemaVersion: 1,
    createdAtMillis: 1,
    id: versionId,
    note: {
      schemaVersion: 1,
      id: noteId,
      revision: 0,
      title: '课堂笔记',
      createdAtMillis: 1,
      blocks: [{ id: blockId, kind: 'markdown', text: '服务器上的正文' }],
    },
    recordings: [],
  }),
  manifestJson: JSON.stringify({
    schemaVersion: 1,
    versionId,
    noteId,
    inputSha256: 'a'.repeat(64),
    revision: 0,
    files: [],
  }),
  upload: {
    versionId,
    noteId,
    baseVersionId: null,
    currentVersionId: versionId,
    phase: 'Uploaded',
    createdAt: '2026-11-01T10:00:00Z',
    expiresAt: '2026-11-01T11:00:00Z',
    isCurrent: true,
    hasConflict: false,
    limits: {
      partSizeBytes: 1024 * 1024,
      maxMetadataCharacters: 8_388_608,
      maxVersionBytes: 64 * 1024 * 1024 * 1024,
      maxUserReservedBytes: 64 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      maxPendingVersions: 10,
      maxVersionsPerUser: 1000,
      uploadExpiryHours: 1,
    },
    files: [],
  },
}

let client: QueryClient

afterEach(() => {
  cleanup()
  client?.clear()
  resetUnsavedDraftsForTests()
  vi.restoreAllMocks()
})

describe('临时缓存清理与真实笔记编辑器', () => {
  it('保留 active 编辑器查询、未保存正文与焦点，只移除未观察查询', async () => {
    const api = {
      version: vi.fn(async () => material),
      fileBytes: vi.fn(),
    } as unknown as NativeNotesClient
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(['inactive-query'], { value: 1 })

    const editor = () => (
      <QueryClientProvider client={client}>
        <NoteEditorPane
          ownerId={ownerId}
          serverBinding={serverBinding}
          api={api}
          showVersionDetails
          versionId={versionId}
          onSaved={vi.fn()}
          promoteInFlight={false}
          promotingVersionId=""
          onPromoteVersion={vi.fn()}
        />
      </QueryClientProvider>
    )
    const view = render(editor())

    const textarea = await screen.findByLabelText('正文块 1') as HTMLTextAreaElement
    textarea.focus()
    fireEvent.change(textarea, { target: { value: '尚未保存的正文' } })

    expect(document.activeElement).toBe(textarea)
    expect(textarea.value).toBe('尚未保存的正文')
    expect(snapshotTemporaryCache(client, ownerId)).toMatchObject({
      queryEntries: 1,
      inUseQueryEntries: 2,
    })

    await act(async () => {
      const cleared = await clearTemporaryWebCache(client, ownerId)
      expect(cleared.queryEntries).toBe(1)
    })
    view.rerender(editor())

    expect(screen.getByLabelText('正文块 1')).toBe(textarea)
    expect(document.activeElement).toBe(textarea)
    expect(textarea.value).toBe('尚未保存的正文')
    expect(api.version).toHaveBeenCalledTimes(1)
    expect(client.getQueryCache().find({ exact: true, queryKey: ['inactive-query'] })).toBeUndefined()
    expect(client.getQueryCache().find({
      exact: true,
      queryKey: ['native-note-material', ownerId, serverBinding, versionId],
    })).toBeDefined()
    expect(client.getQueryCache().find({
      exact: true,
      queryKey: ['native-note-material-hydrated', ownerId, serverBinding, versionId],
    })).toBeDefined()
  }, 30_000)
})
