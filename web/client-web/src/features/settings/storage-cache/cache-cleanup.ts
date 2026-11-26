import type { QueryClient } from '@tanstack/react-query'
import {
  listStoredAiDrafts,
  canonicalServerBinding,
  type StoredAiDraftSummary,
} from '@/features/ai/note-derived/draft-storage'
import { uploadEngine } from '@/features/files/upload-engine'
import {
  clearObjectUrlCache,
  objectUrlCacheCounts,
} from '@/lib/object-url-cache'
import {
  listUnsavedDrafts,
  type UnsavedDraftDescriptor,
} from '@/lib/unsaved-drafts'

export interface TemporaryCacheSnapshot {
  readonly serverBinding: string
  readonly queryEntries: number
  readonly thumbnailEntries: number
  readonly playerEntries: number
  readonly transferHistoryEntries: number
  readonly unsavedDrafts: readonly UnsavedDraftDescriptor[]
  readonly aiDrafts: readonly StoredAiDraftSummary[]
}

export interface TemporaryCacheClearResult {
  readonly serverBinding: string
  readonly queryEntries: number
  readonly thumbnailEntries: number
  readonly playerEntries: number
  readonly transferHistoryEntries: number
  readonly preservedUnsavedDrafts: number
  readonly preservedAiDrafts: number
  readonly clearedAt: string
}

export function snapshotTemporaryCache(queryClient: QueryClient): TemporaryCacheSnapshot {
  const media = objectUrlCacheCounts()
  return {
    serverBinding: canonicalServerBinding(),
    queryEntries: queryClient.getQueryCache().getAll().length,
    thumbnailEntries: media.thumbnail,
    playerEntries: media.player,
    transferHistoryEntries: uploadEngine.historyCount(),
    unsavedDrafts: listUnsavedDrafts(),
    aiDrafts: listStoredAiDrafts(),
  }
}

/**
 * 普通 Web 临时缓存清理：
 * - 查询缓存（保留 mutation cache 与正在进行的保存/上传）
 * - 缩略图与播放器对象 URL
 * - 普通文件传输历史
 *
 * 不触碰：未保存编辑器状态、手写/图片草稿、AI 草稿、笔记索引/材料、令牌、服务器配置。
 * 清理是幂等的；第二次调用不会重复撤销 URL 或删除传输历史。
 */
export async function clearTemporaryWebCache(queryClient: QueryClient): Promise<TemporaryCacheClearResult> {
  const before = snapshotTemporaryCache(queryClient)
  await queryClient.cancelQueries()
  queryClient.removeQueries()
  const thumbnailEntries = clearObjectUrlCache('thumbnail')
  const playerEntries = clearObjectUrlCache('player')
  const transferHistoryEntries = uploadEngine.clearHistory()
  const preserved = snapshotTemporaryCache(queryClient)
  return {
    serverBinding: before.serverBinding,
    queryEntries: before.queryEntries,
    thumbnailEntries,
    playerEntries,
    transferHistoryEntries,
    preservedUnsavedDrafts: preserved.unsavedDrafts.length,
    preservedAiDrafts: preserved.aiDrafts.length,
    clearedAt: new Date().toISOString(),
  }
}
