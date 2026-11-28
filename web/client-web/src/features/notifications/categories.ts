/**
 * Q06 精确通知分类键（与 API `NotificationCategoryKeys` 保持一致）。
 * 客户端用分类键做筛选/文案，不依赖内部 eventType 名称。
 */
export const NOTIFICATION_CATEGORY_KEYS = {
  notesSummaryReady: 'notes.summary_ready',
  notesTranscriptionAttention: 'notes.transcription_attention',
  notesOrganizingReview: 'notes.organizing_review',
  uploadsAttentionDigest: 'uploads.attention_digest',
} as const

export type NotificationCategoryKey =
  (typeof NOTIFICATION_CATEGORY_KEYS)[keyof typeof NOTIFICATION_CATEGORY_KEYS]

const EVENT_TYPE_CATEGORY: Record<string, NotificationCategoryKey> = {
  'note.derived.completed': NOTIFICATION_CATEGORY_KEYS.notesSummaryReady,
  'note.transcription.attention': NOTIFICATION_CATEGORY_KEYS.notesTranscriptionAttention,
  'note.derived.attention': NOTIFICATION_CATEGORY_KEYS.notesOrganizingReview,
  'upload.attention_digest': NOTIFICATION_CATEGORY_KEYS.uploadsAttentionDigest,
}

const CATEGORY_LABELS: Record<string, string> = {
  'calendar.task.completed': '任务完成',
  'calendar.reminder.due': '日历提醒',
  'note.transcription.completed': '转写完成',
  'note.derived.completed': '整理完成',
  'notification.test': '测试通知',
  [NOTIFICATION_CATEGORY_KEYS.notesSummaryReady]: '整理完成',
  [NOTIFICATION_CATEGORY_KEYS.notesTranscriptionAttention]: '转写需处理',
  [NOTIFICATION_CATEGORY_KEYS.notesOrganizingReview]: '整理需复核',
  [NOTIFICATION_CATEGORY_KEYS.uploadsAttentionDigest]: '上传需关注',
}

/** eventType → 稳定分类键；没有 Q06 别名的类别沿用 eventType。 */
export function notificationCategoryKey(eventType: string): string {
  return EVENT_TYPE_CATEGORY[eventType] ?? eventType
}

/** 分类键优先用于展示；未知值原样返回，便于排障。 */
export function notificationCategoryLabel(categoryKey: string): string {
  return CATEGORY_LABELS[categoryKey] ?? categoryKey
}

/** 按分类键筛选通知列表；`null` 表示不筛选，顺序保持不变。 */
export function filterNotificationsByCategory<T extends { type: string }>(
  items: readonly T[],
  categoryKey: string | null,
): T[] {
  if (categoryKey === null) return [...items]
  return items.filter((item) => notificationCategoryKey(item.type) === categoryKey)
}
