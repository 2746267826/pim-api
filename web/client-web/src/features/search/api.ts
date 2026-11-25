import { apiGet, type FetchOptions } from '@/api/client'
import type { GlobalSearchParams, GlobalSearchResult } from './types'

export const searchApi = {
  query: (params: GlobalSearchParams, options?: FetchOptions) => {
    const query = new URLSearchParams({
      q: params.q,
      scope: params.scope,
      match: params.match,
      sort: params.sort,
      page: String(params.page),
      pageSize: String(params.pageSize ?? 20),
    })
    return apiGet<GlobalSearchResult>(`/api/v1/search?${query}`, options)
  },
}
