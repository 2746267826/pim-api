import type { StoredAiDraftSummary } from '@/features/ai/note-derived/draft-storage'

export function serverLabel(binding: string): string {
  return binding || '当前部署源'
}

export function belongsToOtherServer(draftBinding: string, currentBinding: string): boolean {
  return Boolean(currentBinding && draftBinding && draftBinding !== currentBinding)
}

/**
 * 普通账号无法定位历史材料版本：链接携带来源笔记与草稿对应的整理结果版本，处理页据此
 * 同时选中当前材料与对应结果。旧草稿缺少 noteId 时降级为纯处理页，不猜测笔记。
 */
export function ordinaryDraftProcessingHref(draft: StoredAiDraftSummary): string {
  const { artifactVersionId, noteId } = draft.binding
  if (!noteId) return '/native-notes/processing'
  const query = new URLSearchParams({ noteId })
  if (artifactVersionId) query.set('artifactVersionId', artifactVersionId)
  return `/native-notes/processing?${query.toString()}`
}
