import { apiGet, apiPost } from '@/api/client'
import type { components } from '@/api/generated'

export type RecycleBinPage = components['schemas']['RecycleBinPageDto']
export type RecycleBinItem = components['schemas']['RecycleBinItemDto']
export type RecycleBinGroup = components['schemas']['RecycleBinGroupDto']
export type RecycleBinItemRef = components['schemas']['RecycleBinItemRef']
export type RecycleBinBatchRequest = components['schemas']['RecycleBinBatchRequest']
export type RecycleBinBatchReceipt = components['schemas']['RecycleBinBatchReceiptDto']
export type RecycleBinItemResult = components['schemas']['RecycleBinItemResultDto']

export interface RecycleBinListQuery {
  domain?: string
  search?: string
  deletedFrom?: string
  deletedTo?: string
  page?: number
  pageSize?: number
}

export const recycleBinApi = {
  list(query: RecycleBinListQuery = {}, signal?: AbortSignal) {
    const params = new URLSearchParams()
    if (query.domain && query.domain !== 'all') params.set('domain', query.domain)
    if (query.search?.trim()) params.set('search', query.search.trim())
    if (query.deletedFrom) params.set('deletedFrom', query.deletedFrom)
    if (query.deletedTo) params.set('deletedTo', query.deletedTo)
    params.set('page', String(query.page ?? 1))
    params.set('pageSize', String(query.pageSize ?? 50))
    return apiGet<RecycleBinPage>(`/api/v1/recycle-bin?${params.toString()}`, { signal })
  },

  restore(items: RecycleBinItemRef[], restoreAsCopy = false, signal?: AbortSignal) {
    const body: RecycleBinBatchRequest = {
      items,
      operationId: crypto.randomUUID(),
      restoreAsCopy,
    }
    return apiPost<RecycleBinBatchReceipt>('/api/v1/recycle-bin/restore', body, { signal })
  },

  purge(items: RecycleBinItemRef[], signal?: AbortSignal) {
    const body: RecycleBinBatchRequest = {
      items,
      operationId: crypto.randomUUID(),
      restoreAsCopy: false,
    }
    return apiPost<RecycleBinBatchReceipt>('/api/v1/recycle-bin/purge', body, { signal })
  },
}
