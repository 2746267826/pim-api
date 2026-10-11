import { apiDelete, apiGet, apiPost, apiPut, apiUpload } from '@/api/client'
import type { components } from '@/api/generated'
import type { PagedResult } from '@/api/types'
import type { QuickNoteAttachment, QuickNoteDetail, QuickNoteListItem } from './types'

export const quickNotesApi = {
  list: (params: { status?: string; search?: string; page?: number; pageSize?: number }) => {
    const q = new URLSearchParams()
    if (params.status && params.status !== 'all') q.set('status', params.status)
    if (params.search) q.set('search', params.search)
    q.set('page', String(params.page ?? 1))
    q.set('pageSize', String(params.pageSize ?? 50))
    return apiGet<PagedResult<QuickNoteListItem>>(`/api/v1/quick-notes?${q}`)
  },
  get: (id: string) => apiGet<QuickNoteDetail>(`/api/v1/quick-notes/${id}`),
  create: (body: components['schemas']['CreateQuickNoteRequest']) =>
    apiPost<QuickNoteDetail>('/api/v1/quick-notes', body),
  update: (id: string, body: components['schemas']['UpdateQuickNoteRequest']) =>
    apiPut<QuickNoteDetail>(`/api/v1/quick-notes/${id}`, body),
  /** 仅标记为已处理（规格：不要实现"触发 AI 处理"） */
  process: (id: string) => apiPost<QuickNoteDetail>(`/api/v1/quick-notes/${id}/process`, {}),
  archive: (id: string) => apiPost<QuickNoteDetail>(`/api/v1/quick-notes/${id}/archive`, {}),
  restore: (id: string) =>
    apiPost<QuickNoteDetail>(`/api/v1/quick-notes/${id}/restore`, { status: 'inbox' }),
  remove: (id: string) => apiDelete<string>(`/api/v1/quick-notes/${id}`),
  uploadAttachment: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiUpload<QuickNoteAttachment>('/api/v1/quick-notes/attachments', form)
  },
  removeAttachment: (id: string) => apiDelete<string>(`/api/v1/quick-notes/attachments/${id}`),
}
