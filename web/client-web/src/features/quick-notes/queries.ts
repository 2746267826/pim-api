import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { quickNotesApi } from './api'

export const quickNotesKeys = {
  list: (params: { status?: string; search?: string }) =>
    ['quick-notes', 'list', params] as const,
  detail: (id: string) => ['quick-notes', 'detail', id] as const,
}

export function useQuickNotes(params: { status?: string; search?: string }) {
  return useQuery({
    queryKey: quickNotesKeys.list(params),
    queryFn: () => quickNotesApi.list({ ...params, page: 1, pageSize: 50 }),
    placeholderData: (prev) => prev,
  })
}

function useInvalidateNotes() {
  const qc = useQueryClient()
  return () => void qc.invalidateQueries({ queryKey: ['quick-notes'] })
}

export function useQuickNoteMutations() {
  const invalidate = useInvalidateNotes()
  return {
    save: useMutation({
      mutationFn: async (input: { id?: string; contentMarkdown: string; attachmentIds?: string[]; source?: string }) =>
        input.id
          ? quickNotesApi.update(input.id, { contentMarkdown: input.contentMarkdown, attachmentIds: input.attachmentIds })
          : quickNotesApi.create({ contentMarkdown: input.contentMarkdown, attachmentIds: input.attachmentIds, source: input.source }),
      onSuccess: invalidate,
    }),
    process: useMutation({ mutationFn: quickNotesApi.process, onSuccess: invalidate }),
    archive: useMutation({ mutationFn: quickNotesApi.archive, onSuccess: invalidate }),
    restore: useMutation({ mutationFn: quickNotesApi.restore, onSuccess: invalidate }),
    remove: useMutation({ mutationFn: quickNotesApi.remove, onSuccess: invalidate }),
    upload: useMutation({ mutationFn: quickNotesApi.uploadAttachment }),
  }
}
