import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/features/auth/auth-context'
import { useApiBaseBinding } from '@/lib/use-api-base-binding'
import { searchApi } from './api'
import type { GlobalSearchParams } from './types'

export const searchKeys = {
  global: (ownerId: string, serverBinding: string, params: GlobalSearchParams) =>
    ['search', 'global', ownerId, serverBinding, params] as const,
}

export interface SearchIdentity {
  ownerId: string
  serverBinding: string
}

export function useSearchIdentity(): SearchIdentity {
  const { user } = useAuth()
  const serverBinding = useApiBaseBinding()

  return useMemo(
    () => ({ ownerId: user?.id ?? '', serverBinding }),
    [serverBinding, user?.id],
  )
}

export function useGlobalSearch(params: GlobalSearchParams, identity: SearchIdentity) {
  return useQuery({
    queryKey: searchKeys.global(identity.ownerId, identity.serverBinding, params),
    queryFn: ({ signal }) => searchApi.query(params, { signal }),
    enabled: identity.ownerId.length > 0 && params.q.trim().length > 0,
  })
}
