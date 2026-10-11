import { apiDelete, apiFetch, apiGet, apiPost, apiPut, apiUpload } from '@/api/client'
import type { components } from '@/api/generated'
import type { PagedResult } from '@/api/types'
import type {
  FileItem,
  FileProvider,
  FileShare,
  FileTextSnapshot,
  OneDriveBindingStart,
  OneDriveBindingStatus,
  SyncStatus,
  UploadSession,
} from './types'

export const filesApi = {
  /* 提供程序绑定 */
  providers: () => apiGet<FileProvider[]>('/api/v1/files/providers'),
  bindOneDrive: (clientId: string) =>
    apiPost<OneDriveBindingStart>('/api/v1/files/providers/onedrive', { clientId }),
  bindingStatus: (id: string) =>
    apiGet<OneDriveBindingStatus>(`/api/v1/files/providers/${id}/binding-status`),
  unbind: (id: string) => apiDelete<boolean>(`/api/v1/files/providers/${id}`),
  startSync: (id: string) =>
    apiPost<{ started: boolean; message: string }>(`/api/v1/files/providers/${id}/sync`, {}),
  syncStatus: (id: string) => apiGet<SyncStatus>(`/api/v1/files/providers/${id}/sync-status`),

  /* 目录与条目 */
  listItems: (params: { path: string; page?: number; pageSize?: number; q?: string; sort?: string; order?: string; type?: string }) => {
    const q = new URLSearchParams({ path: params.path })
    if (params.page) q.set('page', String(params.page))
    q.set('pageSize', String(params.pageSize ?? 100))
    if (params.q) q.set('q', params.q)
    if (params.sort) q.set('sort', params.sort)
    if (params.order) q.set('order', params.order)
    if (params.type) q.set('type', params.type)
    return apiGet<{ result: PagedResult<FileItem> }>(`/api/v1/files/items?${q}`)
  },
  search: (q: string, page = 1) =>
    apiGet<{ items: FileItem[]; totalCount: number; totalPages: number }>(
      `/api/v1/files/search?q=${encodeURIComponent(q)}&mode=keyword&page=${page}&pageSize=100`,
    ),
  folderTreeChildren: (path: string) => filesApi.listItems({ path, pageSize: 100, type: 'folder' }),

  /* 上传（双通道） */
  uploadSimple: (providerId: string, path: string, file: File) => {
    const form = new FormData()
    form.append('providerId', providerId)
    form.append('path', path)
    form.append('file', file)
    return apiUpload<FileItem>('/api/v1/files/items/upload', form)
  },
  createUploadSession: (path: string, fileName: string) =>
    apiPost<UploadSession>('/api/v1/files/items/upload-session', { path, fileName }),
  completeUploadSession: (path: string, fileName: string, uploadedItemId?: string) =>
    apiPost<FileItem>('/api/v1/files/items/upload-session/complete', { path, fileName, uploadedItemId }),

  /* 整理 */
  createFolder: (path: string) => apiPost<FileItem>('/api/v1/files/folders', { path }),
  move: (id: string, destinationPath: string) =>
    apiPost<FileItem>(`/api/v1/files/items/${id}/move`, { destinationPath }),
  rename: (id: string, name: string) => apiPost<FileItem>(`/api/v1/files/items/${id}/rename`, { name }),
  remove: (id: string) => apiDelete<string>(`/api/v1/files/items/${id}`),

  /* 下载与预览 */
  downloadUrl: (id: string) => apiGet<{ url: string }>(`/api/v1/files/items/${id}/download-url`),
  previewUrl: (id: string) => apiGet<{ url: string }>(`/api/v1/files/items/${id}/preview-url`),
  // 契约该 operation 只声明 path 参数 id（pim-api FilesModule.cs:596-600 的
  // BuildOpenLinkAsync(Guid id, …) 也不接收 query）—— 停发契约外的 ?mode=view。
  // 响应类型里的 mode 是服务端返回字段，保留。
  openLink: (id: string) => apiGet<{ url: string; mode: string }>(`/api/v1/files/items/${id}/open-link`),
  readText: (id: string) =>
    apiGet<{ content: string; mimeType: string | null; size: number; truncated: boolean }>(
      `/api/v1/files/items/${id}/text`,
    ),
  saveText: (id: string, content: string) =>
    apiPut<boolean>(`/api/v1/files/items/${id}/text`, { content }),
  snapshots: (id: string) => apiGet<FileTextSnapshot[]>(`/api/v1/files/items/${id}/snapshots`),
  restoreSnapshot: (id: string, snapshotId: string) =>
    apiPost<boolean>(`/api/v1/files/items/${id}/snapshots/${snapshotId}/restore`, {}),

  /* 分享 */
  share: (id: string, body: components['schemas']['CreateShareRequest']) =>
    apiPost<FileShare>(`/api/v1/files/items/${id}/share`, body),
  shares: (id: string) => apiGet<FileShare[]>(`/api/v1/files/items/${id}/shares`),
  revokeShare: (id: string, permissionId: string) =>
    apiDelete<boolean>(`/api/v1/files/items/${id}/shares/${encodeURIComponent(permissionId)}`),
  allShares: () => apiGet<FileShare[]>('/api/v1/files/shares?limit=50'),
}

export { apiFetch }
