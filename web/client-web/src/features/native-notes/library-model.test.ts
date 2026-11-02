import { describe, expect, it } from 'vitest'
import type { NoteLibrarySource } from './api'
import { groupNoteLibrary } from './library-model'

const noteId = '11111111-1111-4111-8111-111111111111'

function source(versionId: string, overrides: Partial<NoteLibrarySource> = {}): NoteLibrarySource {
  return {
    noteId,
    versionId,
    title: '课堂笔记',
    createdAt: '2026-11-01T10:00:00Z',
    isCurrent: false,
    hasConflict: false,
    recordings: 0,
    totalSamples: 0,
    sampleRate: 16000,
    ...overrides,
  }
}

describe('native note library grouping', () => {
  it('selects the explicit current material version and keeps conflict history visible', () => {
    const items = groupNoteLibrary([
      source('22222222-2222-4222-8222-222222222222', {
        createdAt: '2026-11-01T11:00:00Z',
        hasConflict: true,
        title: '冲突候选标题',
      }),
      source('33333333-3333-4333-8333-333333333333', {
        createdAt: '2026-11-01T10:00:00Z',
        isCurrent: true,
        title: '当前选用标题',
        recordings: 2,
        totalSamples: 32000,
      }),
    ])

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({
      noteId,
      title: '当前选用标题',
      versionId: '33333333-3333-4333-8333-333333333333',
      isCurrent: true,
      hasConflict: true,
      recordings: 2,
      versionCount: 2,
      conflictVersionIds: ['22222222-2222-4222-8222-222222222222'],
    })
  })

  it('does not merge different notes with the same title', () => {
    const other = '99999999-9999-4999-8999-999999999999'
    expect(groupNoteLibrary([
      source('22222222-2222-4222-8222-222222222222', { isCurrent: true }),
      source('33333333-3333-4333-8333-333333333333', {
        noteId: other,
        title: '课堂笔记',
        isCurrent: true,
      }),
    ])).toHaveLength(2)
  })
})
