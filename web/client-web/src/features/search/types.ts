import type { components } from '@/api/generated'

export type SearchDomain = 'note' | 'task' | 'event'
export type SearchScope = 'all' | SearchDomain
export type SearchMatch = 'all' | 'any'
export type SearchSort = 'relevance' | 'recent_edited' | 'recent_created'

export type SearchFieldLocation = components['schemas']['SearchFieldLocation']
export type SearchEventOccurrence = components['schemas']['SearchEventOccurrence']
export type SearchGroupFailure = components['schemas']['SearchGroupFailure']

export type SearchResult = Omit<components['schemas']['SearchResult'], 'domain'> & {
  domain: SearchDomain
}

export type SearchGroup = Omit<
  components['schemas']['SearchGroup'],
  'domain' | 'items'
> & {
  domain: SearchDomain
  items: SearchResult[]
}

export type GlobalSearchResult = Omit<
  components['schemas']['GlobalSearchResult'],
  'groups' | 'items'
> & {
  groups: SearchGroup[]
  items: SearchResult[]
}

export interface GlobalSearchParams {
  q: string
  scope: SearchScope
  match: SearchMatch
  sort: SearchSort
  page: number
  pageSize?: number
}
