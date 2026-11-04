import { describe, expect, it } from 'vitest'
import { groupNoteLibrary } from './library-model'

const noteId = '11111111-1111-4111-8111-111111111111'

describe('native note library grouping', () => {
  it('maps the summary DTO without inventing timestamps, recordings, or version counts', () => {
    const items = groupNoteLibrary([
      { noteId, currentVersionId: '33333333-3333-4333-8333-333333333333', title: '当前选用标题', hasConflict: true },
    ])

    expect(items).toEqual([{
      noteId,
      title: '当前选用标题',
      currentVersionId: '33333333-3333-4333-8333-333333333333',
      hasConflict: true,
    }])
  })

  it('does not merge different notes with the same title', () => {
    const other = '99999999-9999-4999-8999-999999999999'
    expect(groupNoteLibrary([
      { noteId, currentVersionId: '22222222-2222-4222-8222-222222222222', title: '课堂笔记', hasConflict: false },
      { noteId: other, currentVersionId: '33333333-3333-4333-8333-333333333333', title: '课堂笔记', hasConflict: false },
    ])).toHaveLength(2)
  })
})
