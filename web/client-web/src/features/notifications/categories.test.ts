import { describe, expect, it } from 'vitest'
import {
  NOTIFICATION_CATEGORY_KEYS,
  filterNotificationsByCategory,
  notificationCategoryKey,
  notificationCategoryLabel,
} from './categories'

describe('notification categories', () => {
  it('pins the Q06 category keys used by clients', () => {
    expect(NOTIFICATION_CATEGORY_KEYS.notesSummaryReady).toBe('notes.summary_ready')
    expect(NOTIFICATION_CATEGORY_KEYS.notesTranscriptionAttention)
      .toBe('notes.transcription_attention')
    expect(NOTIFICATION_CATEGORY_KEYS.notesOrganizingReview)
      .toBe('notes.organizing_review')
    expect(NOTIFICATION_CATEGORY_KEYS.uploadsAttentionDigest)
      .toBe('uploads.attention_digest')
  })

  it('maps event types that have a precise Q06 category key', () => {
    expect(notificationCategoryKey('note.derived.completed')).toBe('notes.summary_ready')
    expect(notificationCategoryKey('note.transcription.attention'))
      .toBe('notes.transcription_attention')
    expect(notificationCategoryKey('note.derived.attention'))
      .toBe('notes.organizing_review')
    expect(notificationCategoryKey('upload.attention_digest'))
      .toBe('uploads.attention_digest')
  })

  it('keeps the event type as a stable key for categories without a Q06 alias', () => {
    expect(notificationCategoryKey('calendar.task.completed')).toBe('calendar.task.completed')
    expect(notificationCategoryLabel('calendar.task.completed')).toBe('任务完成')
    expect(notificationCategoryLabel('notes.transcription_attention')).toBe('转写需处理')
    expect(notificationCategoryLabel('unknown.category')).toBe('unknown.category')
  })

  it('filters notification items by category key without changing their order', () => {
    const items = [
      { id: 'a', type: 'note.derived.completed' },
      { id: 'b', type: 'note.transcription.attention' },
      { id: 'c', type: 'note.derived.completed' },
    ]
    expect(filterNotificationsByCategory(items, 'notes.summary_ready').map((item) => item.id))
      .toEqual(['a', 'c'])
    expect(filterNotificationsByCategory(items, null).map((item) => item.id))
      .toEqual(['a', 'b', 'c'])
    expect(filterNotificationsByCategory(items, 'uploads.attention_digest')).toEqual([])
  })
})
