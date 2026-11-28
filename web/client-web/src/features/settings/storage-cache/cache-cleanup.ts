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
  readonly inUseQueryEntries: number
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

function isQueryInUse(query: { getObserversCount: () => number }): boolean {
  return query.getObserversCount() > 0
}

function queryEntryCounts(queryClient: QueryClient): { removable: number; inUse: number } {
  let removable = 0
  let inUse = 0
  for (const query of queryClient.getQueryCache().getAll()) {
    if (isQueryInUse(query)) inUse += 1
    else removable += 1
  }
  return { removable, inUse }
}

export function snapshotTemporaryCache(queryClient: QueryClient, ownerId = ''): TemporaryCacheSnapshot {
  const media = objectUrlCacheCounts()
  const queries = queryEntryCounts(queryClient)
  return {
    serverBinding: canonicalServerBinding(),
    queryEntries: queries.removable,
    inUseQueryEntries: queries.inUse,
    thumbnailEntries: media.thumbnail,
    playerEntries: media.player,
    transferHistoryEntries: uploadEngine.historyCount(),
    unsavedDrafts: listUnsavedDrafts(),
    aiDrafts: listStoredAiDrafts(ownerId),
  }
}

/**
 * 普通 Web 临时缓存清理：
 * - 查询缓存（保留 mutation cache 与正在进行的保存/上传）
 * - 缩略图与播放器对象 URL（仍挂载消费者使用的保留，释放归零后撤销）
 * - 普通文件传输历史
 *
 * 不触碰：未保存编辑器状态、手写/图片草稿、AI 草稿、笔记索引/材料、令牌、服务器配置。
 * 清理是幂等的；第二次调用不会重复撤销 URL 或删除传输历史。
 *
 * 只删除当前没有被任何组件观察的查询。仍在页面中的查询（例如笔记编辑器正在读取的
 * 材料版本）一旦被删除会让编辑器回到 pending 并卸载，内存中的未保存正文随之丢失。
 * 这里按 observer 数量而不是 isActive 判断：enabled=false 的查询仍可能被挂载页面用来
 * 渲染已有数据。
 */
export async function clearTemporaryWebCache(queryClient: QueryClient, ownerId = ''): Promise<TemporaryCacheClearResult> {
  const before = snapshotTemporaryCache(queryClient, ownerId)
  const candidates = queryClient.getQueryCache().getAll().filter((query) => !isQueryInUse(query))
  const candidateSet = new Set(candidates)
  await queryClient.cancelQueries({
    predicate: (query) => candidateSet.has(query) && !isQueryInUse(query),
  })
  queryClient.removeQueries({
    predicate: (query) => candidateSet.has(query) && !isQueryInUse(query),
  })
  const remaining = new Set(queryClient.getQueryCache().getAll())
  const queryEntries = candidates.filter((query) => !remaining.has(query)).length
  const thumbnailEntries = clearObjectUrlCache('thumbnail')
  const playerEntries = clearObjectUrlCache('player')
  const transferHistoryEntries = uploadEngine.clearHistory()
  const preserved = snapshotTemporaryCache(queryClient, ownerId)
  return {
    serverBinding: before.serverBinding,
    queryEntries,
    thumbnailEntries,
    playerEntries,
    transferHistoryEntries,
    preservedUnsavedDrafts: preserved.unsavedDrafts.length,
    preservedAiDrafts: preserved.aiDrafts.length,
    clearedAt: new Date().toISOString(),
  }
}
