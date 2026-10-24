import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deferredIntervalMs } from '@/lib/polling'
import { operationsApi } from './api'

export const operationsKeys = {
  pending: ['operations', 'confirmations', 'pending'] as const,
  detail: (id: string) => ['operations', 'confirmations', id] as const,
}

export function usePendingConfirmations() {
  return useQuery({
    queryKey: operationsKeys.pending,
    queryFn: operationsApi.listPending,
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useConfirmation(id: string | null) {
  return useQuery({
    queryKey: operationsKeys.detail(id ?? ''),
    queryFn: () => operationsApi.get(id!),
    enabled: id != null,
  })
}

/** 确认/拒绝后失效列表与详情 */
function useInvalidateConfirmations() {
  const qc = useQueryClient()
  return () => {
    void qc.invalidateQueries({ queryKey: ['operations', 'confirmations'] })
    void qc.invalidateQueries({ queryKey: ['today'] })
  }
}

export function useConfirmationActions() {
  const invalidate = useInvalidateConfirmations()
  const confirm = useMutation({ mutationFn: operationsApi.confirm, onSuccess: invalidate })
  const confirmSecondLevel = useMutation({ mutationFn: operationsApi.confirmSecondLevel, onSuccess: invalidate })
  const confirmStrict = useMutation({ mutationFn: operationsApi.confirmStrict, onSuccess: invalidate })
  const reject = useMutation({ mutationFn: operationsApi.reject, onSuccess: invalidate })
  return { confirm, confirmSecondLevel, confirmStrict, reject }
}
